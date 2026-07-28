using Avalonia.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VariLab.Models;
using VariLab.Services;

namespace VariLab.Views;

/// <summary>
/// Modal popup: shows the current light curve and lets the user drag-select ranges (e.g. a
/// transit dip) to exclude from variability analysis and export. Nothing is applied to the
/// caller's data until Accept is clicked — Cancel discards the draft entirely.
/// </summary>
public partial class TransitExclusionWindow : Window
{
    private List<PhotometryService.FramePoint> _points = [];
    private bool[] _draftExcluded = [];

    /// <summary>Indices into <see cref="_points"/> for frames actually shown on the chart —
    /// statistically-rejected frames (3σ outliers, no WCS, etc.) carry sentinel values
    /// (Jd=0 among them) and must never be plotted or made selectable, or they blow out the
    /// axis scale and squash the real data into an unreadable sliver.</summary>
    private List<int> _visibleIndices = [];

    public TransitExclusionWindow()
    {
        InitializeComponent();
        Chart.SelectionEnabled =  true;
        Chart.RangeSelected    += OnRangeSelected;
        ClearButton.Click      += (_, _) => { Array.Clear(_draftExcluded); RefreshExcludedOverlay(); };
        CancelButton.Click     += (_, _) => Close(null);
        AcceptButton.Click     += (_, _) => Close((bool[])_draftExcluded.Clone());
    }

    /// <summary>Shows the popup and returns the accepted per-frame excluded-flags array
    /// (parallel to <paramref name="points"/>), or null if the user cancelled.</summary>
    public static Task<bool[]?> ShowAsync(Window owner, IReadOnlyList<PhotometryService.FramePoint> points, string yLabel)
    {
        var win = new TransitExclusionWindow
        {
            _points        = points.ToList(),
            _draftExcluded = points.Select(p => p.ExcludedFromTransit).ToArray(),
        };
        win._visibleIndices = Enumerable.Range(0, win._points.Count)
            .Where(i => !win._points[i].Rejected)
            .ToList();
        win.Chart.YLabel  = yLabel;
        win.Chart.Points  = win._visibleIndices.Select(i => new PlotPoint(win._points[i].Jd, win._points[i].TargetMag)).ToList();
        win.Chart.YErrors = win._visibleIndices.Select(i => win._points[i].TargetMagErr).ToList();
        win.RefreshExcludedOverlay();
        return win.ShowDialog<bool[]?>(owner);
    }

    /// <summary>Toggle rule: if every frame in the dragged range is already excluded, un-exclude
    /// them all; otherwise exclude them all. Keeps repeated drags over a mixed selection
    /// predictable instead of flipping each point individually.</summary>
    private void OnRangeSelected(object? sender, (double Lo, double Hi) range)
    {
        var hits = _visibleIndices
            .Where(i => _points[i].Jd >= range.Lo && _points[i].Jd <= range.Hi)
            .ToList();
        if (hits.Count == 0) return;

        bool allAlreadyExcluded = hits.All(i => _draftExcluded[i]);
        foreach (var i in hits)
            _draftExcluded[i] = !allAlreadyExcluded;

        RefreshExcludedOverlay();
    }

    private void RefreshExcludedOverlay() =>
        Chart.Excluded = _visibleIndices.Select(i => _draftExcluded[i]).ToList();
}
