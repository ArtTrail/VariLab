using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering;
using System;
using System.Collections.Generic;
using System.Linq;
using VariLab.Models;

namespace VariLab.Views.Controls;

/// <summary>
/// Minimal hand-drawn scatter/line plot — no third-party charting dependency,
/// matching the pattern already used by TransitLab's LightCurveControl /
/// TransitGeometryControl (custom Avalonia Control.Render), so the eventual
/// fold-in shares the same visual approach rather than a competing library.
/// Hovering a point shows a tooltip with its X value, magnitude ± uncertainty, and
/// source frame (issue #27) — the uncertainty/label come from the optional
/// PointErrors/PointLabels lists, parallel to Points by index.
/// </summary>
public class ScatterPlotControl : Control, ICustomHitTest
{
    /// <summary>Optional per-point uncertainty for the hover tooltip (parallel to Points).
    /// Distinct from YErrors, which also draws error bars; this is tooltip-only.</summary>
    public static readonly StyledProperty<IReadOnlyList<double>?> PointErrorsProperty =
        AvaloniaProperty.Register<ScatterPlotControl, IReadOnlyList<double>?>(nameof(PointErrors));

    /// <summary>Optional per-point label (source frame filename) for the hover tooltip.</summary>
    public static readonly StyledProperty<IReadOnlyList<string>?> PointLabelsProperty =
        AvaloniaProperty.Register<ScatterPlotControl, IReadOnlyList<string>?>(nameof(PointLabels));

    public IReadOnlyList<double>? PointErrors { get => GetValue(PointErrorsProperty); set => SetValue(PointErrorsProperty, value); }
    public IReadOnlyList<string>? PointLabels { get => GetValue(PointLabelsProperty); set => SetValue(PointLabelsProperty, value); }

    public static readonly StyledProperty<IReadOnlyList<PlotPoint>?> PointsProperty =
        AvaloniaProperty.Register<ScatterPlotControl, IReadOnlyList<PlotPoint>?>(nameof(Points));

    /// <summary>Optional ±Y error bars, parallel to <see cref="Points"/> (by index).</summary>
    public static readonly StyledProperty<IReadOnlyList<double>?> YErrorsProperty =
        AvaloniaProperty.Register<ScatterPlotControl, IReadOnlyList<double>?>(nameof(YErrors));

    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<ScatterPlotControl, string>(nameof(Title), "");

    public static readonly StyledProperty<string> XLabelProperty =
        AvaloniaProperty.Register<ScatterPlotControl, string>(nameof(XLabel), "");

    public static readonly StyledProperty<string> YLabelProperty =
        AvaloniaProperty.Register<ScatterPlotControl, string>(nameof(YLabel), "");

    /// <summary>When true, the Y axis is drawn with larger values at the bottom
    /// (for magnitudes, where brighter = numerically smaller).</summary>
    public static readonly StyledProperty<bool> InvertYProperty =
        AvaloniaProperty.Register<ScatterPlotControl, bool>(nameof(InvertY));

    public static readonly StyledProperty<bool> DrawLineProperty =
        AvaloniaProperty.Register<ScatterPlotControl, bool>(nameof(DrawLine));

    /// <summary>Optional vertical marker line (e.g. best period on a periodogram).</summary>
    public static readonly StyledProperty<double?> MarkerXProperty =
        AvaloniaProperty.Register<ScatterPlotControl, double?>(nameof(MarkerX));

    /// <summary>Point/error-bar color as a hex string, e.g. "#88c0d0" or "tomato".</summary>
    public static readonly StyledProperty<string> PointColorProperty =
        AvaloniaProperty.Register<ScatterPlotControl, string>(nameof(PointColor), "#88c0d0");

    public IReadOnlyList<PlotPoint>?  Points   { get => GetValue(PointsProperty);   set => SetValue(PointsProperty, value); }
    public IReadOnlyList<double>?     YErrors  { get => GetValue(YErrorsProperty);  set => SetValue(YErrorsProperty, value); }
    public string Title                        { get => GetValue(TitleProperty);    set => SetValue(TitleProperty, value); }
    public string XLabel                       { get => GetValue(XLabelProperty);   set => SetValue(XLabelProperty, value); }
    public string YLabel                       { get => GetValue(YLabelProperty);   set => SetValue(YLabelProperty, value); }
    public bool   InvertY                      { get => GetValue(InvertYProperty);  set => SetValue(InvertYProperty, value); }
    public bool   DrawLine                     { get => GetValue(DrawLineProperty); set => SetValue(DrawLineProperty, value); }
    public double? MarkerX                     { get => GetValue(MarkerXProperty);  set => SetValue(MarkerXProperty, value); }
    public string PointColor                   { get => GetValue(PointColorProperty); set => SetValue(PointColorProperty, value); }

    static ScatterPlotControl()
    {
        AffectsRender<ScatterPlotControl>(PointsProperty, YErrorsProperty, TitleProperty, XLabelProperty, YLabelProperty,
                                           InvertYProperty, DrawLineProperty, MarkerXProperty, PointColorProperty);
    }

    // Nord palette — matches TransitLab's App.axaml brushes (BrushBg, BrushSep, BrushAccent, BrushHint, BrushErr).
    private static readonly IBrush _bgBrush     = new SolidColorBrush(Color.Parse("#2e3440"));
    private static readonly IBrush _axisBrush   = new SolidColorBrush(Color.Parse("#4c566a"));
    private static readonly IBrush _gridBrush   = new SolidColorBrush(Color.FromArgb(40, 150, 170, 200));
    private static readonly IBrush _pointBrush  = new SolidColorBrush(Color.Parse("#88c0d0"));
    private static readonly IBrush _lineBrush   = new SolidColorBrush(Color.Parse("#88c0d0"));
    private static readonly IBrush _labelBrush  = new SolidColorBrush(Color.Parse("#8892a0"));
    private static readonly IBrush _markerBrush = new SolidColorBrush(Color.FromArgb(200, 191, 97, 106));
    private static readonly IBrush _emptyBrush  = new SolidColorBrush(Color.Parse("#6677aa"));

    private static readonly IPen _axisPen   = new Pen(_axisBrush, 1.0);
    private static readonly IPen _gridPen   = new Pen(_gridBrush, 1.0, dashStyle: DashStyle.Dash);
    private static readonly IPen _linePen   = new Pen(_lineBrush, 1.2);
    private static readonly IPen _markerPen = new Pen(_markerBrush, 1.5);

    // Hover tooltip (issue #27)
    private static readonly IBrush _tipBgBrush   = new SolidColorBrush(Color.Parse("#3b4252"));
    private static readonly IBrush _tipTextBrush = new SolidColorBrush(Color.Parse("#eceff4"));
    private static readonly IPen   _tipBorderPen = new Pen(new SolidColorBrush(Color.Parse("#88c0d0")), 1.0);
    private static readonly IBrush _hoverRing    = new SolidColorBrush(Color.Parse("#ebcb8b"));

    private int _hoverIndex = -1;

    // Coordinate-mapping params captured on the last Render, reused for pointer hit-testing.
    private double _mL, _mT, _plotW, _plotH, _xMin, _xMax, _yMin, _yMax;
    private bool _renderReady;

    public bool HitTest(Point point) => true;   // whole control area is hover-sensitive

    public override void Render(DrawingContext ctx)
    {
        _renderReady = false;
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 20 || h < 20) return;

        ctx.DrawRectangle(_bgBrush, null, new Rect(0, 0, w, h));

        var pts = Points;
        if (pts is null || pts.Count == 0)
        {
            var ft = MakeText("No data yet.", 14, _emptyBrush);
            ctx.DrawText(ft, new Point(w / 2 - ft.Width / 2, h / 2 - ft.Height / 2));
            return;
        }

        var yErr = YErrors;
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
        double yPad = (yMax - yMin) * 0.08;
        yMin -= yPad; yMax += yPad;

        bool hasTitle = !string.IsNullOrEmpty(Title);
        const double mL = 64, mR = 16, mB = 40;
        double mT = hasTitle ? 34 : 14;
        double plotW = w - mL - mR;
        double plotH = h - mT - mB;
        if (plotW < 10 || plotH < 10) return;

        // Capture mapping params for pointer hit-testing / tooltip (issue #27)
        _mL = mL; _mT = mT; _plotW = plotW; _plotH = plotH;
        _xMin = xMin; _xMax = xMax; _yMin = yMin; _yMax = yMax;
        _renderReady = true;

        if (hasTitle)
        {
            var titleFt = MakeText(Title, 14, _labelBrush);
            ctx.DrawText(titleFt, new Point(mL + plotW / 2 - titleFt.Width / 2, 8));
        }

        double XToPx(double x) => mL + (x - xMin) / (xMax - xMin) * plotW;
        double YToPx(double y) => InvertY
            ? mT + (y - yMin) / (yMax - yMin) * plotH
            : mT + plotH - (y - yMin) / (yMax - yMin) * plotH;

        // ── Grid + axes ────────────────────────────────────────────────────
        for (int i = 0; i <= 4; i++)
        {
            double gy = mT + plotH * i / 4.0;
            ctx.DrawLine(_gridPen, new Point(mL, gy), new Point(mL + plotW, gy));
            double yVal = InvertY ? yMin + (yMax - yMin) * i / 4.0 : yMax - (yMax - yMin) * i / 4.0;
            var ft = MakeText(yVal.ToString("F3"), 10, _labelBrush);
            ctx.DrawText(ft, new Point(mL - ft.Width - 6, gy - ft.Height / 2));
        }

        ctx.DrawLine(_axisPen, new Point(mL, mT), new Point(mL, mT + plotH));
        ctx.DrawLine(_axisPen, new Point(mL, mT + plotH), new Point(mL + plotW, mT + plotH));

        for (int i = 0; i <= 4; i++)
        {
            double gx = mL + plotW * i / 4.0;
            double xVal = xMin + (xMax - xMin) * i / 4.0;
            var ft = MakeText(xVal.ToString("F3"), 10, _labelBrush);
            ctx.DrawText(ft, new Point(gx - ft.Width / 2, mT + plotH + 4));
        }

        if (!string.IsNullOrEmpty(XLabel))
        {
            var xLbl = MakeText(XLabel, 12, _labelBrush);
            ctx.DrawText(xLbl, new Point(mL + plotW / 2 - xLbl.Width / 2, h - 14));
        }
        if (!string.IsNullOrEmpty(YLabel))
        {
            var yLbl = MakeText(YLabel, 12, _labelBrush);
            using (ctx.PushTransform(Matrix.CreateRotation(-Math.PI / 2) *
                                     Matrix.CreateTranslation(12, mT + plotH / 2 + yLbl.Width / 2)))
                ctx.DrawText(yLbl, new Point(0, 0));
        }

        // ── Marker (e.g. best period) ──────────────────────────────────────
        if (MarkerX is { } mx && mx >= xMin && mx <= xMax)
        {
            double gx = XToPx(mx);
            ctx.DrawLine(_markerPen, new Point(gx, mT), new Point(gx, mT + plotH));
        }

        // ── Series ─────────────────────────────────────────────────────────
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
            IBrush pointBrush = TryParseColor(PointColor, out var c) ? new SolidColorBrush(c) : _pointBrush;
            IPen   errPen     = new Pen(pointBrush, 1.0);

            for (int i = 0; i < pts.Count; i++)
            {
                var p  = pts[i];
                var px = XToPx(p.X);
                var py = YToPx(p.Y);

                if (yErr is not null && i < yErr.Count && yErr[i] > 0)
                {
                    double pyLo = YToPx(p.Y - yErr[i]);
                    double pyHi = YToPx(p.Y + yErr[i]);
                    ctx.DrawLine(errPen, new Point(px, pyLo), new Point(px, pyHi));
                }
                ctx.DrawEllipse(pointBrush, null, new Point(px, py), 2.2, 2.2);
            }
        }

        // ── Hover highlight + tooltip (issue #27) ──────────────────────────
        if (_hoverIndex >= 0 && _hoverIndex < pts.Count)
            DrawTooltip(ctx, pts, _hoverIndex, w, h);
    }

    private void DrawTooltip(DrawingContext ctx, IReadOnlyList<PlotPoint> pts, int i, double w, double h)
    {
        var p  = pts[i];
        var px = MapX(p.X);
        var py = MapY(p.Y);

        // ring around the hovered point
        ctx.DrawEllipse(null, new Pen(_hoverRing, 1.6), new Point(px, py), 5, 5);

        // build tooltip lines
        var errs = PointErrors;
        var lbls = PointLabels;
        bool isPhase = (XLabel ?? "").Contains("Phase", StringComparison.OrdinalIgnoreCase);
        var lines = new List<string>
        {
            $"{(string.IsNullOrEmpty(XLabel) ? "X" : XLabel)}: {p.X.ToString(isPhase ? "F4" : "F5")}",
            errs is not null && i < errs.Count
                ? $"Mag: {p.Y:F4} ± {errs[i]:F4}"
                : $"Mag: {p.Y:F4}",
        };
        if (lbls is not null && i < lbls.Count && !string.IsNullOrEmpty(lbls[i]))
            lines.Add(lbls[i]);

        var texts = lines.Select(t => MakeText(t, 11, _tipTextBrush)).ToList();
        double boxW = texts.Max(t => t.Width) + 16;
        double boxH = texts.Sum(t => t.Height) + 6 * (texts.Count - 1) + 12;

        // place the box near the point, clamped inside the control
        double bx = px + 12, by = py - boxH - 8;
        if (bx + boxW > w - 4) bx = px - boxW - 12;
        if (bx < 4) bx = 4;
        if (by < 4) by = py + 12;
        if (by + boxH > h - 4) by = h - boxH - 4;

        ctx.DrawRectangle(_tipBgBrush, _tipBorderPen, new Rect(bx, by, boxW, boxH), 4, 4);
        double ty = by + 6;
        foreach (var t in texts)
        {
            ctx.DrawText(t, new Point(bx + 8, ty));
            ty += t.Height + 6;
        }
    }

    private double MapX(double x) => _mL + (x - _xMin) / (_xMax - _xMin) * _plotW;
    private double MapY(double y) => InvertY
        ? _mT + (y - _yMin) / (_yMax - _yMin) * _plotH
        : _mT + _plotH - (y - _yMin) / (_yMax - _yMin) * _plotH;

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pts = Points;
        if (!_renderReady || pts is null || pts.Count == 0) { SetHover(-1); return; }

        var m = e.GetPosition(this);
        int best = -1;
        double bestD2 = 12 * 12;   // hit radius (px)
        for (int i = 0; i < pts.Count; i++)
        {
            double dx = MapX(pts[i].X) - m.X;
            double dy = MapY(pts[i].Y) - m.Y;
            double d2 = dx * dx + dy * dy;
            if (d2 < bestD2) { bestD2 = d2; best = i; }
        }
        SetHover(best);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        SetHover(-1);
    }

    private void SetHover(int index)
    {
        if (index == _hoverIndex) return;
        _hoverIndex = index;
        InvalidateVisual();
    }

    private static bool TryParseColor(string s, out Color color)
    {
        try { color = Color.Parse(s); return true; }
        catch { color = default; return false; }
    }

    private static FormattedText MakeText(string text, double size, IBrush brush)
        => new(text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
               new Typeface("Arial"), size, brush);
}
