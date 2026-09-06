using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using System.Collections.Generic;
using System.Globalization;
using VariLab.Models;

namespace VariLab.Views.Controls;

/// <summary>
/// Renders a stretched FITS reference-frame crop with the target and every comp star
/// circled and labeled, plus the target's fixed aperture/annulus footprint — a visual
/// companion to the numeric CompDiagnostics export, for cases like a suspected nearby
/// star where seeing the actual field is more informative than another column of numbers.
/// All marker coordinates are in the *cropped* image's own pixel space (i.e. already
/// offset by whatever crop origin <see cref="Services.FieldImageService"/> chose) — this
/// control just scales that space to fit its bounds, nothing more.
/// </summary>
public class FieldImageControl : Control
{
    public static readonly StyledProperty<Bitmap?> ImageProperty =
        AvaloniaProperty.Register<FieldImageControl, Bitmap?>(nameof(Image));

    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<FieldImageControl, string>(nameof(Title), "");

    public static readonly StyledProperty<string> SubtitleProperty =
        AvaloniaProperty.Register<FieldImageControl, string>(nameof(Subtitle), "");

    public static readonly StyledProperty<FieldMarker?> TargetProperty =
        AvaloniaProperty.Register<FieldImageControl, FieldMarker?>(nameof(Target));

    public static readonly StyledProperty<IReadOnlyList<FieldMarker>?> CompsProperty =
        AvaloniaProperty.Register<FieldImageControl, IReadOnlyList<FieldMarker>?>(nameof(Comps));

    public static readonly StyledProperty<double> ApertureRadiusPxProperty =
        AvaloniaProperty.Register<FieldImageControl, double>(nameof(ApertureRadiusPx));

    public static readonly StyledProperty<double> AnnulusInnerPxProperty =
        AvaloniaProperty.Register<FieldImageControl, double>(nameof(AnnulusInnerPx));

    public static readonly StyledProperty<double> AnnulusOuterPxProperty =
        AvaloniaProperty.Register<FieldImageControl, double>(nameof(AnnulusOuterPx));

    public Bitmap? Image                          { get => GetValue(ImageProperty);           set => SetValue(ImageProperty, value); }
    public string Title                           { get => GetValue(TitleProperty);           set => SetValue(TitleProperty, value); }
    public string Subtitle                        { get => GetValue(SubtitleProperty);        set => SetValue(SubtitleProperty, value); }
    public FieldMarker? Target                    { get => GetValue(TargetProperty);          set => SetValue(TargetProperty, value); }
    public IReadOnlyList<FieldMarker>? Comps      { get => GetValue(CompsProperty);            set => SetValue(CompsProperty, value); }
    public double ApertureRadiusPx                { get => GetValue(ApertureRadiusPxProperty); set => SetValue(ApertureRadiusPxProperty, value); }
    public double AnnulusInnerPx                  { get => GetValue(AnnulusInnerPxProperty);   set => SetValue(AnnulusInnerPxProperty, value); }
    public double AnnulusOuterPx                  { get => GetValue(AnnulusOuterPxProperty);   set => SetValue(AnnulusOuterPxProperty, value); }

    static FieldImageControl()
    {
        AffectsRender<FieldImageControl>(ImageProperty, TitleProperty, SubtitleProperty, TargetProperty,
                                          CompsProperty, ApertureRadiusPxProperty, AnnulusInnerPxProperty, AnnulusOuterPxProperty);
    }

    private static readonly IBrush _bgBrush        = Brushes.White;
    private static readonly IBrush _blackBrush     = Brushes.Black;
    private static readonly IBrush _emptyBrush     = new SolidColorBrush(Color.Parse("#888888"));
    private static readonly IBrush _targetBrush    = new SolidColorBrush(Color.Parse("#a60000")); // deep red
    private static readonly IBrush _compBrush      = new SolidColorBrush(Color.Parse("#0a6e0a")); // deep green
    private static readonly IBrush _annulusBrush   = new SolidColorBrush(Color.Parse("#a0a0a0")); // muted grey

    private static readonly IBrush _haloBrush       = new SolidColorBrush(Color.Parse("#ffffff"));

    private static readonly IPen _targetAperturePen = new Pen(_targetBrush, 3.5);
    private static readonly IPen _compAperturePen   = new Pen(_compBrush, 3.0);
    private static readonly IPen _annulusPen        = new Pen(_annulusBrush, 1.4, dashStyle: DashStyle.Dash);

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 20 || h < 20) return;

        ctx.DrawRectangle(_bgBrush, null, new Rect(0, 0, w, h));

        var img = Image;
        if (img is null)
        {
            var ft = MakeText("No reference frame available.", 14, _emptyBrush);
            ctx.DrawText(ft, new Point(w / 2 - ft.Width / 2, h / 2 - ft.Height / 2));
            return;
        }

        bool hasTitle = !string.IsNullOrEmpty(Title);
        var subtitleLines = string.IsNullOrEmpty(Subtitle) ? [] : Subtitle.Split('\n');
        bool hasSubtitle = subtitleLines.Length > 0;
        const double margin = 16;
        double headerY = 10;
        double mT = !hasTitle && !hasSubtitle ? margin : 10 + (hasTitle ? 20 : 0) + subtitleLines.Length * 15 + 8;

        if (hasTitle)
        {
            var titleFt = MakeText(Title, 15, _blackBrush);
            ctx.DrawText(titleFt, new Point(w / 2 - titleFt.Width / 2, headerY));
            headerY += 20;
        }
        foreach (var line in subtitleLines)
        {
            var subFt = MakeText(line, 11, _blackBrush);
            ctx.DrawText(subFt, new Point(w / 2 - subFt.Width / 2, headerY));
            headerY += 15;
        }

        double availX = margin, availY = mT, availW = w - 2 * margin, availH = h - mT - margin;
        if (availW < 10 || availH < 10) return;

        double imgW = img.PixelSize.Width, imgH = img.PixelSize.Height;
        double scale = System.Math.Min(availW / imgW, availH / imgH);
        double drawW = imgW * scale, drawH = imgH * scale;
        double ox = availX + (availW - drawW) / 2, oy = availY + (availH - drawH) / 2;

        ctx.DrawImage(img, new Rect(0, 0, imgW, imgH), new Rect(ox, oy, drawW, drawH));

        Point ImgToScreen(double x, double y) => new(ox + x * scale, oy + y * scale);

        var comps = Comps;
        if (comps is not null)
        {
            foreach (var c in comps)
            {
                var p = ImgToScreen(c.X, c.Y);
                ctx.DrawEllipse(null, _compAperturePen, p, ApertureRadiusPx * scale, ApertureRadiusPx * scale);
                DrawLabelWithHalo(ctx, c.Label, 16, _compBrush,
                    new Point(p.X + ApertureRadiusPx * scale + 4, p.Y));
            }
        }

        if (Target is { } t)
        {
            var p = ImgToScreen(t.X, t.Y);
            if (AnnulusOuterPx > 0)
                ctx.DrawEllipse(null, _annulusPen, p, AnnulusOuterPx * scale, AnnulusOuterPx * scale);
            if (AnnulusInnerPx > 0)
                ctx.DrawEllipse(null, _annulusPen, p, AnnulusInnerPx * scale, AnnulusInnerPx * scale);
            ctx.DrawEllipse(null, _targetAperturePen, p, ApertureRadiusPx * scale, ApertureRadiusPx * scale);
            DrawLabelWithHalo(ctx, t.Label, 18, _targetBrush,
                new Point(p.X + ApertureRadiusPx * scale + 4, p.Y));
        }
    }

    /// <summary>Draws label text with a white halo (offset copies behind the colored text) so
    /// it stays legible against a grainy, high-contrast-noise stretched FITS background — plain
    /// colored text at any reasonable size still gets lost in that kind of speckle.</summary>
    private static void DrawLabelWithHalo(DrawingContext ctx, string text, double size, IBrush brush, Point anchor)
    {
        var lbl  = MakeText(text, size, brush, bold: true);
        var halo = MakeText(text, size, _haloBrush, bold: true);
        var origin = new Point(anchor.X, anchor.Y - lbl.Height / 2);
        foreach (var (dx, dy) in HaloOffsets)
            ctx.DrawText(halo, new Point(origin.X + dx, origin.Y + dy));
        ctx.DrawText(lbl, origin);
    }

    private static readonly (double, double)[] HaloOffsets =
        [(-1.5, -1.5), (1.5, -1.5), (-1.5, 1.5), (1.5, 1.5), (-1.5, 0), (1.5, 0), (0, -1.5), (0, 1.5)];

    private static FormattedText MakeText(string text, double size, IBrush brush, bool bold = false)
        => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
               new Typeface("Arial", weight: bold ? FontWeight.Bold : FontWeight.Normal), size, brush);
}
