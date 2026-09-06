using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VariLab.Services;

namespace VariLab.ViewModels;

/// <summary>One row in the Comp Stars tab's combined table — either a selected comp or a
/// rejected candidate, distinguished by <see cref="Selected"/>/<see cref="StatusGlyph"/>.
/// An ObservableObject (not a plain record) specifically so <see cref="IsIncluded"/> can be
/// toggled live from the view (issue #4: let the user de-select an automatically-selected
/// comp) without rebuilding the row or the whole Rows collection.</summary>
public partial class CompRow : ObservableObject
{
    public bool    Selected      { get; }
    public string  StatusGlyph   { get; }
    public string? Label         { get; }
    public int     X             { get; }
    public int     Y             { get; }
    public double? Mag           { get; }
    public string? MagSource     { get; }
    public string? FilterBand    { get; }
    public double? FwhmPx        { get; }
    public double? Snr           { get; }
    public string  CatalogSource { get; }
    public double? SepArcsec     { get; }
    public double? BpRp          { get; }
    public double? Ruwe          { get; }
    public string? Reason        { get; }

    /// <summary>Back-reference to the original selected star (null for rejected rows) — lets
    /// PhotometryViewModel filter out user-deselected comps without any id-matching.</summary>
    public GaiaCompService.CompStarInfo? Source { get; }

    /// <summary>Only meaningful when <see cref="Selected"/> is true. Defaults to included;
    /// unchecking it on the Comp Stars tab excludes this comp from Photometry without removing
    /// it from the list — it stays visible so it can be toggled back on, and so the tab still
    /// shows exactly what the automated pipeline actually selected.</summary>
    [ObservableProperty] private bool _isIncluded = true;

    public CompRow(bool selected, string statusGlyph, string? label, int x, int y, double? mag,
        string? magSource, string? filterBand, double? fwhmPx, double? snr, string catalogSource,
        double? sepArcsec, double? bpRp, double? ruwe, string? reason,
        GaiaCompService.CompStarInfo? source = null)
    {
        Selected = selected; StatusGlyph = statusGlyph; Label = label; X = x; Y = y; Mag = mag;
        MagSource = magSource; FilterBand = filterBand; FwhmPx = fwhmPx; Snr = snr;
        CatalogSource = catalogSource; SepArcsec = sepArcsec; BpRp = bpRp; Ruwe = ruwe;
        Reason = reason; Source = source;
    }
}

/// <summary>
/// Comp Stars tab — runs the Stone method (Gaia DR3 + AAVSO VSP + APASS DR9 + GSPC)
/// against a reference frame from the Data tab's input directory to build the
/// ensemble comparison-star set used for differential photometry.
/// </summary>
public partial class CompStarsViewModel : ViewModelBase
{
    private readonly DataViewModel _data;
    private CancellationTokenSource? _cts;

    /// <summary>Set by BatchRunService so its own Cancel button can interrupt whichever target
    /// is currently running here, not just take effect at the next target boundary — this
    /// ViewModel's own Cancel button (unreachable from the headless Batch window) still works
    /// unaffected, since interactive use never sets this and Run() falls back to a plain
    /// unlinked CancellationTokenSource exactly as before.</summary>
    public CancellationToken? ExternalCancellationToken { get; set; }

    public CompStarsViewModel(DataViewModel data)
    {
        _data = data;
        // Run is gated on !LookupFailed (see CanRun) — Data tab's most recent name lookup, if
        // one was attempted, must not have failed. NotifyCanExecuteChanged() only re-evaluates
        // when explicitly told to, so this has to be wired to fire whenever LookupFailed changes.
        _data.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DataViewModel.LookupFailed))
            {
                RunCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(CanRunNow));
            }
        };
    }

    [ObservableProperty] private int     _maxCompStars           = 10;
    [ObservableProperty] private bool    _isRunning              = false;
    [ObservableProperty] private string  _status                 = "Not run yet.";
    [ObservableProperty] private string  _log                    = "";

    /// <summary>Set when the target itself has a Gaia neighbor closer than this pipeline's own
    /// comp-star isolation standard — a predictive warning shown before Photometry runs, not a
    /// blocking check (there's no fallback target to swap in the way a bad comp candidate can
    /// just be replaced). Null when the target is clear.</summary>
    [ObservableProperty] private string? _targetNeighborWarning  = null;

    /// <summary>
    /// Wired by the code-behind to show a modal confirmation when Gaia, VSP, or APASS fails
    /// outright during a run — lets the user cancel rather than silently continuing with
    /// whatever sources did respond. Returning true continues; false cancels the run.
    /// </summary>
    public Func<string, string, Task<bool>>? ConfirmOnSourceFailure { get; set; }

    /// <summary>Wired by the code-behind to show a blocking, OK-only alert when most of the
    /// input directory's frames have no WCS solution — there's no "continue anyway" here,
    /// since a dataset that isn't plate-solved gives the pipeline nothing to work with.</summary>
    public Func<int, int, Task>? NotifyPlateSolveRequired { get; set; }

    /// <summary>Wired by the code-behind to show a blocking, OK-only alert when the resolved
    /// target position doesn't project onto the reference frame's plate solution (e.g. a stale
    /// FITS OBJECT header from a previous target). Args: targetRa, targetDec, frameRa, frameDec,
    /// separationDeg, fovArcmin. No "continue anyway" — a target outside the frame guarantees
    /// zero comp-star candidates downstream.</summary>
    public Func<double, double, double, double, double, double, Task>? NotifyTargetNotInFrame { get; set; }

    /// <summary>Wired by the code-behind to show a blocking, OK-only alert when the target's
    /// resolved position is close enough to this frame's pointing to pass the check above, but
    /// its projected pixel position still falls outside the frame's real bounds — e.g. a
    /// correctly-resolved target that simply isn't covered by this particular dataset's
    /// footprint. Args: targetRa, targetDec, pixelX, pixelY, naxis1, naxis2. No "continue
    /// anyway" — there's no real pixel data at the target's position to measure.</summary>
    public Func<double, double, int, int, int, int, Task>? NotifyTargetOffFrame { get; set; }

    public ObservableCollection<GaiaCompService.CompStarInfo> Stars { get; } = [];
    public ObservableCollection<GaiaCompService.RejectedCompInfo> RejectedStars { get; } = [];

    /// <summary>
    /// One row per candidate — selected comps and rejected comps combined into a single
    /// display list (green check / red X), rather than two separate tables. <see cref="Stars"/>
    /// and <see cref="RejectedStars"/> are kept as-is for downstream consumers (AAVSO NOTES
    /// field, Excel report sheets) — this is purely an additional view-friendly projection.
    /// </summary>
    public ObservableCollection<CompRow> Rows { get; } = [];

    /// <summary>Blocks Run only when the Data tab's most recent lookup for the current target
    /// name actually failed — not simply "never looked up," which also leaves LookupFailed
    /// false and keeps this legitimate (e.g. RA/Dec typed in directly, no lookup ever used).
    /// Running against a name that just failed to resolve either reuses stale RA/Dec from a
    /// previous target or fails immediately downstream either way.</summary>
    private bool CanRun() => !_data.LookupFailed;

    /// <summary>Drives the Run button's IsEnabled binding directly — the button already binds
    /// IsEnabled explicitly (rather than relying on ICommand.CanExecute alone, which an
    /// explicit IsEnabled binding would just override), so CanRun's result needs its own
    /// change notification wired in on both triggers (IsRunning, Data tab's LookupFailed) to
    /// actually reach the UI.</summary>
    public bool CanRunNow => !IsRunning && CanRun();

    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(CanRunNow));

    /// <summary>Where failure reports (and, downstream, VariLab_Results_... exports) go for
    /// the current target — same OutputDirectory-then-InputDirectory-then-"." fallback
    /// ResultsViewModel.AutoExportAll uses, so a failure report and a later successful export
    /// for the same target land in the same base folder.</summary>
    private string FailureOutputDir() =>
        string.IsNullOrWhiteSpace(_data.OutputDirectory)
            ? (string.IsNullOrWhiteSpace(_data.InputDirectory) ? "." : _data.InputDirectory)
            : _data.OutputDirectory;

    private void ReportFailure(string stage, string reason) =>
        FailureReportService.Write(FailureOutputDir(), _data.TargetName, "CompStars",
            _data.InputDirectory, stage, reason);

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task Run()
    {
        if (IsRunning) return;

        var fitsPath = FitsHeaderService.FindFirstFits(_data.InputDirectory);
        if (fitsPath is null)
        {
            Status = "No FITS files found in the input directory.";
            ReportFailure("Comp Stars", Status);
            return;
        }

        // Pre-flight check: a dataset that was never plate-solved (or barely was — e.g. only
        // 1 of 89 frames actually has a WCS) wastes the user's time running comp-star selection
        // and photometry that can only reject almost everything. Block before that happens.
        var solveCheck = PlateSolveCheckService.CheckDirectory(_data.InputDirectory);
        if (solveCheck.Total > 0 && solveCheck.WithWcs < solveCheck.Total / 2.0)
        {
            Status = $"✗  Only {solveCheck.WithWcs}/{solveCheck.Total} frames are plate-solved — plate-solve this dataset first.";
            ReportFailure("Comp Stars", Status);
            if (NotifyPlateSolveRequired is not null)
                await NotifyPlateSolveRequired(solveCheck.WithWcs, solveCheck.Total);
            return;
        }

        if (!CoordinateParser.TryParseRa(_data.TargetRaText, out var ra) ||
            !CoordinateParser.TryParseDec(_data.TargetDecText, out var dec))
        {
            Status = "Enter target RA/Dec (decimal degrees or sexagesimal, e.g. 16:41:43) on the Data tab first.";
            ReportFailure("Comp Stars", Status);
            return;
        }

        // Filter drives the whole magnitude-lookup chain (VSP band, APASS column, GSPC column) —
        // proceeding without one silently picks up whatever FilterCode last held (e.g. left over
        // from a previous dataset, or an unrecognized FITS header value the dropdown couldn't
        // show as selected). Block rather than run comp selection against the wrong filter.
        if (string.IsNullOrWhiteSpace(_data.FilterCode) ||
            Array.IndexOf(_data.FilterOptions, _data.FilterCode) < 0)
        {
            Status = "Select a Filter on the Data tab before running Comp Star Selection.";
            ReportFailure("Comp Stars", Status);
            return;
        }

        Stars.Clear();
        RejectedStars.Clear();
        Rows.Clear();
        Log = "";
        TargetNeighborWarning = null;
        IsRunning = true;
        StartElapsedTimer();
        Status = "Running Stone method comp-star selection…";
        _cts = ExternalCancellationToken is { } externalToken
            ? CancellationTokenSource.CreateLinkedTokenSource(externalToken)
            : new CancellationTokenSource();

        var progress    = new Progress<string>(s => Status = s);
        var logProgress = new Progress<string>(s => Log += s + "\n");

        try
        {
            var result = await GaiaCompService.FetchAsync(
                fitsPath, ra, dec, _data.FilterCode,
                string.IsNullOrWhiteSpace(_data.AavsoObserverCode) ? null : _data.AavsoObserverCode,
                useVsp: true,
                progress: progress,
                logProgress: logProgress,
                maxCompStars: MaxCompStars,
                confirmOnSourceFailure: ConfirmOnSourceFailure,
                notifyTargetNotInFrame: NotifyTargetNotInFrame,
                notifyTargetOffFrame: NotifyTargetOffFrame,
                ct: _cts.Token);

            Status = result.StatusMessage;
            TargetNeighborWarning = result.TargetNeighborWarning;
            if (result.Stars is not null)
            {
                int compIndex = 1;
                // "C{n}" here must match PhotometryViewModel.Run()'s own labeling exactly
                // ($"C{i + 1} ({s.CatalogSource})", indexed by position in this same Stars
                // collection) — this is purely a display label so you can tell which physical
                // star is which comp while still on this tab, not a second source of truth.
                foreach (var s in result.Stars)
                {
                    Stars.Add(s);
                    Rows.Add(new CompRow(true, "✓", $"C{compIndex++}", s.X, s.Y, s.Mag, s.MagSource, s.FilterBand,
                        s.FwhmPx, s.Snr, s.CatalogSource, s.SepArcsec, s.BpRp, s.Ruwe, null, s));
                }
            }
            if (result.Rejected is not null)
                foreach (var r in result.Rejected)
                {
                    RejectedStars.Add(r);
                    Rows.Add(new CompRow(false, "✗", null, r.X, r.Y, r.Mag, r.MagSource, null,
                        null, null, r.CatalogSource, null, null, null, r.Reason));
                }

            if (Stars.Count == 0)
            {
                // No usable comps means neither Aperture nor PsfFit can proceed for this
                // target — capture the pipeline's own StatusMessage plus, if any candidates
                // were tested and rejected, a grouped breakdown of why, in the same terms the
                // Comp Stars tab's own Reason column already shows.
                var reason = result.StatusMessage;
                if (result.Rejected is { Count: > 0 })
                {
                    var byReason = result.Rejected
                        .GroupBy(r => r.Reason)
                        .OrderByDescending(g => g.Count())
                        .Select(g => $"{g.Count()} {g.Key}");
                    reason += Environment.NewLine + Environment.NewLine +
                              $"{result.Rejected.Count} candidate(s) tested and rejected:" +
                              Environment.NewLine + string.Join(Environment.NewLine, byReason);
                }
                ReportFailure("Comp Stars", reason);
            }
        }
        catch (System.OperationCanceledException)
        {
            Status = "Cancelled.";
        }
        catch (System.Exception ex)
        {
            Status = $"✗  {ex.Message}";
            SessionLogService.Write($"[CompStars] Run failed: {ex}");
            ReportFailure("Comp Stars", $"Unhandled exception: {ex.Message}");
        }
        finally
        {
            IsRunning = false;
            StopElapsedTimer();
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    /// <summary>Resets comp-star selection results back to their pre-run state — called when
    /// the Data tab's input directory changes, so a new dataset doesn't show stale comps
    /// selected for the previous one. Cancels an in-progress run first, if any.</summary>
    public void Clear()
    {
        if (IsRunning) _cts?.Cancel();
        Stars.Clear();
        RejectedStars.Clear();
        Rows.Clear();
        Log = "";
        Status = "Not run yet.";
        TargetNeighborWarning = null;
    }
}
