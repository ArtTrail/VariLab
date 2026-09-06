using ClosedXML.Excel;
using System;
using System.Collections.Generic;
using System.IO;

namespace VariLab.Services;

/// <summary>
/// Writes an Excel (.xlsx) report: a "Data" sheet with the light curve rows, a "Plots"
/// sheet with every chart produced by <see cref="PlotExportService"/>, a "Comp Stars"
/// sheet with the full selected ensemble detail, and a "Rejected Comps" sheet listing
/// candidates that were tested but excluded during PSF validation, with why.
/// </summary>
public static class ExcelExportService
{
    public record ExportRow(double Jd, double Mag, double MagErr, double? Airmass);
    public record PlotImage(string Label, byte[] PngBytes, int Width, int Height);

    public static void Export(
        string path,
        string filterCode,
        IReadOnlyList<ExportRow> rows,
        IReadOnlyList<PlotImage> plots,
        IReadOnlyList<GaiaCompService.CompStarInfo> compStars,
        IReadOnlyList<GaiaCompService.RejectedCompInfo> rejectedComps,
        string photometryMethod = "Aperture",
        IReadOnlyList<CrowdingFlagService.CrowdingFlagResult>? crowdingFlags = null)
    {
        using var wb = new XLWorkbook();

        // ── Data ────────────────────────────────────────────────────────────
        // Row 1 is a one-line method note (Aperture vs PSF Fit) rather than part of the
        // header row — confirmed as worth surfacing everywhere after an Aperture run and a
        // PSF Fit run on the same target/night were otherwise indistinguishable at a glance.
        var data = wb.Worksheets.Add("Data");
        data.Cell(1, 1).Value = $"Photometry method: {photometryMethod}";
        data.Cell(1, 1).Style.Font.Italic = true;

        data.Cell(2, 1).Value = "JD";
        data.Cell(2, 2).Value = $"{filterCode}mag";
        data.Cell(2, 3).Value = "MagErr";
        data.Cell(2, 4).Value = "Airmass";
        data.Row(2).Style.Font.Bold = true;

        int r = 3;
        foreach (var row in rows)
        {
            data.Cell(r, 1).Value = row.Jd;
            data.Cell(r, 2).Value = row.Mag;
            data.Cell(r, 3).Value = row.MagErr;
            if (row.Airmass.HasValue) data.Cell(r, 4).Value = row.Airmass.Value;
            r++;
        }
        data.Columns().AdjustToContents();

        // ── Comp Stars (selected ensemble) ─────────────────────────────────
        var comps = wb.Worksheets.Add("Comp Stars");
        string[] compHeaders =
        [
            "C#", "X", "Y", "RA", "Dec", "Mag", "MagErr", "MagSource", "Filter",
            "FWHM px", "SNR", "Saturated", "Source", "GaiaId", "AUID", "Sep (arcsec)", "BP-RP", "RUWE",
        ];
        for (int i = 0; i < compHeaders.Length; i++) comps.Cell(1, i + 1).Value = compHeaders[i];
        comps.Row(1).Style.Font.Bold = true;

        int cr = 2;
        // "C#" (C1, C2, ...) reuses the exact same index-based labeling convention every other
        // comp label in the app already derives from this same list's order (field image
        // markers, CompStarRef labels for photometry) — issue #12, previously the only place a
        // comp's row couldn't be matched back to which "C#" it corresponds to.
        int compNumber = 1;
        foreach (var c in compStars)
        {
            comps.Cell(cr, 1).Value  = $"C{compNumber++}";
            comps.Cell(cr, 2).Value  = c.X;
            comps.Cell(cr, 3).Value  = c.Y;
            comps.Cell(cr, 4).Value  = c.Ra;
            comps.Cell(cr, 5).Value  = c.Dec;
            if (c.Mag.HasValue)    comps.Cell(cr, 6).Value  = c.Mag.Value;
            if (c.MagErr.HasValue) comps.Cell(cr, 7).Value  = c.MagErr.Value;
            comps.Cell(cr, 8).Value  = c.MagSource ?? "";
            comps.Cell(cr, 9).Value  = c.FilterBand;
            if (c.FwhmPx.HasValue) comps.Cell(cr, 10).Value = c.FwhmPx.Value;
            if (c.Snr.HasValue)    comps.Cell(cr, 11).Value = c.Snr.Value;
            comps.Cell(cr, 12).Value = c.Saturated;
            comps.Cell(cr, 13).Value = c.CatalogSource;
            comps.Cell(cr, 14).Value = c.GaiaId ?? "";
            comps.Cell(cr, 15).Value = c.Auid ?? "";
            comps.Cell(cr, 16).Value = c.SepArcsec;
            if (c.BpRp.HasValue) comps.Cell(cr, 17).Value = c.BpRp.Value;
            if (c.Ruwe.HasValue) comps.Cell(cr, 18).Value = c.Ruwe.Value;
            cr++;
        }
        comps.Columns().AdjustToContents();

        // ── Rejected Comps ──────────────────────────────────────────────────
        var rejected = wb.Worksheets.Add("Rejected Comps");
        string[] rejHeaders = ["X", "Y", "Mag", "MagSource", "Source", "Reason"];
        for (int i = 0; i < rejHeaders.Length; i++) rejected.Cell(1, i + 1).Value = rejHeaders[i];
        rejected.Row(1).Style.Font.Bold = true;

        int rr = 2;
        foreach (var c in rejectedComps)
        {
            rejected.Cell(rr, 1).Value = c.X;
            rejected.Cell(rr, 2).Value = c.Y;
            if (c.Mag.HasValue) rejected.Cell(rr, 3).Value = c.Mag.Value;
            rejected.Cell(rr, 4).Value = c.MagSource ?? "";
            rejected.Cell(rr, 5).Value = c.CatalogSource;
            rejected.Cell(rr, 6).Value = c.Reason;
            rr++;
        }
        rejected.Columns().AdjustToContents();

        // ── Crowding Flags ──────────────────────────────────────────────────
        // FWHM-residual regression results (CrowdingFlagService) — a star whose brightness
        // correlates with per-frame seeing even after accounting for its own expected
        // variability, the signature of a blended neighbor bleeding in more as seeing worsens.
        // Always present (even with zero rows) so its absence in a given report is a positive
        // "checked, found nothing" result rather than ambiguous with "never checked."
        var flags = wb.Worksheets.Add("Crowding Flags");
        string[] flagHeaders = ["Star", "r", "p-value", "N", "Message"];
        for (int i = 0; i < flagHeaders.Length; i++) flags.Cell(1, i + 1).Value = flagHeaders[i];
        flags.Row(1).Style.Font.Bold = true;

        if (crowdingFlags is { Count: > 0 })
        {
            int fr = 2;
            foreach (var f in crowdingFlags)
            {
                flags.Cell(fr, 1).Value = f.StarLabel;
                flags.Cell(fr, 2).Value = f.R;
                flags.Cell(fr, 3).Value = f.PValue;
                flags.Cell(fr, 4).Value = f.N;
                flags.Cell(fr, 5).Value = f.Message;
                fr++;
            }
        }
        else
        {
            flags.Cell(2, 1).Value = "No significant FWHM-residual correlation found.";
            flags.Cell(2, 1).Style.Font.Italic = true;
        }
        flags.Columns().AdjustToContents();

        // ── Plots ───────────────────────────────────────────────────────────
        var plotsSheet = wb.Worksheets.Add("Plots");
        int cursorRow = 1;
        foreach (var plot in plots)
        {
            plotsSheet.Cell(cursorRow, 1).Value = plot.Label;
            plotsSheet.Cell(cursorRow, 1).Style.Font.Bold = true;
            cursorRow += 1;

            using var ms = new MemoryStream(plot.PngBytes);
            var picture = plotsSheet.AddPicture(ms, plot.Label.Replace(" ", "_"));
            picture.MoveTo(plotsSheet.Cell(cursorRow, 1));
            picture.WithSize(plot.Width, plot.Height);

            // Approximate rows spanned by the image (default row height ~20px) plus a gap.
            cursorRow += (int)Math.Ceiling(plot.Height / 20.0) + 2;
        }
        plotsSheet.Column(1).Width = 14;

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        wb.SaveAs(path);
    }
}
