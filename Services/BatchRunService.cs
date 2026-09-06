using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VariLab.ViewModels;

namespace VariLab.Services;

/// <summary>
/// Drives the full Data -&gt; Comp Stars -&gt; Photometry -&gt; Results pipeline across a list of
/// targets, one at a time, for the Batch Process window. Deliberately does NOT reimplement any
/// of that pipeline — it constructs the exact same ViewModel graph MainWindowViewModel already
/// wires up (no View/Window needed, since none of those ViewModels have any View dependency) and
/// invokes the same RunCommand a button click already calls. Every success/failure already lands
/// on disk exactly the way a manual run's does, via the existing VariLab_Results_.../
/// VariLab_FAILED_... mechanism (FailureReportService) built into those ViewModels — this service
/// only adds progress narration and the one Data-tab-level failure case
/// (LookUpCoordinates isn't itself wired to FailureReportService, since a failed lookup during
/// manual use isn't a dead end the way it is here) and per-target subfolder creation.
/// </summary>
public static class BatchRunService
{
    /// <summary>Replaces characters invalid in a Windows folder name — target names are used
    /// as-is for the per-target subfolder, no assumption about their format (unlike this
    /// project's own "V{N} Cen" convention, kept generic on purpose).</summary>
    private static string SanitizeForPath(string s) =>
        string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    public static async Task RunAsync(
        string inputDirectory,
        string resultsBaseDirectory,
        IReadOnlyList<string> targetNames,
        bool runAperture,
        bool runPsfFit,
        string filterCode,
        string? aavsoObserverCode,
        int maxCompStars,
        IProgress<string> progress,
        CancellationToken ct,
        Func<string, Task>? onPlotProduced = null)
    {
        var data       = new DataViewModel();
        var compStars  = new CompStarsViewModel(data);
        var photometry = new PhotometryViewModel(data, compStars);
        var results    = new ResultsViewModel(data, compStars, photometry);

        // Results is run explicitly per-mode in the per-target loop below (via
        // RunResultsAndMaybePopupAsync) instead of through photometry.Completed, so each
        // mode's own onPlotProduced check (issue #10) can run right after that mode's export
        // finishes — Completed is a plain synchronous Action with no way to await a callback
        // from it. ResultsViewModel.Run() is itself synchronous (RelayCommand, not
        // AsyncRelayCommand), so Execute(null) already blocks until AutoExportAll is done;
        // there's no race to worry about either way.

        // Without this, Cancel only ever took effect at the next target boundary (the
        // ThrowIfCancellationRequested() below) — each ViewModel's Run() spins up its own
        // private CancellationTokenSource with no link to this method's ct, so cancelling a
        // batch mid-target (e.g. partway through a 900-frame PSF Fit run) previously did
        // nothing until that whole target finished. Linking both here makes Cancel interrupt
        // whichever stage is actually in flight.
        compStars.ExternalCancellationToken  = ct;
        photometry.ExternalCancellationToken = ct;

        // Every relayed/reported line is prefixed with whichever target the loop below is
        // currently on — once the "=== {targetName} ===" header line scrolls out of view (this
        // fires many times per second during PSF Fit), there'd otherwise be no way to tell which
        // target a given progress line belongs to.
        var currentTarget = "";

        // CompStarsViewModel.Run()/PhotometryViewModel.Run() already update their own Status
        // property live (frame-by-frame for PSF Fit) — that's what the Comp Stars/Photometry
        // tabs show in real time. Relaying every Status change into progress.Report here gives
        // the Batch window the same live view instead of going quiet for the full duration of
        // each stage and only reporting once it's completely finished.
        compStars.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CompStarsViewModel.Status))
                progress.Report($"  {currentTarget}  Comp Stars: {compStars.Status}");
        };
        photometry.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PhotometryViewModel.Status))
                progress.Report($"  {currentTarget}  {(photometry.IsPsfFit ? "PSF Fit" : "Aperture")}: {photometry.Status}");
        };

        // No interactive owner for a headless run — always continue past a VSP/Gaia/APASS
        // source failure with whatever did respond, matching the choice made by hand for every
        // one of these prompts during manual batch processing. NotifyPlateSolveRequired/
        // NotifyTargetNotInFrame/NotifyTargetOffFrame are left unwired on purpose: each is only
        // invoked "if not null" and has no real "continue anyway" option to offer even when a
        // human is watching, so leaving them null already produces the correct headless
        // behavior (straight through to the existing failure-reporting path).
        compStars.ConfirmOnSourceFailure = (_, _) => Task.FromResult(true);

        data.FilterCode        = filterCode;
        data.AavsoObserverCode = aavsoObserverCode ?? "";
        compStars.MaxCompStars = maxCompStars;

        // Setting InputDirectory (both here and inside DataViewModel's own constructor, which
        // restores the last-used directory from config) triggers a fire-and-forget auto-detect
        // of Target Name + a lookup, from whatever OBJECT header the first FITS frame happens to
        // have — stale, and not awaited by DataViewModel itself. Left to race against this
        // service's own per-target lookups, it could occasionally clobber the first target's
        // resolved RA/Dec with garbage. Waiting for it to settle once, up front, costs a few
        // seconds total for the whole batch and avoids the race entirely.
        data.InputDirectory = inputDirectory;
        await Task.Delay(500, ct);
        var waited = 0;
        while (data.IsLookingUp && waited < 15000)
        {
            await Task.Delay(250, ct);
            waited += 250;
        }

        // AutoDetectFromFitsAsync (fired by the InputDirectory setter above) reads the first
        // FITS frame's own FILTER header and can overwrite FilterCode with a value that isn't
        // one of DataViewModel's known FilterOptions (MatchFilterOption falls through to the
        // raw header string on no match) — and it does this before IsLookingUp even goes true,
        // so the wait loop above doesn't reliably guard it. Re-assert the user's chosen filter
        // now that any auto-detect has had time to run, so it always wins as the final value.
        data.FilterCode = filterCode;

        foreach (var rawName in targetNames)
        {
            ct.ThrowIfCancellationRequested();
            var targetName = rawName.Trim();
            if (string.IsNullOrWhiteSpace(targetName)) continue;
            currentTarget = targetName;

            progress.Report($"=== {targetName} ===");

            var targetDir = Path.Combine(resultsBaseDirectory, SanitizeForPath(targetName));
            Directory.CreateDirectory(targetDir);
            data.OutputDirectory = targetDir;
            data.OutputDirUserSet = true;

            data.TargetName = targetName;
            await data.LookUpCoordinatesCommand.ExecuteAsync(null);
            if (!data.TargetResolved)
            {
                progress.Report($"  lookup failed: {data.Status}");
                FailureReportService.Write(targetDir, targetName, "N/A", inputDirectory, "Data", data.Status);
                continue;
            }
            progress.Report($"  {data.Status}");

            await compStars.RunCommand.ExecuteAsync(null);
            if (compStars.Stars.Count == 0)
            {
                progress.Report("  skipped photometry: no usable comp stars");
                continue;
            }

            // IsAperture/IsPsfFit stopped being mutually exclusive when the interactive tab
            // gained "run both" support (issue #9) — Batch still wants exactly one mode per
            // RunCommand call, so both flags are set explicitly every time rather than relying
            // on the old auto-reset-the-other-one behavior that no longer exists.
            if (runAperture)
            {
                photometry.IsAperture = true;
                photometry.IsPsfFit   = false;
                await photometry.RunCommand.ExecuteAsync(null);
                await RunResultsAndMaybePopupAsync(results, onPlotProduced);
            }
            if (runPsfFit)
            {
                photometry.IsAperture = false;
                photometry.IsPsfFit   = true;
                await photometry.RunCommand.ExecuteAsync(null);
                await RunResultsAndMaybePopupAsync(results, onPlotProduced);
            }

            progress.Report($"=== {targetName} done ===");
        }
    }

    /// <summary>Awaits the Results export for whichever mode just finished, then invokes
    /// onPlotProduced (issue #10, gated behind BatchViewModel's "Pop up the results plot"
    /// checkbox) only if a genuinely new PNG came out of it — AutoExportAll resets
    /// LastVariabilityPngPath back to its previous value on its own failure path, and leaves
    /// it untouched entirely if there were no points to export at all, so a plain null-check
    /// isn't enough to avoid re-showing a stale plot from an earlier target/mode.</summary>
    private static async Task RunResultsAndMaybePopupAsync(ResultsViewModel results, Func<string, Task>? onPlotProduced)
    {
        // ResultsViewModel.Run() is a plain synchronous void method (RelayCommand, not
        // AsyncRelayCommand — it has no ExecuteAsync), so Execute(null) already blocks until
        // AutoExportAll fully finishes; no race with the popup check below.
        var pngBefore = results.LastVariabilityPngPath;
        results.RunCommand.Execute(null);
        if (onPlotProduced is not null &&
            results.LastVariabilityPngPath is { } pngAfter &&
            pngAfter != pngBefore)
        {
            await onPlotProduced(pngAfter);
        }
    }
}
