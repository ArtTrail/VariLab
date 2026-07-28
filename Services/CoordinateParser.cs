using System;
using System.Globalization;

namespace VariLab.Services;

/// <summary>
/// Parses RA/Dec text fields that may be entered as plain decimal degrees or as
/// sexagesimal (RA: hours:minutes:seconds, Dec: degrees:minutes:seconds) — the
/// format FITS headers (OBJCTRA/OBJCTDEC) and most astronomy references use.
/// </summary>
public static class CoordinateParser
{
    /// <summary>Parse RA. Sexagesimal input is hours (converted ×15 to degrees).</summary>
    public static bool TryParseRa(string text, out double degrees)
        => TryParseSexagesimal(text, hoursToDegrees: true, out degrees);

    /// <summary>Parse Dec. Sexagesimal input is degrees (sign taken from the first token).</summary>
    public static bool TryParseDec(string text, out double degrees)
        => TryParseSexagesimal(text, hoursToDegrees: false, out degrees);

    private static bool TryParseSexagesimal(string text, bool hoursToDegrees, out double degrees)
    {
        degrees = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();

        // Plain decimal degrees — the common case.
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var plain))
        {
            degrees = plain;
            return true;
        }

        // Sexagesimal: "HH:MM:SS", "HH MM SS", "+DD:MM:SS", "-DD MM SS.S", 2- or 3-component.
        var parts = text.Split([':', ' '], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 2 or > 3) return false;

        bool negative = parts[0].StartsWith('-');
        if (!double.TryParse(parts[0].TrimStart('+', '-'), NumberStyles.Float, CultureInfo.InvariantCulture, out var p0)) return false;
        if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var p1)) return false;
        double p2 = 0;
        if (parts.Length == 3 && !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out p2)) return false;

        double magnitude = p0 + p1 / 60.0 + p2 / 3600.0;
        if (negative) magnitude = -magnitude;
        degrees = hoursToDegrees ? magnitude * 15.0 : magnitude;
        return true;
    }
}
