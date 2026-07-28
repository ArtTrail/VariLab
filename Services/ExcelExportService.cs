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
        IReadOnlyList<GaiaCompService.RejectedCompInfo> rejectedComps)
    {
        using var wb = new XLWorkbook();

        // ── Data ────────────────────────────────────────────────────────────
        var data = wb.Worksheets.Add("Data");
        data.Cell(1, 1).Value = "JD";
        data.Cell(1, 2).Value = $"{filterCode}mag";
        data.Cell(1, 3).Value = "MagErr";
        data.Cell(1, 4).Value = "Airmass";
        data.Row(1).Style.Font.Bold = true;

        int r = 2;
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
            "X", "Y", "RA", "Dec", "Mag", "MagErr", "MagSource", "Filter",
            "FWHM px", "SNR", "Saturated", "Source", "GaiaId", "AUID", "Sep (arcsec)", "BP-RP", "RUWE",
        ];
        for (int i = 0; i < compHeaders.Length; i++) comps.Cell(1, i + 1).Value = compHeaders[i];
        comps.Row(1).Style.Font.Bold = true;

        int cr = 2;
        foreach (var c in compStars)
        {
            comps.Cell(cr, 1).Value  = c.X;
            comps.Cell(cr, 2).Value  = c.Y;
            comps.Cell(cr, 3).Value  = c.Ra;
            comps.Cell(cr, 4).Value  = c.Dec;
            if (c.Mag.HasValue)    comps.Cell(cr, 5).Value  = c.Mag.Value;
            if (c.MagErr.HasValue) comps.Cell(cr, 6).Value  = c.MagErr.Value;
            comps.Cell(cr, 7).Value  = c.MagSource ?? "";
            comps.Cell(cr, 8).Value  = c.FilterBand;
            if (c.FwhmPx.HasValue) comps.Cell(cr, 9).Value  = c.FwhmPx.Value;
            if (c.Snr.HasValue)    comps.Cell(cr, 10).Value = c.Snr.Value;
            comps.Cell(cr, 11).Value = c.Saturated;
            comps.Cell(cr, 12).Value = c.CatalogSource;
            comps.Cell(cr, 13).Value = c.GaiaId ?? "";
            comps.Cell(cr, 14).Value = c.Auid ?? "";
            comps.Cell(cr, 15).Value = c.SepArcsec;
            if (c.BpRp.HasValue) comps.Cell(cr, 16).Value = c.BpRp.Value;
            if (c.Ruwe.HasValue) comps.Cell(cr, 17).Value = c.Ruwe.Value;
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
