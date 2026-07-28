using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using VariLab.Models;

namespace VariLab.Views.Controls;

/// <summary>
/// Renders a magnitude-vs-JD scatter plot in the same visual style as EXOTIC's
/// Stellar_Variability.png (matplotlib default/astropy_mpl_style look): white
/// background, black spines/ticks, light gray grid, tomato error bars with dot
/// markers, and matplotlib's "offset notation" for large X values (e.g. JD),
/// where tick labels show only the remainder and the subtracted constant is
/// printed once in the bottom-right corner.
///
/// Optionally supports click-drag range selection (<see cref="SelectionEnabled"/>), used by
/// the Exclude Transit popup to mark points for exclusion. <see cref="Services.PlotExportService"/>'s
/// off-screen PNG rendering never sets SelectionEnabled, so it never receives pointer input.
/// </summary>
public class VariabilityChartControl : Control
{
    public static readonly StyledProperty<IReadOnlyList<PlotPoint>?> PointsProperty =
        AvaloniaProperty.Register<VariabilityChartControl, IReadOnlyList<PlotPoint>?>(nameof(Points));

    public static readonly StyledProperty<IReadOnlyList<double>?> YErrorsProperty =
        AvaloniaProperty.Register<VariabilityChartControl, IReadOnlyList<double>?>(nameof(YErrors));

    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<VariabilityChartControl, string>(nameof(Title), "");

    /// <summary>Optional smaller-font text drawn below the title — one or more lines,
    /// separated by '\n', e.g. run metadata (comp ensemble, filter, mean mag, amplitude).</summary>
    public static readonly StyledProperty<string> SubtitleProperty =
        AvaloniaProperty.Register<VariabilityChartControl, string>(nameof(Subtitle), "");

    public static readonly StyledProperty<string> XLabelProperty =
        AvaloniaProperty.Register<VariabilityChartControl, string>(nameof(XLabel), "");

    public static readonly StyledProperty<string> YLabelProperty =
        AvaloniaProperty.Register<VariabilityChartControl, string>(nameof(YLabel), "");

    public static readonly StyledProperty<bool> DrawLineProperty =
        AvaloniaProperty.Register<VariabilityChartControl, bool>(nameof(DrawLine));

    /// <summary>Optional vertical marker line (e.g. best period on a periodogram).</summary>
    public static readonly StyledProperty<double?> MarkerXProperty =
        AvaloniaProperty.Register<VariabilityChartControl, double?>(nameof(MarkerX));

    /// <summary>Parallel to <see cref="Points"/>: true marks a point as excluded (drawn muted
    /// grey instead of tomato).</summary>
    public static readonly StyledProperty<IReadOnlyList<bool>?> ExcludedProperty =
        AvaloniaProperty.Register<VariabilityChartControl, IReadOnlyList<bool>?>(nameof(Excluded));

    /// <summary>When true, click-drag on the plot area raises <see cref="RangeSelected"/> with
    /// the dragged X range instead of doing nothing.</summary>
    public static readonly StyledProperty<bool> SelectionEnabledProperty =
        AvaloniaProperty.Register<VariabilityChartControl, bool>(nameof(SelectionEnabled));

    /// <summary>When true, the Y axis increases downward (highest value at the bottom, lowest
    /// at the top) — the standard astronomical magnitude convention, where "up" means brighter.
    /// Default false preserves the original plain/matplotlib-default orientation (highest value
    /// at top).</summary>
    public static readonly StyledProperty<bool> InvertYProperty =
        AvaloniaProperty.Register<VariabilityChartControl, bool>(nameof(InvertY));

    public IReadOnlyList<PlotPoint>? Points  { get => GetValue(PointsProperty);  set => SetValue(PointsProperty, value); }
    public IReadOnlyList<double>?    YErrors { get => GetValue(YErrorsProperty); set => SetValue(YErrorsProperty, value); }
    public string Title                     { get => GetValue(TitleProperty);   set => SetValue(TitleProperty, value); }
    public string Subtitle                  { get => GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }
    public string XLabel                    { get => GetValue(XLabelProperty);  set => SetValue(XLabelProperty, value); }
    public string YLabel                    { get => GetValue(YLabelProperty);  set => SetValue(YLabelProperty, value); }
    public bool   DrawLine                  { get => GetValue(DrawLineProperty); set => SetValue(DrawLineProperty, value); }
    public double? MarkerX                  { get => GetValue(MarkerXProperty); set => SetValue(MarkerXProperty, value); }
    public IReadOnlyList<bool>? Excluded    { get => GetValue(ExcludedProperty); set => SetValue(ExcludedProperty, value); }
    public bool   SelectionEnabled          { get => GetValue(SelectionEnabledProperty); set => SetValue(SelectionEnabledProperty, value); }
    public bool   InvertY                   { get => GetValue(InvertYProperty); set => SetValue(InvertYProperty, value); }

    /// <summary>Raised when the user finishes a click-drag on the plot area (only while
    /// <see cref="SelectionEnabled"/> is true), with the dragged range in data (X-axis)
    /// coordinates, Lo &lt;= Hi.</summary>
    public event EventHandler<(double Lo, double Hi)>? RangeSelected;

    static VariabilityChartControl()
    {
        AffectsRender<VariabilityChartControl>(PointsProperty, YErrorsProperty, TitleProperty, SubtitleProperty, XLabelProperty, YLabelProperty,
                                                DrawLineProperty, MarkerXProperty, ExcludedProperty, InvertYProperty);
    }

    private static readonly IBrush _bgBrush       = Brushes.White;
    private static readonly IBrush _blackBrush    = Brushes.Black;
    private static readonly IBrush _gridBrush     = new SolidColorBrush(Color.Parse("#b0b0b0"));
    private static readonly IBrush _pointBrush    = new SolidColorBrush(Color.Parse("#ff6347")); // tomato
    private static readonly IBrush _excludedBrush = new SolidColorBrush(Color.Parse("#a0a0a0")); // muted grey
    private static readonly IBrush _emptyBrush    = new SolidColorBrush(Color.Parse("#888888"));
    private static readonly IBrush _markerBrush   = new SolidColorBrush(Color.Parse("#4682b4")); // steelblue
    private static readonly IBrush _dragBrush     = new SolidColorBrush(Color.Parse("#4682b4"), 0.18); // translucent steelblue

    private static readonly IPen _spinePen       = new Pen(_blackBrush, 1.0);
    private static readonly IPen _tickPen        = new Pen(_blackBrush, 1.0);
    private static readonly IPen _gridPen        = new Pen(_gridBrush, 0.7);
    private static readonly IPen _errPen         = new Pen(_pointBrush, 2.4);
    private static readonly IPen _excludedErrPen = new Pen(_excludedBrush, 2.4);
    private static readonly IPen _linePen        = new Pen(_pointBrush, 1.4);
    private static readonly IPen _markerPen      = new Pen(_markerBrush, 1.5, dashStyle: DashStyle.Dash);

    // ── Drag-select state ───────────────────────────────────────────────────
    // Plot geometry is cached from the most recent Render() so pointer handlers (which run
    // between renders) can convert a pixel X back to a data-space X without recomputing the
    // whole layout — plot bounds don't change mid-drag in practice.
    private bool   _geomValid;
    private double _geomXMin, _geomXMax, _geomML, _geomPlotW, _geomMT, _geomPlotH;
    private bool   _isDragging;
    private double _dragStartX, _dragCurrentX;

    private double PxToX(double px) => _geomXMin + (px - _geomML) / _geomPlotW * (_geomXMax - _geomXMin);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!SelectionEnabled || !_geomValid) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        _isDragging   = true;
        _dragStartX   = PxToX(e.GetPosition(this).X);
        _dragCurrentX = _dragStartX;
        e.Pointer.Capture(this);
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_isDragging) return;
        _dragCurrentX = PxToX(e.GetPosition(this).X);
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_isDragging) return;
        _isDragging = false;
        e.Pointer.Capture(null);

        double lo = Math.Min(_dragStartX, _dragCurrentX);
        double hi = Math.Max(_dragStartX, _dragCurrentX);
        InvalidateVisual();
        if (hi - lo > 1e-9)
            RangeSelected?.Invoke(this, (lo, hi));
    }

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 20 || h < 20) return;

        ctx.DrawRectangle(_bgBrush, null, new Rect(0, 0, w, h));

        var pts = Points;
        if (pts is null || pts.Count == 0)
        {
            _geomValid = false;
            var ft = MakeText("No data yet.", 14, _emptyBrush);
            ctx.DrawText(ft, new Point(w / 2 - ft.Width / 2, h / 2 - ft.Height / 2));
            return;
        }

        var yErr = YErrors;
        var excl = Excluded;
        double xMin = pts.Min(p => p.X), xMax = pts.Max(p => p.X);
        double yMin = pts.Min(p => p.Y), yMax = pts.Max(p => p.Y);
        if (yErr is { Count: > 0 })
        {
            for (int i = 0; i < pts.Count && i < yErr.Count; i++)
            {
                yMin = Math.Min(yMin, pts[i].Y - yErr[i]);
                yMax = Math.Max(yMax, pts[i].Y + yErr[i]);
            }
        }
        if (xMax - xMin < 1e-12) { xMin -= 0.5; xMax += 0.5; }
        if (yMax - yMin < 1e-12) { yMin -= 0.5; yMax += 0.5; }
        double xPad = (xMax - xMin) * 0.04, yPad = (yMax - yMin) * 0.08;
        xMin -= xPad; xMax += xPad;
        yMin -= yPad; yMax += yPad;

        // ── X-axis offset notation (matplotlib-style): subtract the integer part
        // of xMin so tick labels show only the small varying remainder, with the
        // subtracted constant printed once in the bottom-right corner. Only useful
        // for large absolute values (e.g. JD) — periods/phases stay as-is.
        bool   useOffset = Math.Abs(xMin) > 1000;
        double xOffset   = useOffset ? Math.Floor(xMin) : 0;

        bool hasTitle = !string.IsNullOrEmpty(Title);
        var subtitleLines = string.IsNullOrEmpty(Subtitle) ? Array.Empty<string>() : Subtitle.Split('\n');
        bool hasSubtitle = subtitleLines.Length > 0;
        const double mL = 72, mR = 20, mB = 58;
        const double headerTop = 10;
        double mT = !hasTitle && !hasSubtitle
            ? 16
            : headerTop + (hasTitle ? 20 : 0) + subtitleLines.Length * 15 + 8;
        double plotW = w - mL - mR;
        double plotH = h - mT - mB;
        if (plotW < 10 || plotH < 10) { _geomValid = false; return; }

        double XToPx(double x) => mL + (x - xMin) / (xMax - xMin) * plotW;
        // InvertY=false (default): plain/matplotlib-default orientation, largest value at top.
        // InvertY=true: standard astronomical magnitude convention, largest (faintest) value at
        // the bottom and smallest (brightest) at the top — "up" means brighter.
        double YToPx(double y) => InvertY
            ? mT + (y - yMin) / (yMax - yMin) * plotH
            : mT + plotH - (y - yMin) / (yMax - yMin) * plotH;

        // Cache geometry for pointer handlers now that we know it's valid.
        _geomXMin = xMin; _geomXMax = xMax; _geomML = mL; _geomPlotW = plotW; _geomMT = mT; _geomPlotH = plotH;
        _geomValid = true;

        double headerY = headerTop;
        if (hasTitle)
        {
            var titleFt = MakeText(Title, 15, _blackBrush);
            ctx.DrawText(titleFt, new Point(mL + plotW / 2 - titleFt.Width / 2, headerY));
            headerY += 20;
        }
        foreach (var line in subtitleLines)
        {
            var subFt = MakeText(line, 11, _blackBrush);
            ctx.DrawText(subFt, new Point(mL + plotW / 2 - subFt.Width / 2, headerY));
            headerY += 15;
        }

        // ── Plot border (matplotlib spines) ───────────────────────────────
        ctx.DrawRectangle(null, _spinePen, new Rect(mL, mT, plotW, plotH));

        // ── Y gridlines / ticks / labels ───────────────────────────────────
        double yStep = NiceStep(yMax - yMin, 6);
        for (double yv = Math.Ceiling(yMin / yStep) * yStep; yv <= yMax + 1e-9; yv += yStep)
        {
            double gy = YToPx(yv);
            if (gy < mT - 1 || gy > mT + plotH + 1) continue;
            ctx.DrawLine(_gridPen, new Point(mL, gy), new Point(mL + plotW, gy));
            ctx.DrawLine(_tickPen, new Point(mL - 4, gy), new Point(mL, gy));
            var ft = MakeText(yv.ToString("0.00"), 11, _blackBrush);
            ctx.DrawText(ft, new Point(mL - ft.Width - 8, gy - ft.Height / 2));
        }

        // ── X gridlines / ticks / labels ───────────────────────────────────
        double xStep = NiceStep(xMax - xMin, 8);
        for (double xv = Math.Ceiling((xMin - xOffset) / xStep) * xStep + xOffset; xv <= xMax + 1e-9; xv += xStep)
        {
            double gx = XToPx(xv);
            if (gx < mL - 1 || gx > mL + plotW + 1) continue;
            ctx.DrawLine(_gridPen, new Point(gx, mT), new Point(gx, mT + plotH));
            ctx.DrawLine(_tickPen, new Point(gx, mT + plotH), new Point(gx, mT + plotH + 4));
            var ft = MakeText((xv - xOffset).ToString("0.00"), 11, _blackBrush);
            ctx.DrawText(ft, new Point(gx - ft.Width / 2, mT + plotH + 6));
        }

        // ── Offset annotation (bottom-right, matplotlib "offset text") ─────
        if (useOffset)
        {
            string offsetStr = FormatOffset(xOffset);
            var offFt = MakeText(offsetStr, 11, _blackBrush);
            ctx.DrawText(offFt, new Point(mL + plotW - offFt.Width, mT + plotH + 22));
        }

        // ── Axis labels ─────────────────────────────────────────────────
        if (!string.IsNullOrEmpty(XLabel))
        {
            var xLbl = MakeText(XLabel, 13, _blackBrush);
            ctx.DrawText(xLbl, new Point(mL + plotW / 2 - xLbl.Width / 2, h - 18));
        }
        if (!string.IsNullOrEmpty(YLabel))
        {
            var yLbl = MakeText(YLabel, 13, _blackBrush);
            using (ctx.PushTransform(Matrix.CreateRotation(-Math.PI / 2) *
                                     Matrix.CreateTranslation(16, mT + plotH / 2 + yLbl.Width / 2)))
                ctx.DrawText(yLbl, new Point(0, 0));
        }

        // ── Marker (e.g. best period on a periodogram) ─────────────────────
        if (MarkerX is { } mx && mx >= xMin && mx <= xMax)
        {
            double gx = XToPx(mx);
            ctx.DrawLine(_markerPen, new Point(gx, mT), new Point(gx, mT + plotH));
        }

        // ── Data (same YToPx transform as the gridlines above) ─────────────
        if (DrawLine)
        {
            var geom = new StreamGeometry();
            using var sgc = geom.Open();
            bool first = true;
            foreach (var p in pts.OrderBy(p => p.X))
            {
                var pt = new Point(XToPx(p.X), YToPx(p.Y));
                if (first) { sgc.BeginFigure(pt, false); first = false; }
                else        sgc.LineTo(pt);
            }
            ctx.DrawGeometry(null, _linePen, geom);
        }
        else
        {
            for (int i = 0; i < pts.Count; i++)
            {
                var p  = pts[i];
                var px = XToPx(p.X);
                var py = YToPx(p.Y);
                bool isExcluded = excl is not null && i < excl.Count && excl[i];
                var pointBrush = isExcluded ? _excludedBrush : _pointBrush;
                var errPen     = isExcluded ? _excludedErrPen : _errPen;

                if (yErr is not null && i < yErr.Count && yErr[i] > 0)
                {
                    double pyLo = YToPx(p.Y - yErr[i]);
                    double pyHi = YToPx(p.Y + yErr[i]);
                    ctx.DrawLine(errPen, new Point(px, pyLo), new Point(px, pyHi));
                }
                ctx.DrawEllipse(pointBrush, null, new Point(px, py), 1.6, 1.6);
            }
        }

        // ── Active drag-selection overlay (drawn last, on top of data) ─────
        if (_isDragging)
        {
            double loX = Math.Min(_dragStartX, _dragCurrentX);
            double hiX = Math.Max(_dragStartX, _dragCurrentX);
            double gxLo = Math.Clamp(XToPx(loX), mL, mL + plotW);
            double gxHi = Math.Clamp(XToPx(hiX), mL, mL + plotW);
            ctx.DrawRectangle(_dragBrush, null, new Rect(gxLo, mT, gxHi - gxLo, plotH));
        }
    }

    /// <summary>Pick a "nice" grid step (1/2/5 × 10^n) targeting ~n ticks across the range.</summary>
    private static double NiceStep(double range, int targetTicks)
    {
        if (range <= 0) return 1;
        double raw = range / targetTicks;
        double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double norm = raw / mag;
        double step = norm < 1.5 ? 1 : norm < 3.5 ? 2 : norm < 7.5 ? 5 : 10;
        return step * mag;
    }

    private static string FormatOffset(double offset)
    {
        if (offset == 0) return "";
        int power = (int)Math.Floor(Math.Log10(Math.Abs(offset)));
        double mantissa = offset / Math.Pow(10, power);
        return $"+{mantissa.ToString("0.000000", CultureInfo.InvariantCulture)}e{power}";
    }

    private static FormattedText MakeText(string text, double size, IBrush brush)
        => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
               new Typeface("Arial"), size, brush);
}
