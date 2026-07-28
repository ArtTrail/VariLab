using Avalonia;
using Avalonia.Media.Imaging;
using System.Collections.Generic;
using System.IO;
using VariLab.Models;
using VariLab.Views.Controls;

namespace VariLab.Services;

/// <summary>
/// Renders <see cref="VariabilityChartControl"/> plots (matplotlib-style, matching
/// EXOTIC's Stellar_Variability.png) off-screen to PNG bytes/files, for use both
/// as a standalone file export and for embedding into the Excel report. Defaults to
/// the standard astronomical magnitude convention (Y axis inverted — brighter/lower
/// magnitude numbers plotted higher, fainter/larger numbers lower), matching the
/// live in-app Results tab charts (<c>ScatterPlotControl</c> with InvertY=true).
/// </summary>
public static class PlotExportService
{
    public static void ExportStellarVariabilityPng(
        string path,
        IReadOnlyList<PlotPoint> points,
        IReadOnlyList<double> yErrors,
        string title, string xLabel, string yLabel,
        int width = 1200, int height = 800,
        string subtitle = "",
        bool invertY = true)
    {
        var bytes = RenderPngBytes(points, yErrors, title, xLabel, yLabel, width: width, height: height, subtitle: subtitle, invertY: invertY);

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(path, bytes);
    }

    public static byte[] RenderPngBytes(
        IReadOnlyList<PlotPoint> points,
        IReadOnlyList<double>? yErrors,
        string title, string xLabel, string yLabel,
        bool drawLine = false, double? markerX = null,
        int width = 900, int height = 600,
        string subtitle = "",
        bool invertY = true)
    {
        var control = new VariabilityChartControl
        {
            Points   = points,
            YErrors  = yErrors,
            Title    = title,
            Subtitle = subtitle,
            XLabel   = xLabel,
            YLabel   = yLabel,
            DrawLine = drawLine,
            MarkerX  = markerX,
            Width    = width,
            Height   = height,
            InvertY  = invertY,
        };

        control.Measure(new Size(width, height));
        control.Arrange(new Rect(0, 0, width, height));

        using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        bitmap.Render(control);

        using var ms = new MemoryStream();
        bitmap.Save(ms);
        return ms.ToArray();
    }
}
