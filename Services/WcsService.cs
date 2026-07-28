using System;

namespace VariLab.Services;

/// <summary>
/// Minimal FITS WCS helper: reads CD-matrix or CDELT+PC-matrix keywords
/// and converts between sky coordinates and pixel coordinates using the
/// gnomonic (TAN) projection, which is what ASTAP and astrometry.net write.
/// </summary>
public static class WcsService
{
    public record WcsInfo(
        double Crpix1, double Crpix2,
        double Crval1, double Crval2,   // RA, Dec in degrees
        double Cd11,   double Cd12,
        double Cd21,   double Cd22);    // CD matrix (degrees/pixel)

    /// <summary>
    /// Read WCS from a FITS header.  Returns null if required keywords are absent.
    /// Supports CD matrix (CD1_1 etc.) and CDELT+PC matrix.
    /// </summary>
    public static WcsInfo? ReadWcs(FitsHeaderService.FitsHeader hdr)
    {
        var crpix1 = hdr.GetDouble("CRPIX1");
        var crpix2 = hdr.GetDouble("CRPIX2");
        var crval1 = hdr.GetDouble("CRVAL1");
        var crval2 = hdr.GetDouble("CRVAL2");
        if (crpix1 is null || crpix2 is null || crval1 is null || crval2 is null)
            return null;

        double cd11, cd12, cd21, cd22;

        var c11 = hdr.GetDouble("CD1_1");
        if (c11 is not null)
        {
            cd11 = c11.Value;
            cd12 = hdr.GetDouble("CD1_2") ?? 0.0;
            cd21 = hdr.GetDouble("CD2_1") ?? 0.0;
            cd22 = hdr.GetDouble("CD2_2") ?? 0.0;
        }
        else
        {
            var cdelt1 = hdr.GetDouble("CDELT1");
            var cdelt2 = hdr.GetDouble("CDELT2");
            if (cdelt1 is null || cdelt2 is null) return null;
            var pc11 = hdr.GetDouble("PC1_1") ?? 1.0;
            var pc12 = hdr.GetDouble("PC1_2") ?? 0.0;
            var pc21 = hdr.GetDouble("PC2_1") ?? 0.0;
            var pc22 = hdr.GetDouble("PC2_2") ?? 1.0;
            cd11 = cdelt1.Value * pc11;
            cd12 = cdelt1.Value * pc12;
            cd21 = cdelt2.Value * pc21;
            cd22 = cdelt2.Value * pc22;
        }

        return new WcsInfo(crpix1.Value, crpix2.Value,
                           crval1.Value, crval2.Value,
                           cd11, cd12, cd21, cd22);
    }

    /// <summary>
    /// Convert sky (RA/Dec in decimal degrees) → FITS pixel (1-based).
    /// Returns null if the point is behind the tangent plane or the CD
    /// matrix is degenerate.
    /// </summary>
    public static (int X, int Y)? SkyToPixel(WcsInfo wcs, double ra, double dec)
    {
        double alpha0 = wcs.Crval1 * Math.PI / 180.0;
        double delta0 = wcs.Crval2 * Math.PI / 180.0;
        double alpha  = ra  * Math.PI / 180.0;
        double delta  = dec * Math.PI / 180.0;

        double denom = Math.Sin(delta0) * Math.Sin(delta)
                     + Math.Cos(delta0) * Math.Cos(delta) * Math.Cos(alpha - alpha0);
        if (Math.Abs(denom) < 1e-10) return null;

        double scale = 180.0 / Math.PI;
        double xi  = scale *  Math.Cos(delta) * Math.Sin(alpha - alpha0) / denom;
        double eta = scale * (Math.Cos(delta0) * Math.Sin(delta)
                            - Math.Sin(delta0) * Math.Cos(delta) * Math.Cos(alpha - alpha0)) / denom;

        // Invert CD matrix: [dx, dy] = CD^{-1} [xi, eta]
        double det = wcs.Cd11 * wcs.Cd22 - wcs.Cd12 * wcs.Cd21;
        if (Math.Abs(det) < 1e-20) return null;

        double dx = ( wcs.Cd22 * xi  - wcs.Cd12 * eta) / det;
        double dy = (-wcs.Cd21 * xi  + wcs.Cd11 * eta) / det;

        int px = (int)Math.Round(dx + wcs.Crpix1);
        int py = (int)Math.Round(dy + wcs.Crpix2);
        return (px, py);
    }
}
