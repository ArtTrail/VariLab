using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VariLab.Services;

namespace VariLab.ViewModels;

/// <summary>
/// Photometry tab — runs multi-frame differential photometry (target + Stone
/// ensemble comps) across every frame in the input directory, sigma-clips
/// outlier frames, and combines comps into a weighted-median light curve.
/// </summary>
public partial class PhotometryViewModel : ViewModelBase
{
    private readonly DataViewModel      _data;
    private readonly CompStarsViewModel _compStars;
    private CancellationTokenSource?    _cts;

    /// <summary>Set by BatchRunService so its own Cancel button can interrupt whichever target
    /// is currently running here, not just take effect at the next target boundary — see the
    /// matching property on CompStarsViewModel for the full rationale.</summary>
    public CancellationToken? ExternalCancellationToken { get; set; }

    public PhotometryViewModel(DataViewModel data, CompStarsViewModel compStars)
    {
        _data      = data;
        _compStars = compStars;
    }

    [ObservableProperty] private bool   _isRunning = false;
    [ObservableProperty] private string _status    = "Not run yet.";
    [ObservableProperty] private string _summary   = "";
    [ObservableProperty] private double _apertureRadiusPx;
    [ObservableProperty] private double _annulusInnerPx;
    [ObservableProperty] private double _annulusOuterPx;

    // Aperture (default, always available) vs PSF Fit (requires the bundled Python engine to
    // be set up once — see PsfEngineSetup). Independent checkboxes, not mutually exclusive
    // (issue #9) — checking both runs Aperture fully (through its own Results export) then
    // PSF Fit fully, the same sequential "run both" pattern BatchRunService already used for
    // headless batch runs. (Previously plain booleans bound to RadioButtons rather than a
    // single Mode enum through ObjectConverters.Equal — confirmed on real testing that
    // converter doesn't implement ConvertBack, so the RadioButton visually toggled but never
    // actually wrote the new value back into the view model. Plain booleans still need no
    // converter, so that part of the reasoning carries over to the checkboxes.)
    [ObservableProperty] private bool _isAperture = true;
    [ObservableProperty] private bool _isPsfFit;

    /// <summary>Which mode the pass currently running (or the one that just finished) used —
    /// set explicitly by Run() before each pass rather than derived from IsAperture/IsPsfFit,
    /// since both of those can now be true at once (issue #9) and Mode still needs to
    /// unambiguously answer "which one is this result from" for ReportFailure, the Results
    /// tab's export tagging, etc.</summary>
    private PhotometryMode _currentRunMode = PhotometryMode.Aperture;
    public PhotometryMode Mode => _currentRunMode;

    /// <summary>True once a photometry run has produced at least one frame — gates the
    /// Exclude Transit button, which has nothing to show before that.</summary>
    [ObservableProperty] private bool _hasPoints;

    public ObservableCollection<PhotometryService.FramePoint> Points { get; } = [];

    /// <summary>Each comp star's own bias-corrected magnitude time series, keyed by its
    /// label — set after every run, regardless of whether the Results tab's per-comp
    /// diagnostics export is turned on, since retaining it costs nothing (a comp-count ×
    /// frame-count list of small structs) — only writing it to disk is opt-in.</summary>
    public IReadOnlyDictionary<string, List<PhotometryService.PerCompPoint>>? PerCompSeries { get; private set; }

    /// <summary>The target's own per-frame diagnostic snapshot (same fields as a comp's
    /// PerCompPoint) — exported alongside the comps' files when per-comp diagnostics is on, so
    /// the target's trend can be compared directly against each comp's.</summary>
    public List<PhotometryService.PerCompPoint>? TargetSeries { get; private set; }

    /// <summary>Raised after a photometry run finishes successfully, so the Results tab can
    /// auto-run its period search without requiring a manual button click.</summary>
    public event Action? Completed;

    /// <summary>Set by the view's code-behind to open the modal Exclude Transit popup and
    /// return the accepted per-frame excluded-flags array (parallel to the points passed in),
    /// or null if the user cancelled.</summary>
    public Func<IReadOnlyList<PhotometryService.FramePoint>, string, Task<bool[]?>>? ExcludeTransitFunc { get; set; }

    /// <summary>Set by the view's code-behind to open the PSF Engine Setup popup.</summary>
    public Func<Task>? OpenPsfSetupFunc { get; set; }

    [RelayCommand]
    private async Task OpenPsfSetup()
    {
        if (OpenPsfSetupFunc is not null) await OpenPsfSetupFunc();
    }

    private string FailureOutputDir() =>
        string.IsNullOrWhiteSpace(_data.OutputDirectory)
            ? (string.IsNullOrWhiteSpace(_data.InputDirectory) ? "." : _data.InputDirectory)
            : _data.OutputDirectory;

    private void ReportFailure(string stage, string reason) =>
        FailureReportService.Write(FailureOutputDir(), _data.TargetName,
            Mode == PhotometryMode.PsfFit ? "PsfFit" : "Aperture",
            _data.InputDirectory, stage, reason);

    [RelayCommand]
    private async Task Run()
    {
        if (IsRunning) return;

        if (!IsAperture && !IsPsfFit)
        {
            Status = "Check at least one of Aperture / PSF Fit.";
            ReportFailure("Photometry", Status);
            return;
        }

        if (!CoordinateParser.TryParseRa(_data.TargetRaText, out var ra) ||
            !CoordinateParser.TryParseDec(_data.TargetDecText, out var dec))
        {
            Status = "Enter target RA/Dec (decimal degrees or sexagesimal, e.g. 16:41:43) on the Data tab first.";
            ReportFailure("Photometry", Status);
            return;
        }

        // Built from Rows (not Stars) so a user-deselected comp (issue #4) is excluded, and
        // reusing each row's own already-assigned Label ("C3") rather than recomputing
        // position from this filtered list — a naive Select((s,i) => $"C{i+1}") would relabel
        // comps after a deselection (e.g. C3 becomes C2 once C2 is unchecked), silently
        // mismatching what the Comp Stars tab itself shows for the same star.
        var comps = _compStars.Rows
            .Where(r => r.Selected && r.IsIncluded && r.Source is not null)
            .Select(r => new PhotometryService.CompStarRef(
                r.Source!.Ra, r.Source.Dec, r.Source.Mag, $"{r.Label} ({r.Source.CatalogSource})"))
            .ToList();

        if (comps.Count == 0)
        {
            Status = "No comparison stars — run the Comp Stars tab first, or re-include a deselected comp.";
            ReportFailure("Photometry", Status);
            return;
        }

        IsRunning = true;
        StartElapsedTimer();
        _cts = ExternalCancellationToken is { } externalToken
            ? CancellationTokenSource.CreateLinkedTokenSource(externalToken)
            : new CancellationTokenSource();

        try
        {
            // Aperture fully first (through its own Results export via Completed), then PSF
            // Fit fully, when both are checked — same sequential pattern BatchRunService
            // already used headlessly. If Aperture throws, PSF Fit is skipped rather than
            // running against whatever partial state the exception left behind.
            if (IsAperture)
            {
                _currentRunMode = PhotometryMode.Aperture;
                await RunOnePassAsync(ra, dec, comps);
            }
            if (IsPsfFit)
            {
                _currentRunMode = PhotometryMode.PsfFit;
                await RunOnePassAsync(ra, dec, comps);
            }
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled.";
        }
        catch (Exception ex)
        {
            Status = $"✗  {ex.Message}";
            SessionLogService.Write($"[Photometry] Run failed: {ex}");
            ReportFailure("Photometry", $"Unhandled exception: {ex.Message}");
        }
        finally
        {
            IsRunning = false;
            StopElapsedTimer();
        }
    }

    /// <summary>Runs one full Aperture-or-PSF-Fit pass (whichever Mode currently says) and
    /// fires Completed so the Results tab exports it, before Run() potentially moves on to
    /// the other mode. Exceptions/cancellation propagate to Run()'s own try/catch.</summary>
    private async Task RunOnePassAsync(double ra, double dec, List<PhotometryService.CompStarRef> comps)
    {
        Points.Clear();
        Summary = "";
        Status  = "Starting…";
        var progress = new Progress<string>(s => Status = s);

        var result = await PhotometryService.RunAsync(
            _data.InputDirectory, ra, dec, comps, Mode, progress, _cts!.Token);

        ApertureRadiusPx = result.ApertureRadiusPx;
        AnnulusInnerPx   = result.AnnulusInnerPx;
        AnnulusOuterPx   = result.AnnulusOuterPx;
        Status           = result.StatusMessage;
        PerCompSeries    = result.PerCompSeries;
        TargetSeries     = result.TargetSeries;

        foreach (var p in result.Points)
            Points.Add(p);
        HasPoints = Points.Count > 0;

        var accepted = result.Points.Count(p => !p.Rejected);
        if (accepted == 0)
        {
            var reason = result.StatusMessage;
            if (result.Points.Count > 0)
            {
                var byReason = result.Points
                    .Where(p => p.Rejected)
                    .GroupBy(p => p.RejectReason ?? "unspecified")
                    .OrderByDescending(g => g.Count())
                    .Select(g => $"{g.Count()} {g.Key}");
                reason += Environment.NewLine + Environment.NewLine +
                          $"{result.Points.Count} frame(s) processed, 0 accepted:" +
                          Environment.NewLine + string.Join(Environment.NewLine, byReason);
            }
            ReportFailure("Photometry", reason);
        }

        RefreshSummary();
        Completed?.Invoke();
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    /// <summary>Resets photometry results back to their pre-run state — called when the Data
    /// tab's input directory changes, so a new dataset doesn't show a stale light curve from
    /// the previous one. Cancels an in-progress run first, if any.</summary>
    public void Clear()
    {
        if (IsRunning) _cts?.Cancel();
        Points.Clear();
        Summary          = "";
        Status           = "Not run yet.";
        ApertureRadiusPx = 0;
        AnnulusInnerPx   = 0;
        AnnulusOuterPx   = 0;
        HasPoints        = false;
        PerCompSeries    = null;
        TargetSeries     = null;
    }

    [RelayCommand]
    private async Task ExcludeTransit()
    {
        if (!HasPoints || ExcludeTransitFunc is null) return;

        var accepted = await ExcludeTransitFunc(Points.ToList(), $"{_data.FilterCode}mag");
        if (accepted is null || accepted.Length != Points.Count) return; // cancelled

        for (int i = 0; i < Points.Count; i++)
            if (Points[i].ExcludedFromTransit != accepted[i])
                Points[i] = Points[i] with { ExcludedFromTransit = accepted[i] };

        RefreshSummary();

        // Same signal Run() sends when it finishes — Results needs to recompute stats/period
        // search/export using the now-current set of excluded frames, not just after a fresh run.
        Completed?.Invoke();
    }

    /// <summary>Recomputes the Mean mag / Amplitude / Range / Frames-used summary line from
    /// whichever frames currently count (not statistically rejected, not manually excluded).
    /// Called after a run and again after Exclude Transit changes what counts.</summary>
    private void RefreshSummary()
    {
        var used = Points.Where(p => !p.Rejected && !p.ExcludedFromTransit).ToList();
        if (used.Count == 0) { Summary = ""; return; }

        double mean = used.Average(p => p.TargetMag);
        double min  = used.Min(p => p.TargetMag);
        double max  = used.Max(p => p.TargetMag);
        Summary = $"Mean mag: {mean:F3}   |   Amplitude: {max - min:F3}   |   " +
                  $"Range: {min:F3} – {max:F3}   |   Frames used: {used.Count}/{Points.Count}";
    }

}
