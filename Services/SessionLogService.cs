using System;
using System.IO;
using System.Linq;

namespace VariLab.Services;

/// <summary>
/// Writes a timestamped session log to %AppData%\VariLab\logs\.
/// Keeps the five most recent sessions; older files are deleted at startup.
/// All writes are best-effort — exceptions are silently swallowed.
/// </summary>
public static class SessionLogService
{
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VariLab", "logs");

    private static string _logPath = "";

    public static string CurrentLogPath => _logPath;

    public static void Initialize(string appVersion)
    {
        try
        {
            Directory.CreateDirectory(LogDir);

            // Delete all but the 5 most recent prior sessions
            var old = Directory.GetFiles(LogDir, "VariLab_diagnostics_*.log")
                               .OrderByDescending(f => f)
                               .Skip(5)
                               .ToArray();
            foreach (var f in old)
                try { File.Delete(f); } catch { }

            _logPath = Path.Combine(LogDir, $"VariLab_diagnostics_{DateTime.Now:yyyyMMdd_HHmmss}.log");
        }
        catch { return; }

        Write($"=== VariLab {appVersion} — Session started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
        Write("");
    }

    /// <summary>
    /// Fired on the calling thread whenever a new line is written.
    /// Subscribers are responsible for dispatching to the UI thread if needed.
    /// </summary>
    public static event Action<string>? LineWritten;

    /// <summary>
    /// Writes a single line with a [HH:mm:ss] prefix.
    /// Pass an empty string to write a blank separator line.
    /// </summary>
    public static void Write(string message)
    {
        if (string.IsNullOrEmpty(_logPath)) return;
        var line = string.IsNullOrEmpty(message)
            ? ""
            : $"[{DateTime.Now:HH:mm:ss}]  {message}";
        try { File.AppendAllText(_logPath, line + Environment.NewLine); }
        catch { }
        LineWritten?.Invoke(line);
    }

    /// <summary>Returns the full text of the current session log.</summary>
    public static string ReadAll()
    {
        if (string.IsNullOrEmpty(_logPath) || !File.Exists(_logPath)) return "";
        try { return File.ReadAllText(_logPath); }
        catch { return ""; }
    }
}
