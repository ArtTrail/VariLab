using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace VariLab.Services;

public sealed record PsfEngineRuntime(string PythonExePath, string ScriptPath);

/// <summary>
/// Resolves whether the PSF-fit engine is ready to run: an isolated venv (fixed location,
/// %AppData%\VariLab\pyenv — one venv total, unlike TransitLab's per-EXOTIC-environment
/// venvs, since there's no "stable vs pre-release" concept for a script that ships with the
/// app itself) with astropy/photutils actually importable, plus the bundled psf_engine.py
/// script sitting next to the running executable.
/// </summary>
public static class PsfEngineRuntimeService
{
    public static string VenvDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VariLab", "pyenv");

    public static string VenvPythonExe => PsfEngineInstallService.GetVenvPythonExe(VenvDir);

    public static string ScriptPath =>
        Path.Combine(AppContext.BaseDirectory, "PsfEngine", "psf_engine.py");

    public static string RequirementsPath =>
        Path.Combine(AppContext.BaseDirectory, "PsfEngine", "requirements.txt");

    /// <summary>Returns the runtime if the venv exists and astropy/photutils import
    /// cleanly, or null if setup hasn't happened (or needs to be redone) yet.</summary>
    public static async Task<PsfEngineRuntime?> ResolveAsync(CancellationToken ct = default)
    {
        if (!File.Exists(ScriptPath)) return null;
        if (!File.Exists(VenvPythonExe)) return null;
        if (!await PsfEngineInstallService.CanRunPsfEngineAsync(VenvPythonExe, ct)) return null;
        return new PsfEngineRuntime(VenvPythonExe, ScriptPath);
    }
}
