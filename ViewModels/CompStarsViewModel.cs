using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using VariLab.Services;

namespace VariLab.ViewModels;

/// <summary>One row in the Comp Stars tab's combined table — either a selected comp or a
/// rejected candidate, distinguished by <see cref="Selected"/>/<see cref="StatusGlyph"/>.</summary>
public record CompRow(
    bool    Selected,
    string  StatusGlyph,
    int     X,
    int     Y,
    double? Mag,
    string? MagSource,
    string? FilterBand,
    double? FwhmPx,
    double? Snr,
    string  CatalogSource,
    double? SepArcsec,
    double? BpRp,
    double? Ruwe,
    string? Reason);

/// <summary>
/// Comp Stars tab — runs the Stone method (Gaia DR3 + AAVSO VSP + APASS DR9 + GSPC)
/// against a reference frame from the Data tab's input directory to build the
/// ensemble comparison-star set used for differential photometry.
/// </summary>
public partial class CompStarsViewModel : ViewModelBase
{
    private readonly DataViewModel _data;
    private CancellationTokenSource? _cts;

    public CompStarsViewModel(DataViewModel data) => _data = data;

    [ObservableProperty] private int    _maxCompStars = 10;
    [ObservableProperty] private bool   _isRunning    = false;
    [ObservableProperty] private string _status       = "Not run yet.";
    [ObservableProperty] private string _log          = "";

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

    public ObservableCollection<GaiaCompService.CompStarInfo> Stars { get; } = [];
    public ObservableCollection<GaiaCompService.RejectedCompInfo> RejectedStars { get; } = [];

    /// <summary>
    /// One row per candidate — selected comps and rejected comps combined into a single
    /// display list (green check / red X), rather than two separate tables. <see cref="Stars"/>
    /// and <see cref="RejectedStars"/> are kept as-is for downstream consumers (AAVSO NOTES
    /// field, Excel report sheets) — this is purely an additional view-friendly projection.
    /// </summary>
    public ObservableCollection<CompRow> Rows { get; } = [];

    [RelayCommand]
    private async Task Run()
    {
        if (IsRunning) return;

        var fitsPath = FitsHeaderService.FindFirstFits(_data.InputDirectory);
        if (fitsPath is null)
        {
            Status = "No FITS files found in the input directory.";
            return;
        }

        // Pre-flight check: a dataset that was never plate-solved (or barely was — e.g. only
        // 1 of 89 frames actually has a WCS) wastes the user's time running comp-star selection
        // and photometry that can only reject almost everything. Block before that happens.
        var solveCheck = PlateSolveCheckService.CheckDirectory(_data.InputDirectory);
        if (solveCheck.Total > 0 && solveCheck.WithWcs < solveCheck.Total / 2.0)
        {
            Status = $"✗  Only {solveCheck.WithWcs}/{solveCheck.Total} frames are plate-solved — plate-solve this dataset first.";
            if (NotifyPlateSolveRequired is not null)
                await NotifyPlateSolveRequired(solveCheck.WithWcs, solveCheck.Total);
            return;
        }

        if (!CoordinateParser.TryParseRa(_data.TargetRaText, out var ra) ||
            !CoordinateParser.TryParseDec(_data.TargetDecText, out var dec))
        {
            Status = "Enter target RA/Dec (decimal degrees or sexagesimal, e.g. 16:41:43) on the Data tab first.";
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
            return;
        }

        Stars.Clear();
        RejectedStars.Clear();
        Rows.Clear();
        Log = "";
        IsRunning = true;
        Status = "Running Stone method comp-star selection…";
        _cts = new CancellationTokenSource();

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
                ct: _cts.Token);

            Status = result.StatusMessage;
            if (result.Stars is not null)
                foreach (var s in result.Stars)
                {
                    Stars.Add(s);
                    Rows.Add(new CompRow(true, "✓", s.X, s.Y, s.Mag, s.MagSource, s.FilterBand,
                        s.FwhmPx, s.Snr, s.CatalogSource, s.SepArcsec, s.BpRp, s.Ruwe, null));
                }
            if (result.Rejected is not null)
                foreach (var r in result.Rejected)
                {
                    RejectedStars.Add(r);
                    Rows.Add(new CompRow(false, "✗", r.X, r.Y, r.Mag, r.MagSource, null,
                        null, null, r.CatalogSource, null, null, null, r.Reason));
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
        }
        finally
        {
            IsRunning = false;
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
    }
}
