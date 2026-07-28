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

    [RelayCommand]
    private async Task Run()
    {
        if (IsRunning) return;

        if (!CoordinateParser.TryParseRa(_data.TargetRaText, out var ra) ||
            !CoordinateParser.TryParseDec(_data.TargetDecText, out var dec))
        {
            Status = "Enter target RA/Dec (decimal degrees or sexagesimal, e.g. 16:41:43) on the Data tab first.";
            return;
        }

        if (_compStars.Stars.Count == 0)
        {
            Status = "No comparison stars — run the Comp Stars tab first.";
            return;
        }

        var comps = _compStars.Stars
            .Select((s, i) => new PhotometryService.CompStarRef(
                s.Ra, s.Dec, s.Mag, $"C{i + 1} ({s.CatalogSource})"))
            .ToList();

        Points.Clear();
        Summary   = "";
        IsRunning = true;
        Status    = "Starting…";
        _cts      = new CancellationTokenSource();

        var progress = new Progress<string>(s => Status = s);

        try
        {
            var result = await PhotometryService.RunAsync(
                _data.InputDirectory, ra, dec, comps, progress, _cts.Token);

            ApertureRadiusPx = result.ApertureRadiusPx;
            AnnulusInnerPx   = result.AnnulusInnerPx;
            AnnulusOuterPx   = result.AnnulusOuterPx;
            Status           = result.StatusMessage;
            PerCompSeries    = result.PerCompSeries;
            TargetSeries     = result.TargetSeries;

            foreach (var p in result.Points)
                Points.Add(p);
            HasPoints = Points.Count > 0;

            RefreshSummary();
            Completed?.Invoke();
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled.";
        }
        catch (Exception ex)
        {
            Status = $"✗  {ex.Message}";
            SessionLogService.Write($"[Photometry] Run failed: {ex}");
        }
        finally
        {
            IsRunning = false;
        }
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
