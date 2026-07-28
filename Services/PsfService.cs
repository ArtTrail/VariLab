using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace VariLab.Services;

/// <summary>
/// Reads FITS pixel data and measures PSF quality (FWHM, SNR, saturation)
/// using weighted 2nd-moment analysis — no external library required.
///
/// Supported FITS formats: uncompressed, BITPIX 8 / 16 / 32 / -32 / -64.
/// Rice/GZIP compressed images (.fz, ZTILE/ZCMPTYPE header keywords) return null
/// from ReadFitsPixels — the caller should skip PSF validation gracefully.
/// </summary>
public static class PsfService
{
    // ── Public result type ────────────────────────────────────────────────────

    public record PsfResult(
        bool   Success,
        double FwhmX,
        double FwhmY,
        double FwhmMean,
        double PeakAdu,
        double Snr,
        bool   Saturated,
        string Message = "");

    // ── FITS pixel reader ─────────────────────────────────────────────────────

    /// <summary>
    /// Read the primary image plane of an uncompressed FITS file.
    /// Returns float[row, col] (NAXIS2 × NAXIS1), FITS origin (row 0 = bottom).
    /// Returns null on failure or if the image is compressed.
    /// </summary>
    public static float[,]? ReadFitsPixels(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

            int    bitpix = 0, naxis1 = 0, naxis2 = 0;
            double bzero  = 0.0, bscale = 1.0;
            bool   compressed = false, end = false;
            var    block = new byte[2880];

            while (!end)
            {
                int read = fs.Read(block, 0, 2880);
                if (read < 80) break;

                for (int i = 0; i + 79 < read; i += 80)
                {
                    string kw  = Encoding.ASCII.GetString(block, i, 8).TrimEnd();
                    string rec = Encoding.ASCII.GetString(block, i, 80);

                    if (kw == "END") { end = true; break; }

                    // Rice / GZIP compression markers
                    if (kw is "ZTILE1" or "ZTILE2" or "ZCMPTYPE" or "ZBITPIX"
                           or "ZIMAGE" or "ZPCOUNT" or "ZGCOUNT")
                    { compressed = true; break; }

                    if (rec.Length > 9 && rec[8] == '=')
                    {
                        string val = rec[9..].Split('/')[0].Trim().Trim('\'').Trim();
                        switch (kw)
                        {
                            case "BITPIX": int.TryParse(val, out bitpix);  break;
                            case "NAXIS1": int.TryParse(val, out naxis1);  break;
                            case "NAXIS2": int.TryParse(val, out naxis2);  break;
                            case "BZERO":
                                double.TryParse(val,
                                    System.Globalization.NumberStyles.Any,
                                    System.Globalization.CultureInfo.InvariantCulture, out bzero);
                                break;
                            case "BSCALE":
                                double.TryParse(val,
                                    System.Globalization.NumberStyles.Any,
                                    System.Globalization.CultureInfo.InvariantCulture, out bscale);
                                break;
                        }
                    }
                }
                if (compressed) break;
            }

            if (compressed || naxis1 <= 0 || naxis2 <= 0 || bitpix == 0) return null;
            if (bscale == 0.0) bscale = 1.0;

            int    bpp        = Math.Abs(bitpix) / 8;
            long   totalBytes = (long)naxis1 * naxis2 * bpp;
            if (totalBytes > 600_000_000L) return null;  // 600 MB sanity cap

            var raw       = new byte[totalBytes];
            int remaining = (int)totalBytes;
            int offset    = 0;
            while (remaining > 0)
            {
                int got = fs.Read(raw, offset, Math.Min(remaining, 65536));
                if (got <= 0) break;
                offset    += got;
                remaining -= got;
            }

            var image = new float[naxis2, naxis1];

            for (int row = 0; row < naxis2; row++)
            {
                for (int col = 0; col < naxis1; col++)
                {
                    int    idx = (row * naxis1 + col) * bpp;
                    double raw_val;

                    switch (bitpix)
                    {
                        case 8:
                            raw_val = raw[idx];
                            break;
                        case 16:
                        {
                            // Big-endian signed short; BZERO=32768 gives unsigned 0-65535
                            short s = (short)((raw[idx] << 8) | raw[idx + 1]);
                            raw_val = s;
                            break;
                        }
                        case 32:
                        {
                            int v = (raw[idx] << 24) | (raw[idx+1] << 16)
                                  | (raw[idx+2] << 8)  |  raw[idx+3];
                            raw_val = v;
                            break;
                        }
                        case -32:
                        {
                            // Big-endian IEEE 754 single — reverse bytes
                            byte[] buf = [raw[idx+3], raw[idx+2], raw[idx+1], raw[idx]];
                            raw_val = BitConverter.ToSingle(buf, 0);
                            break;
                        }
                        case -64:
                        {
                            byte[] buf = [raw[idx+7], raw[idx+6], raw[idx+5], raw[idx+4],
                                          raw[idx+3], raw[idx+2], raw[idx+1], raw[idx]];
                            raw_val = BitConverter.ToDouble(buf, 0);
                            break;
                        }
                        default:
                            raw_val = 0;
                            break;
                    }

                    image[row, col] = (float)(bscale * raw_val + bzero);
                }
            }

            return image;
        }
        catch (Exception ex)
        {
            SessionLogService.Write($"[PSF] FITS pixel read failed: {ex.Message}");
            return null;
        }
    }

    // ── Header-derived detector parameters ────────────────────────────────────

    /// <summary>Estimate saturation level (ADU) from FITS header.</summary>
    public static double EstimateSaturation(FitsHeaderService.FitsHeader hdr)
    {
        var sat = hdr.GetDouble("SATURATE");
        if (sat.HasValue && sat.Value > 0) return sat.Value;

        int    bitpix = hdr.GetInt("BITPIX") ?? 16;
        double bzero  = hdr.GetDouble("BZERO") ?? 0.0;

        return bitpix switch
        {
            16  => (bzero > 0 ? 65535.0 : 32767.0) * 0.90,
            32  => 2_147_483_647.0 * 0.80,
            8   => 255.0 * 0.90,
            -32 => 1e9,
            -64 => 1e9,
            _   => 60_000.0
        };
    }

    /// <summary>
    /// Return detector gain in e-/ADU from EGAIN or GAIN header keywords.
    /// Sanity-checked to 0.1–10 e-/ADU; falls back to 1.0.
    /// </summary>
    public static double GetGain(FitsHeaderService.FitsHeader hdr)
    {
        var egain = hdr.GetDouble("EGAIN");
        if (egain.HasValue && egain.Value is >= 0.1 and <= 10.0) return egain.Value;

        var gain = hdr.GetDouble("GAIN");
        if (gain.HasValue && gain.Value is >= 0.1 and <= 10.0) return gain.Value;

        return 1.0;
    }

    // ── PSF measurement ───────────────────────────────────────────────────────

    /// <summary>
    /// Measure PSF quality for a star centered at (cx, cy) using:
    ///   • Weighted 2nd-moment analysis for FWHM_x and FWHM_y
    ///   • Aperture photometry (2×FWHM radius) for SNR
    ///   • Peak ADU search in a 21×21 box for saturation detection
    ///
    /// Returns a PsfResult; never throws.
    /// </summary>
    public static PsfResult Measure(
        float[,] image, int cx, int cy,
        double saturationAdu, double gainEPerAdu)
    {
        int rows = image.GetLength(0);
        int cols = image.GetLength(1);

        // Need at least 20px clearance from edge for the background annulus
        if (cx < 20 || cy < 20 || cx > cols - 21 || cy > rows - 21)
            return Fail("Near image edge");

        // ── 1. Peak ADU (21×21 search box) + saturation ──────────────────────
        double peakAdu = 0;
        for (int dy = -10; dy <= 10; dy++)
            for (int dx = -10; dx <= 10; dx++)
                peakAdu = Math.Max(peakAdu, image[cy + dy, cx + dx]);

        bool saturated = peakAdu >= saturationAdu;

        // ── 2. Background — median of annulus at 13–18 px ────────────────────
        var skyPixels = new List<float>(250);
        for (int dy = -20; dy <= 20; dy++)
        {
            int ry = cy + dy;
            if (ry < 0 || ry >= rows) continue;
            for (int dx = -20; dx <= 20; dx++)
            {
                double r = Math.Sqrt(dx * dx + dy * dy);
                if (r < 13 || r > 18) continue;
                int rx = cx + dx;
                if (rx < 0 || rx >= cols) continue;
                skyPixels.Add(image[ry, rx]);
            }
        }

        if (skyPixels.Count < 12) return Fail("Insufficient sky pixels");
        skyPixels.Sort();
        double background = skyPixels[skyPixels.Count / 2];

        // Robust sky sigma (MAD, scaled to a Gaussian-equivalent sigma) — needed
        // below to tell real starlight apart from sky noise. Without this, a faint,
        // low-SNR star (e.g. SNR~4) gets its FWHM measured from background noise:
        // roughly half of all sky pixels sit above the median by chance, and with
        // no significance threshold every one of them contributes a small positive
        // weight to the 2nd-moment sum. Spread uniformly across the whole 21×21
        // window (rather than clustered at the true, tiny stellar core), that noise
        // inflates the computed FWHM toward the window's own size — observed on a
        // real SNR~4 MObs target as FWHM=10.8px when the true FWHM was ~2px, which
        // cascaded into a wildly oversized photometry aperture for the whole run.
        double skyMad = skyPixels.Select(v => (double)Math.Abs(v - background))
                                  .OrderBy(v => v).ElementAt(skyPixels.Count / 2) * 1.4826;
        const double sigmaClipK = 2.0;
        double floor = background + sigmaClipK * skyMad;

        // ── 3. Weighted 2nd-moment FWHM (10px half-aperture) ─────────────────
        const int half = 10;
        double sumW = 0, sumWdx = 0, sumWdy = 0;

        for (int dy = -half; dy <= half; dy++)
            for (int dx = -half; dx <= half; dx++)
            {
                double w = Math.Max(0.0, image[cy + dy, cx + dx] - floor);
                sumW   += w;
                sumWdx += w * dx;
                sumWdy += w * dy;
            }

        if (sumW <= 0) return Fail("No flux above background");

        double xOff = sumWdx / sumW;  // centroid offset from initial (cx,cy)
        double yOff = sumWdy / sumW;

        double sumWxx = 0, sumWyy = 0;
        for (int dy = -half; dy <= half; dy++)
            for (int dx = -half; dx <= half; dx++)
            {
                double w  = Math.Max(0.0, image[cy + dy, cx + dx] - floor);
                double ex = dx - xOff;
                double ey = dy - yOff;
                sumWxx += w * ex * ex;
                sumWyy += w * ey * ey;
            }

        double sigmaX   = Math.Sqrt(sumWxx / sumW);
        double sigmaY   = Math.Sqrt(sumWyy / sumW);
        double fwhmX    = 2.355 * sigmaX;
        double fwhmY    = 2.355 * sigmaY;
        double fwhmMean = (fwhmX + fwhmY) / 2.0;

        if (fwhmMean < 0.3 || fwhmMean > 60)
            return Fail($"Unphysical FWHM ({fwhmMean:F1}px)");

        // ── 4. Aperture SNR (2×FWHM aperture radius) ─────────────────────────
        double aperR  = Math.Max(3.0, 2.0 * fwhmMean);
        double aperR2 = aperR * aperR;
        double flux   = 0;
        int    nPix   = 0;
        int    extent = (int)(aperR + 1.5);

        for (int dy = -extent; dy <= extent; dy++)
        {
            int ry = cy + dy;
            if (ry < 0 || ry >= rows) continue;
            for (int dx = -extent; dx <= extent; dx++)
            {
                if (dx * dx + dy * dy > aperR2) continue;
                int rx = cx + dx;
                if (rx < 0 || rx >= cols) continue;
                flux += Math.Max(0.0, image[ry, rx] - background);
                nPix++;
            }
        }

        if (flux <= 0) return Fail("Non-positive aperture flux");

        double gain      = Math.Max(0.1, gainEPerAdu);
        double noiseVar  = flux / gain + nPix * Math.Max(0, background) / gain;
        double snr       = noiseVar > 0 ? flux / Math.Sqrt(noiseVar) : 0;

        return new PsfResult(true, fwhmX, fwhmY, fwhmMean, peakAdu, snr, saturated);
    }

    // ── Fixed-aperture photometry (for multi-frame light curves) ─────────────

    public record ApertureResult(
        bool   Success,
        double Flux,
        double Background,
        double PeakAdu,
        double Snr,
        bool   Saturated,
        string Message = "");

    /// <summary>
    /// Aperture photometry with a caller-supplied fixed aperture/annulus radius —
    /// unlike <see cref="Measure"/>, the aperture size does not adapt per-star or
    /// per-frame, since a size that shifts with local seeing/flux would inject
    /// spurious flux changes into a differential light curve.
    /// Sky background is the sigma-clipped median of the annulus (2 passes, 3σ),
    /// rejecting contaminating stars in the annulus. (cx, cy) is a sub-pixel
    /// centre (typically from <see cref="RefineCentroid"/>) — the aperture/annulus
    /// masks are evaluated against the precise double position, not rounded first.
    /// </summary>
    public static ApertureResult MeasureAperture(
        float[,] image, double cx, double cy,
        double apertureRadiusPx, double annulusInnerPx, double annulusOuterPx,
        double saturationAdu, double gainEPerAdu)
    {
        int rows = image.GetLength(0);
        int cols = image.GetLength(1);
        int margin = (int)Math.Ceiling(annulusOuterPx) + 1;
        int icx = (int)Math.Round(cx);
        int icy = (int)Math.Round(cy);

        if (icx < margin || icy < margin || icx > cols - margin - 1 || icy > rows - margin - 1)
            return FailAperture("Near image edge");

        // ── 1. Sky annulus — sigma-clipped median (2 passes, 3σ) ─────────────
        var skyPixels = new List<double>(512);
        double innerR2 = annulusInnerPx * annulusInnerPx;
        double outerR2 = annulusOuterPx * annulusOuterPx;
        int    annExtent = (int)Math.Ceiling(annulusOuterPx) + 1;

        for (int dy = -annExtent; dy <= annExtent; dy++)
        {
            int ry = icy + dy;
            if (ry < 0 || ry >= rows) continue;
            for (int dx = -annExtent; dx <= annExtent; dx++)
            {
                int rx = icx + dx;
                if (rx < 0 || rx >= cols) continue;
                double ddx = rx - cx, ddy = ry - cy;
                double r2 = ddx * ddx + ddy * ddy;
                if (r2 < innerR2 || r2 > outerR2) continue;
                skyPixels.Add(image[ry, rx]);
            }
        }

        if (skyPixels.Count < 12) return FailAperture("Insufficient sky pixels");

        for (int pass = 0; pass < 2; pass++)
        {
            var sorted = skyPixels.OrderBy(v => v).ToList();
            double med = sorted[sorted.Count / 2];
            double mad = sorted.Select(v => Math.Abs(v - med)).OrderBy(v => v)
                                .ElementAt(sorted.Count / 2) * 1.4826;
            if (mad <= 0) break;
            var clipped = skyPixels.Where(v => Math.Abs(v - med) <= 3.0 * mad).ToList();
            if (clipped.Count < 12) break;
            skyPixels = clipped;
        }

        skyPixels.Sort();
        double background = skyPixels[skyPixels.Count / 2];

        // ── 2. Peak ADU within the aperture (saturation check) ───────────────
        double peakAdu = 0;
        double aperR2  = apertureRadiusPx * apertureRadiusPx;
        int    aperExtent = (int)Math.Ceiling(apertureRadiusPx) + 1;
        double apSum = 0;
        int    nPix = 0;

        for (int dy = -aperExtent; dy <= aperExtent; dy++)
        {
            int ry = icy + dy;
            if (ry < 0 || ry >= rows) continue;
            for (int dx = -aperExtent; dx <= aperExtent; dx++)
            {
                int rx = icx + dx;
                if (rx < 0 || rx >= cols) continue;
                double ddx = rx - cx, ddy = ry - cy;
                if (ddx * ddx + ddy * ddy > aperR2) continue;
                double v = image[ry, rx];
                peakAdu = Math.Max(peakAdu, v);
                apSum  += v;
                nPix++;
            }
        }

        // Sum raw pixel values first, then subtract the sky in one shot — NOT a per-pixel
        // max(0, v-background) clip. Clipping each pixel individually before summing would
        // discard negative noise fluctuations while keeping positive ones, biasing flux high
        // (worst at low SNR). Summing first lets noise cancel symmetrically, matching standard
        // aperture photometry practice.
        bool saturated = peakAdu >= saturationAdu;
        double flux = apSum - nPix * background;
        if (flux <= 0) return FailAperture("Non-positive aperture flux");

        double gain     = Math.Max(0.1, gainEPerAdu);
        double noiseVar = flux / gain + nPix * Math.Max(0, background) / gain;
        double snr      = noiseVar > 0 ? flux / Math.Sqrt(noiseVar) : 0;

        return new ApertureResult(true, flux, background, peakAdu, snr, saturated);
    }

    private static ApertureResult FailAperture(string msg) => new(false, 0, 0, 0, 0, false, msg);

    // ── Centroid refinement ───────────────────────────────────────────────────

    /// <summary>
    /// Refines a WCS-predicted pixel position to the actual local intensity
    /// centroid within <paramref name="searchRadiusPx"/>. <see cref="MeasureAperture"/>
    /// trusts its input position exactly, which is fragile whenever the plate
    /// solve is only accurate to a few pixels (undersampled data, or a solver
    /// like ASTAP that's known to be imprecise on some formats) and the
    /// aperture itself is only a few pixels across — a small, consistent
    /// centering error there clips real flux every frame, biasing the target
    /// fainter with no redundancy to average it out (unlike the multi-star
    /// comp ensemble). Two iterations of background-subtracted center-of-mass,
    /// each re-centered on the previous result.
    /// Falls back to the original (cx, cy) — never returns something worse —
    /// if there's no usable local flux, or if the refined position drifts more
    /// than <paramref name="searchRadiusPx"/> from the start (a sign it's about
    /// to latch onto a different, unrelated star rather than refine the right one).
    /// </summary>
    public static (double X, double Y) RefineCentroid(
        float[,] image, double cx, double cy, double searchRadiusPx)
    {
        int rows = image.GetLength(0);
        int cols = image.GetLength(1);
        int r = (int)Math.Ceiling(searchRadiusPx);

        double bx = cx, by = cy;

        for (int iter = 0; iter < 2; iter++)
        {
            int icx = (int)Math.Round(bx);
            int icy = (int)Math.Round(by);
            if (icx - r < 0 || icy - r < 0 || icx + r >= cols || icy + r >= rows)
                return (cx, cy);

            var vals = new List<float>((2 * r + 1) * (2 * r + 1));
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                    vals.Add(image[icy + dy, icx + dx]);
            vals.Sort();
            double bg = vals[vals.Count / 2];

            double sumW = 0, sumWx = 0, sumWy = 0;
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    double w = Math.Max(0.0, image[icy + dy, icx + dx] - bg);
                    sumW  += w;
                    sumWx += w * (icx + dx);
                    sumWy += w * (icy + dy);
                }

            if (sumW <= 0) return (cx, cy);

            bx = sumWx / sumW;
            by = sumWy / sumW;
        }

        double dist = Math.Sqrt((bx - cx) * (bx - cx) + (by - cy) * (by - cy));
        return dist <= searchRadiusPx ? (bx, by) : (cx, cy);
    }

    // ── Field FWHM median (for outlier threshold) ─────────────────────────────

    /// <summary>
    /// Compute the median FWHM from a list of per-star PSF results.
    /// Excludes failures and saturated stars.
    /// </summary>
    public static double MedianFwhm(IEnumerable<PsfResult> results)
    {
        var fwhms = results
            .Where(r => r.Success && !r.Saturated && r.FwhmMean > 0)
            .Select(r => r.FwhmMean)
            .OrderBy(x => x)
            .ToList();

        if (fwhms.Count == 0) return 0;
        int mid = fwhms.Count / 2;
        return fwhms.Count % 2 == 0 ? (fwhms[mid - 1] + fwhms[mid]) / 2.0 : fwhms[mid];
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static PsfResult Fail(string msg) =>
        new(false, 0, 0, 0, 0, 0, false, msg);
}
