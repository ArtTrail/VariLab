using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VariLab.Services;

public record PythonInfo(string Version, string ExePath)
{
    /// <summary>PSF-fit engine dependencies (astropy>=7.2, photutils>=2.3) require
    /// Python >=3.11 — confirmed directly: installing against 3.10 failed with
    /// "No matching distribution found for astropy==7.2".</summary>
    public static bool IsCompatible(string version)
    {
        var parts = version.Split('.');
        return parts.Length >= 2 &&
               int.TryParse(parts[0], out var maj) &&
               int.TryParse(parts[1], out var min) &&
               maj == 3 && min >= 11;
    }
}

/// <summary>
/// Finds/validates a Python 3.11+ interpreter and manages the isolated venv the PSF-fit
/// engine (PsfEngine/psf_engine.py, bundled with the app) runs in. Adapted from TransitLab's
/// ExoticInstallService.cs, trimmed to what this engine actually needs: unlike EXOTIC, the
/// engine ships as part of VariLab itself (not a separate PyPI package), so there's no
/// package-install/uninstall/branch-switching machinery here — just "find a compatible
/// Python" and "pip install this bundled requirements.txt into one venv."
///
/// Deliberately does NOT auto-install Python itself (TransitLab's ExoticInstallService does,
/// via a silent MSI download) — that's a large, Windows-installer-specific subsystem that
/// isn't needed for the feature to work, just for convenience. If Python isn't found, the
/// setup flow directs the user to install Python 3.11+ from python.org themselves.
/// </summary>
public static class PsfEngineInstallService
{
    public const string PyDownloadUrl = "https://www.python.org/downloads/";

    // ── Python detection ──────────────────────────────────────────────────────

    public static async Task<PythonInfo?> FindPythonAsync(CancellationToken ct = default)
    {
        var fromLauncher = await FindViaPyLauncherAsync(ct);
        if (fromLauncher != null) return fromLauncher;

        if (OperatingSystem.IsMacOS())
        {
            var macosPaths = new[]
            {
                "/Library/Frameworks/Python.framework/Versions/3.13/bin/python3.13",
                "/opt/homebrew/bin/python3.13",
                "/Library/Frameworks/Python.framework/Versions/3.12/bin/python3.12",
                "/opt/homebrew/bin/python3.12",
                "/Library/Frameworks/Python.framework/Versions/3.11/bin/python3.11",
                "/usr/local/bin/python3.11",
                "/opt/homebrew/bin/python3.11",
            };
            foreach (var absPath in macosPaths)
            {
                if (!File.Exists(absPath)) continue;
                var p = await ProbeExeAsync(absPath, ct);
                if (p != null) return p;
            }
        }

        foreach (var cmd in new[] { "python3.13", "python3.12", "python3.11", "python3", "python" })
        {
            var p = await ProbeExeAsync(cmd, ct);
            if (p != null) return p;
        }

        return ScanCommonPaths();
    }

    private static async Task<PythonInfo?> FindViaPyLauncherAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var output = await RunCaptureAsync("py", "-0p", ct);
            foreach (var line in output.Split('\n'))
            {
                var m = Regex.Match(line.Trim(),
                    @"-V:(\d+\.\d+)\s+\*?\s+(.+python\.exe)", RegexOptions.IgnoreCase);
                if (!m.Success) continue;
                var verShort = m.Groups[1].Value;
                var path     = m.Groups[2].Value.Trim();
                if (!File.Exists(path)) continue;
                var full = await GetVersionAsync(path, ct) ?? verShort;
                if (PythonInfo.IsCompatible(full)) return new PythonInfo(full, path);
            }
        }
        catch { }
        return null;
    }

    private static async Task<PythonInfo?> ProbeExeAsync(string exe, CancellationToken ct)
    {
        try
        {
            var ver = await GetVersionAsync(exe, ct);
            if (ver == null || !PythonInfo.IsCompatible(ver)) return null;
            var path = (await RunCaptureAsync(exe, "-c \"import sys; print(sys.executable)\"", ct)).Trim();
            return File.Exists(path) ? new PythonInfo(ver, path) : null;
        }
        catch { return null; }
    }

    private static async Task<string?> GetVersionAsync(string exe, CancellationToken ct)
    {
        try
        {
            var out1 = await RunCaptureAsync(exe, "--version", ct);
            var m = Regex.Match(out1, @"Python (\d+\.\d+\.\d+)");
            return m.Success ? m.Groups[1].Value : null;
        }
        catch { return null; }
    }

    private static PythonInfo? ScanCommonPaths()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roots = new[]
        {
            Path.Combine(local, "Programs", "Python"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Python313"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Python312"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Python311"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "anaconda3"),
        };
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            var dirs = new[] { root }.Concat(
                Directory.GetDirectories(root, "Python3*", SearchOption.TopDirectoryOnly));
            foreach (var dir in dirs)
            {
                var exe = Path.Combine(dir, "python.exe");
                if (!File.Exists(exe)) continue;
                var dm = Regex.Match(Path.GetFileName(dir), @"Python(\d)(\d+)");
                var ver = dm.Success ? $"{dm.Groups[1].Value}.{dm.Groups[2].Value}" : "3.13";
                if (PythonInfo.IsCompatible(ver)) return new PythonInfo(ver, exe);
            }
        }
        return null;
    }

    // ── Validation ───────────────────────────────────────────────────────────

    public static async Task<bool> ValidatePythonAsync(string exePath, CancellationToken ct = default)
    {
        try
        {
            var result = await RunCaptureAsync(exePath, "-c \"import encodings, os; print('ok')\"", ct);
            return result.Trim() == "ok";
        }
        catch { return false; }
    }

    // ── Venv ─────────────────────────────────────────────────────────────────

    public static async Task CreateVenvAsync(string basePythonExe, string destPath, IProgress<string>? log, CancellationToken ct)
    {
        Report(log, $"Creating environment at {destPath}…");
        Directory.CreateDirectory(Path.GetDirectoryName(destPath) ?? ".");
        await RunStreamAsync(basePythonExe, $"-m venv \"{destPath}\"", log, ct);
    }

    public static string GetVenvPythonExe(string venvPath) =>
        OperatingSystem.IsWindows()
            ? Path.Combine(venvPath, "Scripts", "python.exe")
            : Path.Combine(venvPath, "bin", "python");

    // ── Install engine dependencies ─────────────────────────────────────────

    /// <summary>pip installs the bundled PsfEngine/requirements.txt (astropy, photutils,
    /// numpy, scipy — no matplotlib/tkinter/pandas/etc., since the engine is headless and
    /// VariLab already does its own Gaia/APASS querying in C#) into the given venv.</summary>
    public static async Task InstallDepsAsync(string venvPythonExe, string requirementsPath, IProgress<string>? log, CancellationToken ct)
    {
        Report(log, "Upgrading pip…");
        await RunStreamAsync(venvPythonExe, "-m pip install --upgrade pip --no-warn-script-location", log, ct);

        Report(log, "\nInstalling PSF-fit engine dependencies (astropy, photutils, numpy, scipy)…");
        Report(log, "This may take a few minutes on first setup.\n");
        await RunStreamAsync(venvPythonExe, $"-m pip install -r \"{requirementsPath}\" --no-warn-script-location", log, ct);
    }

    /// <summary>Confirms astropy and photutils are actually importable in the given
    /// interpreter — distinct from a successful pip exit code, which doesn't guarantee
    /// the packages actually import cleanly afterward.</summary>
    public static async Task<bool> CanRunPsfEngineAsync(string pythonExe, CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo(pythonExe,
                "-c \"import importlib.util,sys; sys.exit(0 if importlib.util.find_spec('astropy') and importlib.util.find_spec('photutils') else 1)\"")
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false,       CreateNoWindow        = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            await proc.WaitForExitAsync(ct);
            return proc.ExitCode == 0;
        }
        catch { return false; }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void Report(IProgress<string>? log, string message)
    {
        if (log is not null)
            log.Report(message);
        else
            SessionLogService.Write($"[PsfEngineSetup] {message}");
    }

    private static async Task RunStreamAsync(string exe, string args, IProgress<string>? log, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false,       CreateNoWindow        = true,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Cannot start {exe}.");
        var outTask = DrainAsync(proc.StandardOutput, log, ct);
        var errTask = DrainAsync(proc.StandardError,  log, ct);
        await Task.WhenAll(outTask, errTask);
        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(exe)} exited with code {proc.ExitCode}.");
    }

    private static async Task DrainAsync(StreamReader reader, IProgress<string>? log, CancellationToken ct)
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync(ct)) is not null)
            {
                var clean = line.TrimEnd('\r', ' ');
                if (clean.Length == 0) continue;
                Report(log, clean);
            }
        }
        catch (OperationCanceledException) { }
    }

    private static async Task<string> RunCaptureAsync(string exe, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false,       CreateNoWindow        = true,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Cannot start {exe}.");
        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        return stdout;
    }
}
