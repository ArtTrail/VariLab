using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace VariLab.Services;

/// <summary>
/// Persists why a photometry attempt failed, per target/method, directly alongside where
/// VariLab_Results_... folders go — the only place any record of a run currently exists at
/// all. Before this, every failure branch across the Comp Stars/Photometry/Results tabs
/// already computed a specific, human-readable reason (Status messages, per-star/per-frame
/// rejection reasons) but only ever held it in memory; the moment the user moved to the next
/// target, it was gone, and the only visible trace of the whole attempt was the absence of a
/// results folder. This makes those existing reasons durable instead of inventing new ones.
///
/// Best-effort like SessionLogService — every write is wrapped so a failure to *record* a
/// failure can never itself interrupt the run that's already failing.
/// </summary>
public static class FailureReportService
{
    private static string SanitizeForPath(string s) =>
        string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    /// <summary>Same "UT date shifted back 12h" convention ResultsViewModel.ObsDateStamp uses
    /// for successful runs, so a failed and a successful attempt on the same observing night
    /// land on the same ObsDate stamp — derived here directly from a FITS DATE-OBS string
    /// rather than a JD, since a failure can happen before any frame is ever accepted.</summary>
    private static string ObsDateStamp(DateTime utc) => utc.AddHours(-12).ToString("yyyyMMdd");

    private static string DetermineObsDate(string inputDirectory)
    {
        try
        {
            var path = FitsHeaderService.FindFirstFits(inputDirectory);
            if (path is not null)
            {
                var dateObs = FitsHeaderService.Read(path).Get("DATE-OBS");
                if (!string.IsNullOrEmpty(dateObs) &&
                    DateTime.TryParse(dateObs, null,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
                    return ObsDateStamp(dt);
            }
        }
        catch { /* fall through to today's date below */ }
        return ObsDateStamp(DateTime.UtcNow);
    }

    /// <summary>Finds the next unused "VariLab_FAILED_{Target}_{ObsDate}_{Mode}_N" subfolder,
    /// mirroring ResultsViewModel.GetNextResultsDir's naming/disambiguation but with a prefix
    /// that can never be mistaken for a real VariLab_Results_... folder at a glance.</summary>
    private static string GetNextFailureDir(string baseDir, string targetName, string obsDate, string modeTag)
    {
        var safeName = SanitizeForPath(targetName);
        var prefix   = $"VariLab_FAILED_{safeName}_{obsDate}_{modeTag}_";

        int n = 1;
        if (Directory.Exists(baseDir))
        {
            var existing = Directory.GetDirectories(baseDir)
                .Select(Path.GetFileName)
                .Select(fname => Regex.Match(fname ?? "", $"^{Regex.Escape(prefix)}(\\d+)$"))
                .Where(m => m.Success)
                .Select(m => int.Parse(m.Groups[1].Value))
                .ToList();
            if (existing.Count > 0)
                n = existing.Max() + 1;
        }

        return Path.Combine(baseDir, $"{prefix}{n}");
    }

    /// <summary>Writes failure_reason.txt into a new VariLab_FAILED_... subfolder of
    /// outputOrInputDir. inputDirectory is used only to find a representative FITS frame for
    /// the ObsDate stamp — it may be the same path as outputOrInputDir. modeTag is normally
    /// "Aperture" or "PsfFit", but a failure at the Comp Stars stage blocks both methods
    /// equally (neither has run yet), so that call site passes "CompStars" instead of
    /// guessing/misattributing a specific mode that was never actually responsible.</summary>
    public static void Write(string outputOrInputDir, string targetName, string modeTag,
        string inputDirectory, string stage, string reason)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(outputOrInputDir)) return;
            Directory.CreateDirectory(outputOrInputDir);

            var obsDate  = DetermineObsDate(inputDirectory);
            var dir      = GetNextFailureDir(outputOrInputDir, targetName, obsDate, modeTag);
            Directory.CreateDirectory(dir);

            var text = $"Target:    {targetName}{Environment.NewLine}" +
                       $"Method:    {modeTag}{Environment.NewLine}" +
                       $"Stage:     {stage}{Environment.NewLine}" +
                       $"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}" +
                       $"{Environment.NewLine}Reason:{Environment.NewLine}{reason}{Environment.NewLine}";

            File.WriteAllText(Path.Combine(dir, "failure_reason.txt"), text);
        }
        catch { /* best-effort — never let failure reporting itself throw */ }
    }
}
