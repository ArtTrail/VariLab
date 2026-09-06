using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using VariLab.Models;
using VariLab.Views.Controls;

namespace VariLab.Services;

/// <summary>
/// Builds the annotated field-image export: crops the reference frame around the target and
/// comp ensemble, applies a percentile stretch (so faint field stars and the target/comps are
/// all visible together, the way a quick-look FITS viewer would show them — a straight
/// min/max stretch would be dominated by a handful of saturated-looking bright pixels), and
/// renders it off-screen with <see cref="FieldImageControl"/> using the same
/// render-to-PNG-bytes technique as <see cref="PlotExportService"/>.
/// </summary>
public static class FieldImageService
{
    /// <summary>Crops <paramref name="image"/> to a padded bounding box around the target and
    /// comps, stretches it, and renders the annotated PNG. Returns null if the target/comps
    /// fall entirely outside the image (shouldn't happen in practice — they were measured on
    /// this same frame — but guards against a mismatched frame being passed in).</summary>
    public static byte[]? RenderFieldImagePng(
        float[,] image,
        FieldMarker target,
        IReadOnlyList<FieldMarker> comps,
        double apertureRadiusPx, double annulusInnerPx, double annulusOuterPx,
        string title, string subtitle,
        int width = 1100, int height = 900)
    {
        int rows = image.GetLength(0), cols = image.GetLength(1);

        var allX = comps.Select(c => c.X).Append(target.X).ToList();
        var allY = comps.Select(c => c.Y).Append(target.Y).ToList();
        double minX = allX.Min(), maxX = allX.Max();
        double minY = allY.Min(), maxY = allY.Max();

        double padX = Math.Max(80, (maxX - minX) * 0.12);
        double padY = Math.Max(80, (maxY - minY) * 0.12);

        int x0 = (int)Math.Floor(Math.Clamp(minX - padX, 0, cols - 1));
        int x1 = (int)Math.Ceiling(Math.Clamp(maxX + padX, 0, cols - 1));
        int y0 = (int)Math.Floor(Math.Clamp(minY - padY, 0, rows - 1));
        int y1 = (int)Math.Ceiling(Math.Clamp(maxY + padY, 0, rows - 1));
        int cropW = x1 - x0 + 1, cropH = y1 - y0 + 1;
        if (cropW < 2 || cropH < 2) return null;

        var bitmap = BuildStretchedBitmap(image, x0, y0, cropW, cropH);

        FieldMarker Shift(FieldMarker m) => m with { X = m.X - x0, Y = m.Y - y0 };
        var control = new FieldImageControl
        {
            Image           = bitmap,
            Title           = title,
            Subtitle        = subtitle,
            Target          = Shift(target),
            Comps           = comps.Select(Shift).ToList(),
            ApertureRadiusPx = apertureRadiusPx,
            AnnulusInnerPx   = annulusInnerPx,
            AnnulusOuterPx   = annulusOuterPx,
            Width  = width,
            Height = height,
        };

        control.Measure(new Size(width, height));
        control.Arrange(new Rect(0, 0, width, height));

        using var rtb = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        rtb.Render(control);

        using var ms = new MemoryStream();
        rtb.Save(ms);
        return ms.ToArray();
    }

    public static bool ExportFieldImagePng(
        string path,
        float[,] image,
        FieldMarker target,
        IReadOnlyList<FieldMarker> comps,
        double apertureRadiusPx, double annulusInnerPx, double annulusOuterPx,
        string title, string subtitle)
    {
        var bytes = RenderFieldImagePng(image, target, comps, apertureRadiusPx, annulusInnerPx, annulusOuterPx, title, subtitle);
        if (bytes is null) return false;

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(path, bytes);
        return true;
    }

    /// <summary>Percentile-clip stretch (1st–99.5th) mapped to an 8-bit grayscale bitmap —
    /// wide enough to keep faint field stars visible without a handful of bright pixels
    /// (the target's own core, most likely) crushing everything else to black.</summary>
    private static WriteableBitmap BuildStretchedBitmap(float[,] image, int x0, int y0, int cropW, int cropH)
    {
        var vals = new float[cropW * cropH];
        int k = 0;
        for (int y = 0; y < cropH; y++)
            for (int x = 0; x < cropW; x++)
                vals[k++] = image[y0 + y, x0 + x];

        var sorted = (float[])vals.Clone();
        Array.Sort(sorted);
        float lo = sorted[(int)(sorted.Length * 0.01)];
        float hi = sorted[(int)(sorted.Length * 0.995)];
        if (hi <= lo) hi = lo + 1f;

        byte Stretch(float v) => (byte)Math.Clamp((v - lo) / (hi - lo) * 255.0, 0, 255);

        var bmp = new WriteableBitmap(new PixelSize(cropW, cropH), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using (var fb = bmp.Lock())
        {
            var buffer = new byte[fb.RowBytes * cropH];
            for (int y = 0; y < cropH; y++)
            {
                int rowOff = y * fb.RowBytes;
                for (int x = 0; x < cropW; x++)
                {
                    byte v = Stretch(image[y0 + y, x0 + x]);
                    int off = rowOff + x * 4;
                    buffer[off]     = v; // B
                    buffer[off + 1] = v; // G
                    buffer[off + 2] = v; // R
                    buffer[off + 3] = 255;
                }
            }
            Marshal.Copy(buffer, 0, fb.Address, buffer.Length);
        }
        return bmp;
    }
}
