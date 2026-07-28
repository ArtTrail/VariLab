using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace VariLab.Services;

/// <summary>
/// Writes the AAVSO Extended Format for ensemble differential photometry.
/// Format per exotic-proto's PHOTOMETRY_ROADMAP.md section 4.
/// TRANS is always "NO" — VariLab does not yet compute transformation coefficients
/// (see roadmap section 3, lower priority than differential photometry itself).
/// CNAME="ENSEMBLE" is AAVSO's documented convention for ensemble comparison-star
/// photometry, where no single comp star name/magnitude applies. Since the Extended
/// Format record only carries the ensemble average (no per-comp fields), AAVSO's own
/// guidance is to list which comps were used in the NOTES field — see
/// https://www.aavso.org/comparison-star-and-check-star-labels-when-submitting-observations
/// </summary>
public static class AavsoExportService
{
    public record ExportRow(double Jd, double Mag, double MagErr, double? Airmass);

    /// <summary>
    /// Builds the "Ensemble comps: ..." NOTES-field text listing every selected comp star.
    /// AUID is used when the comp came from AAVSO VSP (the AAVSO-native identifier); comps
    /// sourced from Gaia/APASS/GSPC only (no AUID) are identified by Gaia DR3 source ID —
    /// a globally unique, publicly cross-referenceable catalog ID, in the same spirit AAVSO's
    /// own guidance already endorses using APASS/VizieR-sourced comps when no VSP chart exists.
    /// Semicolons (not commas) separate entries so the NOTES value can't be mistaken for
    /// additional CSV columns given #DELIM=,
    /// </summary>
    public static string BuildCompNotes(IReadOnlyList<GaiaCompService.CompStarInfo> comps)
    {
        if (comps.Count == 0) return "";

        var labels = comps.Select(c =>
            !string.IsNullOrWhiteSpace(c.Auid) ? c.Auid! :
            !string.IsNullOrWhiteSpace(c.GaiaId) ? $"GAIA DR3 {c.GaiaId}" :
            "unlabeled");

        return $"Ensemble comps: {string.Join("; ", labels)}";
    }

    /// <summary>
    /// Detects meridian (pier) flips from the FITS PIERSIDE keyword captured per frame and
    /// builds a NOTES-field remark documenting them, e.g. "Meridian flip detected (pier West
    /// -> East at JD 2461222.82340)". AAVSO's own recommendation for a session that crosses a
    /// meridian flip is to submit the photometry as measured (no manual offset correction) and
    /// document the flip in NOTES for future analysts — this is generated automatically rather
    /// than relying on the observer to remember to add it by hand. Returns null when no
    /// PIERSIDE keyword was present or no flip occurred, so it never manufactures a note where
    /// there's nothing to say.
    /// </summary>
    public static string? BuildFlipNote(IReadOnlyList<PhotometryService.FramePoint> points)
    {
        var ordered = points
            .Where(p => !p.Rejected && !string.IsNullOrEmpty(p.PierSide))
            .OrderBy(p => p.Jd)
            .ToList();
        if (ordered.Count < 2) return null;

        var flips = new List<string>();
        for (int i = 1; i < ordered.Count; i++)
        {
            if (ordered[i].PierSide != ordered[i - 1].PierSide)
            {
                flips.Add($"{ordered[i - 1].PierSide} -> {ordered[i].PierSide} at JD " +
                          ordered[i].Jd.ToString("F5", CultureInfo.InvariantCulture));
            }
        }

        if (flips.Count == 0) return null;
        return flips.Count == 1
            ? $"Meridian flip detected (pier {flips[0]})"
            : $"Meridian flips detected (pier {string.Join("; ", flips)})";
    }

    // Maps VariLab's internal filter codes (used for comp-star magnitude source lookup in
    // GaiaCompService) to a valid AAVSO Extended Format FILT code. AAVSO's uploader validates
    // FILT against a fixed ShortName list (https://vsx.aavso.org/index.php?view=api.bands) —
    // several codes VariLab accepts for comp-star matching aren't on that list and get silently
    // rejected as "unexpected" errors at upload time. Each mapping below targets the same band
    // GaiaCompService already treats the code as equivalent to (e.g. "L" is already looked up
    // against V-band comp magnitudes everywhere else in the pipeline, so it's submitted as CV
    // here too, rather than introducing a second, inconsistent notion of what "L" means).
    private static readonly Dictionary<string, string> AavsoFilterMap =
        new(System.StringComparer.OrdinalIgnoreCase)
    {
        ["L"]   = "CV",   // Luminance — not an AAVSO band; already V-band-calibrated internally
        ["C"]   = "CV",   // bare "Clear" — AAVSO's code for this is CV
        ["CBB"] = "CR",   // Blue-Blocking Clear — already R-band-calibrated internally
        ["Rc"]  = "R",    // AAVSO's ShortName for Cousins R is bare "R", not "Rc"
        ["Ic"]  = "I",    // AAVSO's ShortName for Cousins I is bare "I", not "Ic"
    };

    /// <summary>Translates a VariLab filter code to its AAVSO-valid equivalent for submission,
    /// passing through unchanged any code already on AAVSO's accepted list.</summary>
    public static string ToAavsoFilterCode(string filterCode) =>
        AavsoFilterMap.GetValueOrDefault(filterCode, filterCode);

    public static string Build(
        string starName,
        string observerCode,
        string filterCode,
        string softwareName,
        IReadOnlyList<ExportRow> rows,
        string? notes = null)
    {
        string aavsoFilterCode = ToAavsoFilterCode(filterCode);
        string notesField = string.IsNullOrWhiteSpace(notes) ? "na" : notes;
        var sb = new StringBuilder();
        sb.AppendLine("#TYPE=Extended");
        sb.AppendLine($"#OBSCODE={(string.IsNullOrWhiteSpace(observerCode) ? "na" : observerCode)}");
        sb.AppendLine($"#SOFTWARE={softwareName}");
        sb.AppendLine("#DELIM=,");
        sb.AppendLine("#DATE=JD");
        sb.AppendLine("#OBSTYPE=CCD");
        sb.AppendLine("#NAME,DATE,MAG,MERR,FILT,TRANS,MTYPE,CNAME,CMAG,KNAME,KMAG,AMASS,GROUP,CHART,NOTES");

        foreach (var r in rows)
        {
            string amass = r.Airmass.HasValue ? r.Airmass.Value.ToString("F2", CultureInfo.InvariantCulture) : "na";
            sb.AppendLine(string.Join(",",
                string.IsNullOrWhiteSpace(starName) ? "na" : starName,
                r.Jd.ToString("F5", CultureInfo.InvariantCulture),
                r.Mag.ToString("F3", CultureInfo.InvariantCulture),
                r.MagErr.ToString("F3", CultureInfo.InvariantCulture),
                aavsoFilterCode,
                "NO",           // TRANS — no transformation coefficients applied
                "STD",          // MTYPE
                "ENSEMBLE",     // CNAME
                "na",           // CMAG
                "na",           // KNAME
                "na",           // KMAG
                amass,
                "na",           // GROUP
                "na",           // CHART
                notesField));   // NOTES
        }

        return sb.ToString();
    }
}
