using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VariLab.Services;

/// <summary>
/// Comprehensive comparison star selection using Gaia DR3 + AAVSO VSP + APASS DR9 + Gaia GSPC.
/// Ported from CompStarSelector (exotic-proto) to native C# — no Docker or Python required.
///
/// Pipeline:
///   1.  Query Gaia DR3 TAP for all field stars (cone search)
///   2.  Query AAVSO VSP for vetted comparison stars
///   3.  Cross-match VSP against Gaia for quality metric enrichment
///   4.  Query APASS DR9 (VizieR) for calibrated filter-band magnitudes
///   5.  Filter Gaia stars for eligibility (mag range, RUWE &lt; 1.1/1.2/1.4 tiered, not variable, FOE &gt; 200)
///   5b. VSX variable star cross-check — exclude Gaia candidates matching AAVSO VSX known variables
///   6.  Apply proper motion correction to the observation epoch
///   7.  Project to pixel coordinates via the plate-solved WCS; check isolation
///   8.  Score by color match, magnitude, RUWE, centrality, flux quality
///   9.  Spatial deduplication; split into selected (top N, configurable 1–25) + backfill pool
///   10. Query Gaia GSPC synthetic photometry for filter-matched magnitudes
///   11. PSF measurement + validation loop (up to 3 passes; backfill replaces failures)
///   12. Assign filter-matched magnitudes via preference chain (VSP → GSPC → APASS → G→V)
///   13. Return up to 10 pixel coordinate pairs with per-star detail
/// </summary>
public static class GaiaCompService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    // Gaia DR3 TAP sync endpoints — tried in order on failure
    private static readonly string[] TapEndpoints =
    [
        "https://gea.esac.esa.int/tap-server/tap/sync",
        "https://gaia.ari.uni-heidelberg.de/tap/sync",
    ];

    // VizieR TAP sync endpoints for APASS DR9
    private static readonly string[] VizierEndpoints =
    [
        "https://tapvizier.u-strasbg.fr/TAPVizieR/tap/sync",
        "https://tapvizier.cfa.harvard.edu/TAPVizieR/tap/sync",
    ];

    // AAVSO filter code → VSP band name
    private static readonly Dictionary<string, string> VspFilterMap =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ["V"]   = "V",  ["B"]   = "B",  ["R"]   = "Rc", ["Rc"]  = "Rc",
        ["I"]   = "Ic", ["Ic"]  = "Ic", ["CV"]  = "V",  ["C"]   = "V",
        ["SR"]  = "Rc", ["SG"]  = "V",  ["SI"]  = "Ic", ["SZ"]  = "Ic",
        ["CBB"] = "Rc", ["L"]   = "V",  ["RJ"]  = "Rc", ["IJ"]  = "Ic",
    };

    // APASS DR9 VizieR column pair per filter.
    // NOTE: R, Rc, I, Ic are deliberately absent — APASS publishes Sloan r'/i' only,
    // which has a ~0.1 mag colour mismatch vs Cousins. Falls through to GSPC for those.
    private static readonly Dictionary<string, (string MagCol, string ErrCol)> ApassFilterMap =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ["V"]    = ("Vmag",   "e_Vmag"),
        ["B"]    = ("Bmag",   "e_Bmag"),
        ["SR"]   = ("r'mag",  "e_r'mag"),
        ["SG"]   = ("g'mag",  "e_g'mag"),
        ["SI"]   = ("i'mag",  "e_i'mag"),
        ["C"]    = ("Vmag",   "e_Vmag"),
        ["CV"]   = ("Vmag",   "e_Vmag"),
        ["TG"]   = ("Vmag",   "e_Vmag"),
        ["L"]    = ("Vmag",   "e_Vmag"),
        ["CBB"]  = ("r'mag",  "e_r'mag"),
    };

    // Gaia GSPC (gaiadr3.synthetic_photometry_gspc) magnitude column per filter.
    // Derives flux column names as: mag_col → base (strip "_mag") → base_flux, base_flux_error.
    private static readonly Dictionary<string, string> GspcMagColMap =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ["V"]    = "v_jkc_mag",
        ["B"]    = "b_jkc_mag",
        ["R"]    = "r_jkc_mag",  ["Rc"]  = "r_jkc_mag",
        ["I"]    = "i_jkc_mag",  ["Ic"]  = "i_jkc_mag",
        ["SR"]   = "r_sdss_mag",
        ["SG"]   = "g_sdss_mag",
        ["SI"]   = "i_sdss_mag",
        ["SZ"]   = "z_sdss_mag",
        ["CBB"]  = "r_sdss_mag",
        ["C"]    = "v_jkc_mag",  ["CV"]  = "v_jkc_mag",
        ["TG"]   = "v_jkc_mag",  ["L"]   = "v_jkc_mag",
    };

    // ── Public types ──────────────────────────────────────────────────────────

    /// <summary>Per-star detail included in the CompResult.</summary>
    public record CompStarInfo(
        int     X,
        int     Y,
        double  Ra,
        double  Dec,
        double? Mag,
        double? MagErr,
        string? MagSource,
        string  FilterBand,
        double? FwhmPx,
        double? Snr,
        bool    Saturated,
        string  CatalogSource,   // "VSP" | "Gaia"
        string? GaiaId,
        string? Auid,
        double  SepArcsec,
        double? BpRp,
        double? Ruwe);

    /// <summary>Image quality metrics derived from the comp star selection pass.</summary>
    public record ImageQualityInfo(
        bool     ColorMatchActive,  // true — target BP-RP found in Gaia
        double?  TargetSnr,         // target PSF peak SNR
        double?  PrecisionMmag,     // photon-limited: 1000 / TargetSnr (mmag)
        double?  FwhmMeanPx,        // mean FWHM across accepted comp stars
        double?  FwhmMinPx,
        double?  FwhmMaxPx,
        string   FwhmUniformity,    // "uniform" | "mild" | "significant" | "—"
        bool     TargetSaturated,
        string   OverallGrade,      // "good" | "acceptable" | "marginal" | "poor" | "—"
        string   Summary);          // one-line human-readable

    /// <summary>A candidate that was tested (selected, then PSF-validated) but excluded.</summary>
    public record RejectedCompInfo(
        int     X,
        int     Y,
        double? Mag,
        string? MagSource,
        string  CatalogSource,
        string  Reason);

    public record CompResult(
        List<(int X, int Y)>     Pairs,
        string                   StatusMessage,
        bool                     GaiaUsed              = false,
        List<CompStarInfo>?      Stars                 = null,
        ImageQualityInfo?        Quality               = null,
        List<RejectedCompInfo>?  Rejected              = null,
        string?                  TargetNeighborWarning = null);

    // ── Internal candidate model ──────────────────────────────────────────────

    private sealed class GaiaCandidate
    {
        // Catalog identity
        public string? GaiaId           { get; set; }
        public string? Auid             { get; set; }
        public string  Source           { get; set; } = "Gaia";   // "VSP" | "Gaia"

        // Sky position
        public double  Ra               { get; set; }
        public double  Dec              { get; set; }

        // Gaia photometry
        public double? GMag             { get; set; }
        public double? BpRp             { get; set; }
        public double? Ruwe             { get; set; }
        public double? FluxOverError    { get; set; }
        public bool    IsVariable       { get; set; }

        // Gaia astrometry
        public double? PmRa             { get; set; }   // mas/yr (includes cos-dec factor)
        public double? PmDec            { get; set; }
        public double? RefEpoch         { get; set; }   // typically 2016.0

        // Gaia binarity indicators (soft scoring penalties, not hard cuts)
        public double? IpdFracMultiPeak { get; set; }
        public double? IpdGofHarmonic   { get; set; }
        public bool    DuplicatedSource { get; set; }

        // VSP catalog magnitude
        public double? VspMag           { get; set; }

        // APASS cross-match result (set by QueryApassAsync + AssignMagnitudes)
        public double? ApassMag         { get; set; }
        public double? ApassMagErr      { get; set; }

        // GSPC synthetic photometry (set by QueryGspcAsync + AssignMagnitudes)
        public double? GspcMag          { get; set; }
        public double? GspcMagErr       { get; set; }

        // Final assigned magnitude (set by AssignMagnitudes)
        public double? FinalMag         { get; set; }
        public double? FinalMagErr      { get; set; }
        public string? FinalMagSource   { get; set; }  // "GSPC" | "APASS" | "G→V" | "VSP"

        // PSF measurement (set by PsfService.Measure)
        public double? FwhmPx           { get; set; }
        public double? FwhmX            { get; set; }
        public double? FwhmY            { get; set; }
        public double? PeakAdu          { get; set; }
        public double? Snr              { get; set; }
        public bool    Saturated        { get; set; }
        public string? RejectReason     { get; set; }  // set if excluded during PSF validation

        // Pipeline-set geometry
        public double  SeparationArcsec { get; set; }
        public int?    XPixel           { get; set; }
        public int?    YPixel           { get; set; }
        public double  Score            { get; set; }
    }

    // ── Internal APASS star ───────────────────────────────────────────────────

    private record ApassStar(double Ra, double Dec, double Mag, double MagErr);

    // ── Public entry point ────────────────────────────────────────────────────

    /// <summary>
    /// Run the full comp star selection pipeline.
    /// Returns up to 10 pixel-coordinate pairs and a rich status message.
    /// Set <paramref name="useVsp"/> = false to skip the AAVSO VSP query.
    /// </summary>
    public static async Task<CompResult> FetchAsync(
        string              fitsPath,
        double              targetRa,
        double              targetDec,
        string              filterCode,
        string?             aavsoCode,
        bool                useVsp       = true,
        IProgress<string>?  progress     = null,
        IProgress<string>?  logProgress  = null,
        int                 maxCompStars = 10,
        Func<string, string, Task<bool>>? confirmOnSourceFailure = null,
        Func<double, double, double, double, double, double, Task>? notifyTargetNotInFrame = null,
        Func<double, double, int, int, int, int, Task>? notifyTargetOffFrame = null,
        CancellationToken   ct           = default)
    {
        // Asks the user (via confirmOnSourceFailure, if wired) whether to keep going after a
        // data source fails outright — rather than silently falling back to whatever else did
        // respond. Returns true to keep going; logs and returns false if the user cancels.
        // With no callback wired (e.g. a future non-interactive caller), defaults to the old
        // silent-continue behavior so this stays backward compatible.
        async Task<bool> ConfirmContinueAsync(string source, string message)
        {
            if (confirmOnSourceFailure is null) return true;
            bool cont = await confirmOnSourceFailure(source, message);
            if (!cont)
                Log($"[Stone] ✗  Cancelled by user after {source} failure", logProgress);
            return cont;
        }

        var _sw = System.Diagnostics.Stopwatch.StartNew();

        // ── Pipeline header ───────────────────────────────────────────────────
        Log("[Stone] ══════════════════════════════════════════════════════════", logProgress);
        Log($"[Stone] Comp Star Selection  |  Stone method (Gaia DR3 + APASS + GSPC{(useVsp ? " + VSP" : "")})  |  Max comps: {maxCompStars}", logProgress);
        Log($"[Stone] Target: RA={targetRa:F6}  Dec={targetDec:F6}  |  Filter: {filterCode}", logProgress);
        Log($"[Stone] FITS: {System.IO.Path.GetFileName(fitsPath)}", logProgress);
        Log("[Stone] ══════════════════════════════════════════════════════════", logProgress);

        // ── Read FITS header ──────────────────────────────────────────────────
        FitsHeaderService.FitsHeader? hdr = null;
        WcsService.WcsInfo?           wcs = null;
        int    naxis1   = 0, naxis2 = 0;
        double obsEpoch = 2025.0;

        try
        {
            hdr    = await Task.Run(() => FitsHeaderService.Read(fitsPath), ct);
            wcs    = WcsService.ReadWcs(hdr);
            naxis1 = hdr.GetInt("NAXIS1") ?? 0;
            naxis2 = hdr.GetInt("NAXIS2") ?? 0;
            var dateObs = hdr.Get("DATE-OBS");
            if (!string.IsNullOrEmpty(dateObs))
                obsEpoch = DateObsToEpoch(dateObs);
        }
        catch (Exception ex)
        {
            SessionLogService.Write($"[GaiaComp] WARNING — could not read FITS header: {ex.Message}");
        }

        if (wcs is null)
        {
            Log("[Stone] ✗  Aborted — no WCS/plate solution in FITS header", logProgress);
            return new CompResult([], "⚠  No plate solution — plate-solve first");
        }

        // ── Target pixel position (for PSF measurement later) ─────────────────
        int? targetXPx = null, targetYPx = null;
        var tPx = WcsService.SkyToPixel(wcs, targetRa, targetDec);
        if (tPx.HasValue) { targetXPx = tPx.Value.X; targetYPx = tPx.Value.Y; }

        // ── FOV from WCS pixel scale ──────────────────────────────────────────
        double scaleRaDeg  = Math.Sqrt(wcs.Cd11 * wcs.Cd11 + wcs.Cd21 * wcs.Cd21);
        double scaleDecDeg = Math.Sqrt(wcs.Cd12 * wcs.Cd12 + wcs.Cd22 * wcs.Cd22);
        double scaleDeg    = (scaleRaDeg + scaleDecDeg) / 2.0;
        double fovArcmin   = 60.0;
        if (scaleDeg > 0 && naxis1 > 0 && naxis2 > 0)
            fovArcmin = Math.Clamp(Math.Max(naxis1, naxis2) * scaleDeg * 60.0, 10.0, 120.0);

        double fovRadiusDeg = fovArcmin / 60.0;
        Log($"[Stone] Image: {naxis1}×{naxis2} px  |  FOV: {fovArcmin:F1}'  |  Scale: {scaleDeg * 3600:F2}\"/px  |  Epoch: {obsEpoch:F1}", logProgress);

        // ── Target-vs-frame sanity check ───────────────────────────────────────
        // Catches a real failure mode found in practice: the FITS OBJECT header (used to
        // resolve targetRa/targetDec on the Data tab) doesn't match what this frame is
        // actually pointing at — e.g. a stale OBJECT keyword left over from a previous
        // target in the same imaging session. Left unchecked, the pipeline runs the full
        // ~15-90s Gaia/APASS/VSP/GSPC sequence and only fails much later, opaquely, at the
        // WCS-projection step ("0 in-frame") with no indication of why. A real
        // pointing/tracking error is at most a few arcmin to a degree; several frame-widths
        // off (or more) means this is almost certainly a different star entirely.
        double frameSepDeg = SepArcsec(targetRa, targetDec, wcs.Crval1, wcs.Crval2) / 3600.0;
        double toleranceDeg = Math.Max(1.0, 3.0 * fovRadiusDeg);
        if (frameSepDeg > toleranceDeg)
        {
            Log($"[Stone] ✗  Target is {frameSepDeg:F1}° from this frame's plate-solved center " +
                $"(FOV {fovArcmin:F1}') — likely FITS OBJECT header mismatch", logProgress);
            if (notifyTargetNotInFrame is not null)
                await notifyTargetNotInFrame(targetRa, targetDec, wcs.Crval1, wcs.Crval2, frameSepDeg, fovArcmin);
            return new CompResult([], "✗  Target not in frame — check Target Name/RA/Dec on the Data tab");
        }

        // ── Target pixel-bounds check ───────────────────────────────────────────
        // The check above only catches a target whose ANGULAR separation from the frame center
        // is large (a wildly wrong pointing, e.g. a stale header) — it never verifies the
        // target's own projected pixel position actually falls on real image data, the way
        // every comp candidate independently is a few steps below (the inFrame filter's own
        // margin test). Confirmed on a real case (V1455 Cen): a target only ~3.5' outside this
        // frame's real edge passed the angular check easily (comfortably inside its generous
        // few-frame-widths tolerance), and comp selection still succeeded — the Gaia cone search
        // is centered on the TARGET's own position with a wide radius, so it still pulled in
        // real candidates from well inside the frame (9-11' away). But the target itself had no
        // real pixel data at its position (Target PSF measured FWHM=0/SNR=0/peak=0), and every
        // subsequent Photometry frame was rejected — a run that looked like it worked (10 good
        // comps selected) but silently couldn't have produced a light curve. Checking the
        // target's own position against the same real-bounds test here fails fast with a clear
        // reason instead.
        if (naxis1 > 0 && naxis2 > 0 && targetXPx.HasValue && targetYPx.HasValue)
        {
            int tMarginX = Math.Max(20, (int)(naxis1 * 0.05));
            int tMarginY = Math.Max(20, (int)(naxis2 * 0.05));
            if (targetXPx.Value < tMarginX || targetXPx.Value > naxis1 - tMarginX ||
                targetYPx.Value < tMarginY || targetYPx.Value > naxis2 - tMarginY)
            {
                Log($"[Stone] ✗  Target projects to pixel ({targetXPx},{targetYPx}) — outside this " +
                    $"{naxis1}×{naxis2} frame's real bounds (5% edge margin). Close enough to the " +
                    "pointing to pass the coarser center-separation check, but no real pixel data " +
                    "at that position.", logProgress);
                if (notifyTargetOffFrame is not null)
                    await notifyTargetOffFrame(targetRa, targetDec, targetXPx.Value, targetYPx.Value, naxis1, naxis2);
                return new CompResult([],
                    "✗  Target falls outside this frame's real pixel bounds — this dataset may not cover this target's field");
            }
        }

        // ── Fire Gaia / VSP / VSX / APASS concurrently ─────────────────────────
        // All four are independent network round-trips that only need the target's own
        // RA/Dec/FOV/filter, not each other's results — VSP's magnitude limit used to narrow
        // slightly once Gaia's own G-band match for the target was known (see the old
        // `(targetGMag ?? 14.0) + 2.0` calculation this replaces), but that was only ever a
        // minor response-size tuning, not a correctness dependency, so it's traded here for
        // starting all four at once instead of waiting on Gaia first. Verified via a scratch
        // console test against the real Gaia/VSP/VSX/APASS endpoints (2026-09-06): identical
        // results either way, ~3.9x faster wall-clock for this stage. Results are still
        // consumed and logged one at a time below, in the same Step 1/2/3 order as before, so
        // the pipeline log and per-source "continue anyway?" prompts read exactly as they did
        // sequentially.
        const double MagFloor = 17.0;
        double vspMagLimitEarly = Math.Min(MagFloor, 16.0);

        var gaiaTask  = QueryGaiaAsync(targetRa, targetDec, fovRadiusDeg * 1.1, MagFloor, progress, ct);
        var vspTask   = useVsp
            ? QueryVspAsync(targetRa, targetDec, fovArcmin, filterCode, vspMagLimitEarly, ct)
            : Task.FromResult<(List<GaiaCandidate> Stars, string Message, bool Failed)>(([], "", false));
        var vsxTask   = QueryVsxAsync(targetRa, targetDec, fovRadiusDeg * 1.1, MagFloor, ct);
        var apassTask = QueryApassAsync(targetRa, targetDec, fovArcmin * 1.1, filterCode, ct);

        // ── Gaia DR3 field query ──────────────────────────────────────────────
        List<GaiaCandidate>? gaiaTable = null;
        bool   gaiaOk    = false;
        string gaiaError = "";

        Log($"[Stone]", logProgress);
        Log($"[Stone] ── Step 1/8: Gaia DR3 field query ─────────────────────────────", logProgress);
        Log($"[Stone]   Cone: r={fovRadiusDeg * 1.1:F3}°  maglim={MagFloor:F1}", logProgress);

        try
        {
            gaiaTable = await gaiaTask;
            gaiaOk = gaiaTable?.Count > 0;
            Log(gaiaOk
                ? $"[Stone]   Result: {gaiaTable!.Count} field stars"
                : "[Stone]   Result: 0 field stars (Gaia returned nothing)",
                logProgress);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            gaiaError = ex.Message;
            Log($"[Stone]   ✗  Gaia TAP failed: {ex.Message}", logProgress);
        }

        if (!gaiaOk && !string.IsNullOrEmpty(gaiaError))
        {
            bool cont = await ConfirmContinueAsync("Gaia DR3",
                $"Gaia DR3 is the primary source of comp-star candidates — every other source only enriches or replaces magnitudes on top of it. It failed on all mirrors:\n\n{gaiaError}\n\nWithout it, this run will likely produce a much smaller or empty ensemble.");
            if (!cont)
                return new CompResult([], "✗  Cancelled by user — Gaia DR3 query failed");
        }

        // ── Progress: Gaia stage done ─────────────────────────────────────────
        if (gaiaOk)
            progress?.Report(useVsp
                ? $"⟳  Gaia: {gaiaTable!.Count} stars — querying VSP + APASS…"
                : $"⟳  Gaia: {gaiaTable!.Count} stars — querying APASS…");
        else
            progress?.Report(useVsp
                ? "⟳  Gaia unavailable — querying VSP + APASS…"
                : "⟳  Gaia unavailable — querying APASS…");

        // ── Target star in Gaia table ─────────────────────────────────────────
        double? targetGMag = null, targetBpRp = null;
        if (gaiaOk)
        {
            var nearest = gaiaTable!
                .Where(s => s.GMag.HasValue)
                .OrderBy(s => SepArcsec(targetRa, targetDec, s.Ra, s.Dec))
                .FirstOrDefault();

            if (nearest is not null && SepArcsec(targetRa, targetDec, nearest.Ra, nearest.Dec) < 30.0)
            {
                targetGMag = nearest.GMag;
                targetBpRp = nearest.BpRp;
                Log($"[Stone]   Target Gaia match: G={targetGMag:F2}  BP-RP={targetBpRp?.ToString("F2") ?? "—"}  " +
                    $"({(targetBpRp.HasValue ? "color matching ACTIVE" : "no BP-RP — color match disabled")})",
                    logProgress);
            }
            else
            {
                Log($"[Stone]   Target not found in Gaia catalog within 30\" — color matching DISABLED", logProgress);
            }
        }

        // ── Target neighbor pre-flight check ──────────────────────────────────
        // Comp candidates are isolation-checked below (a bad one is simply dropped and another
        // picked instead) — the target itself never goes through the same test, since it can't
        // be swapped out. Applies the identical isolation standard already used for comps, just
        // against the target's own position, so a run heading into unreliable territory is
        // flagged before Photometry spends minutes on it rather than discovered afterward via a
        // discrepant amplitude — confirmed the hard way on V1786 Cen (4.4", 2.2 mag brighter)
        // and V1615 Cen, both of which would have tripped this check immediately. Non-blocking:
        // unlike a rejected comp candidate, there's no fallback target to try instead.
        string? targetNeighborWarning = null;
        if (gaiaOk && gaiaTable is not null)
        {
            int    tgtNstars       = hdr?.GetInt("NSTARS") ?? 0;
            double tgtIsoThreshold = tgtNstars > 500 ? 10.0 : 15.0;

            GaiaCandidate? worstNeighbor = null;
            double         worstExcess   = 0;
            foreach (var nb in gaiaTable)
            {
                if (!nb.GMag.HasValue) continue;
                double nbSep = SepArcsec(targetRa, targetDec, nb.Ra, nb.Dec);
                if (nbSep < 0.5) continue;   // the target's own Gaia match
                double diff = 12.0 - nb.GMag.Value;
                double excl = diff > 0 ? tgtIsoThreshold + diff * 30.0 : tgtIsoThreshold;
                if (nbSep >= excl) continue;
                double excess = excl - nbSep;
                if (worstNeighbor is null || excess > worstExcess) { worstNeighbor = nb; worstExcess = excess; }
            }

            if (worstNeighbor is not null)
            {
                double sep  = SepArcsec(targetRa, targetDec, worstNeighbor.Ra, worstNeighbor.Dec);
                double dMag = targetGMag.HasValue ? worstNeighbor.GMag!.Value - targetGMag.Value : double.NaN;
                targetNeighborWarning =
                    $"⚠ Target has a Gaia neighbor {sep:F1}\" away" +
                    (double.IsNaN(dMag) ? "" : dMag < 0 ? $" ({-dMag:F1} mag brighter)" : $" ({dMag:F1} mag fainter)") +
                    " — closer than this pipeline's own comp-star isolation standard. Reliable " +
                    "deblending is unlikely at this seeing regardless of photometry method; " +
                    "consider PSF Fit mode, or treat amplitude/mean-mag results here with caution.";
                Log($"[Stone]   {targetNeighborWarning}", logProgress);
            }
        }

        // ── Build Gaia comp candidates ────────────────────────────────────────
        // Faint-end window: +3.0 mag (wide pool; scorer penalises dimmer stars)
        var    gaiaCandidates = new List<GaiaCandidate>();
        double ruweUsed       = 1.4;   // whichever tier was actually applied (logged below)
        if (gaiaOk)
        {
            double magMin = targetGMag.HasValue ? targetGMag.Value - 2.0 :  8.0;
            double magMax = targetGMag.HasValue ? targetGMag.Value + 3.0 : 16.0;

            int nVariable = gaiaTable!.Count(s => s.IsVariable);

            // Tiered RUWE fallback: try <1.1 first, relax to <1.2, then <1.4 if the
            // pool is too thin. This keeps the tightest possible gate on well-populated
            // fields without starving sparse ones.
            // FOE floor raised to 200: stars with flux-over-error 100–200 have non-trivial
            // Gaia photometric uncertainty and are marginal for differential photometry.
            const int    TieredRuweMinimum = 8;
            const double FoeFloor          = 200.0;
            double[]     ruweThresholds    = [1.1, 1.2, 1.4];
            bool         tieredRelaxed     = false;

            foreach (var thresh in ruweThresholds)
            {
                var candidates = gaiaTable!
                    .Where(s =>
                        s.GMag.HasValue            &&
                        s.GMag.Value >= magMin     &&
                        s.GMag.Value <= magMax     &&
                        !s.IsVariable              &&
                        (s.Ruwe          ?? 1.0) < thresh    &&
                        (s.FluxOverError ?? 0.0) > FoeFloor)
                    .ToList();

                if (candidates.Count >= TieredRuweMinimum || thresh == 1.4)
                {
                    gaiaCandidates = candidates;
                    ruweUsed       = thresh;
                    if (thresh < 1.4)
                        Log($"[Stone]     Tiered RUWE: using <{thresh:F1} ({candidates.Count} candidates)", logProgress);
                    else if (tieredRelaxed)
                        Log($"[Stone]     Tiered RUWE: fell back to <{thresh:F1} ({candidates.Count} candidates)", logProgress);
                    break;
                }

                Log($"[Stone]     Tiered RUWE: <{thresh:F1} → {candidates.Count} candidates " +
                    $"(< {TieredRuweMinimum} minimum — relaxing to next tier)", logProgress);
                tieredRelaxed = true;
            }

            // Recount exclusions against the chosen threshold for the log summary
            int nRuwe = gaiaTable!.Count(s =>
                !s.IsVariable              &&
                s.GMag.HasValue            &&
                s.GMag.Value >= magMin     &&
                s.GMag.Value <= magMax     &&
                (s.FluxOverError ?? 0.0) > FoeFloor &&
                (s.Ruwe          ?? 1.0) >= ruweUsed);

            int nFlux = gaiaTable!.Count(s =>
                !s.IsVariable              &&
                s.GMag.HasValue            &&
                s.GMag.Value >= magMin     &&
                s.GMag.Value <= magMax     &&
                (s.Ruwe          ?? 1.0) < ruweUsed &&
                s.FluxOverError.HasValue   &&
                s.FluxOverError.Value <= FoeFloor);

            var magValues = gaiaCandidates.Select(s => s.GMag!.Value).ToList();
            Log($"[Stone]   Quality filter (RUWE<{ruweUsed:F1}, FOE>{FoeFloor:F0}, mag [{magMin:F1}..{magMax:F1}]):", logProgress);
            Log($"[Stone]     {gaiaTable!.Count} → {gaiaCandidates.Count} candidates" +
                $"  (excluded: {nVariable} variable, {nRuwe} RUWE≥{ruweUsed:F1}, {nFlux} low-flux)", logProgress);
            if (magValues.Count > 0)
                Log($"[Stone]     Mag range: {magValues.Min():F2} → {magValues.Max():F2}", logProgress);
        }

        // ── VSP query (optional) ──────────────────────────────────────────────
        // vspMagLimitEarly (fired above, before Gaia's own targetGMag match was known) is used
        // here only for the log line — the actual query was already issued with it.
        List<GaiaCandidate> vspCandidates = [];
        string vspMsg = "";
        bool vspFailed = false;
        if (useVsp)
        {
            Log($"[Stone]", logProgress);
            Log($"[Stone] ── Step 2/8: AAVSO VSP query ────────────────────────────────────", logProgress);
            Log($"[Stone]   FOV: {fovArcmin:F1}'  |  maglim: {vspMagLimitEarly:F1}  |  Filter: {filterCode}", logProgress);
            (vspCandidates, vspMsg, vspFailed) = await vspTask;
            Log($"[Stone]   Result: {vspMsg}", logProgress);

            if (vspFailed)
            {
                bool cont = await ConfirmContinueAsync("AAVSO VSP",
                    $"AAVSO's vetted comparison-star sequence (VSP) failed to respond:\n\n{vspMsg}\n\nWithout it, comp magnitudes for this run will fall back to APASS or the less-precise Gaia G→V conversion, and VSP's independently-vetted candidates won't be available.");
                if (!cont)
                    return new CompResult([], "✗  Cancelled by user — AAVSO VSP query failed");
            }
        }

        // ── Cross-match VSP → Gaia for quality metric enrichment ─────────────
        var vspGaiaIds = new HashSet<string?>();
        if (useVsp && gaiaOk)
        {
            int enriched = 0;
            foreach (var v in vspCandidates)
            {
                var near = gaiaTable!
                    .OrderBy(g => SepArcsec(v.Ra, v.Dec, g.Ra, g.Dec))
                    .FirstOrDefault();

                if (near is null || SepArcsec(v.Ra, v.Dec, near.Ra, near.Dec) > 5.0) continue;

                v.GaiaId           = near.GaiaId;
                v.GMag             = near.GMag;
                v.BpRp             = near.BpRp;
                v.Ruwe             = near.Ruwe;
                v.FluxOverError    = near.FluxOverError;
                v.PmRa             = near.PmRa;
                v.PmDec            = near.PmDec;
                v.RefEpoch         = near.RefEpoch;
                v.IpdFracMultiPeak = near.IpdFracMultiPeak;
                v.IpdGofHarmonic   = near.IpdGofHarmonic;
                v.DuplicatedSource = near.DuplicatedSource;
                v.IsVariable       = v.IsVariable || near.IsVariable;
                vspGaiaIds.Add(near.GaiaId);
                enriched++;
            }

            int dedupBefore = gaiaCandidates.Count;
            // Drop Gaia candidates already represented by a VSP star
            gaiaCandidates = gaiaCandidates
                .Where(g => g.GaiaId is null || !vspGaiaIds.Contains(g.GaiaId))
                .ToList();

            if (useVsp)
                Log($"[Stone]   Gaia enrichment: {enriched}/{vspCandidates.Count} VSP stars matched  |  " +
                    $"Gaia pool after dedup: {dedupBefore} → {gaiaCandidates.Count}", logProgress);
        }

        // ── VSX variable star cross-check ────────────────────────────────────
        // Query the AAVSO Variable Star Index (VSX) and hard-exclude any Gaia
        // candidate within 5" of a known variable. VSX is complementary to
        // Gaia's phot_variable_flag — a star can be in VSX without being flagged
        // in Gaia DR3. Fails open: if the query fails, the check is skipped and
        // the pipeline continues with the full Gaia pool.
        Log($"[Stone]", logProgress);
        Log($"[Stone] ── VSX variable star cross-check ───────────────────────────────", logProgress);

        var vsxVariables = new List<(double Ra, double Dec, string Name)>();
        try
        {
            vsxVariables = await vsxTask;
            Log($"[Stone]   VSX: {vsxVariables.Count} known variable{(vsxVariables.Count == 1 ? "" : "s")} in field", logProgress);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log($"[Stone]   ✗  VSX query failed: {ex.Message} — cross-check skipped", logProgress);
        }

        if (vsxVariables.Count > 0)
        {
            const double VsxMatchArcsec = 5.0;
            var vsxExcluded = new List<(GaiaCandidate Cand, string VsxName)>();
            var vsxSafe     = new List<GaiaCandidate>();

            foreach (var c in gaiaCandidates)
            {
                string? matchedName = null;
                foreach (var (vRa, vDec, vName) in vsxVariables)
                {
                    if (SepArcsec(c.Ra, c.Dec, vRa, vDec) <= VsxMatchArcsec)
                    {
                        matchedName = vName;
                        break;
                    }
                }
                if (matchedName is null)
                    vsxSafe.Add(c);
                else
                    vsxExcluded.Add((c, matchedName));
            }

            if (vsxExcluded.Count > 0)
            {
                Log($"[Stone]   VSX exclusion ({VsxMatchArcsec:F0}\" match radius): " +
                    $"{gaiaCandidates.Count} → {vsxSafe.Count} candidates " +
                    $"({vsxExcluded.Count} excluded)", logProgress);
                foreach (var (cand, vName) in vsxExcluded)
                    Log($"[Stone]     ✗  {CandLabel(cand)}  [{vName}]", logProgress);
                gaiaCandidates = vsxSafe;
            }
            else
            {
                Log($"[Stone]   VSX cross-check: no candidates matched a known variable", logProgress);
            }
        }

        // ── APASS DR9 query ───────────────────────────────────────────────────
        List<ApassStar> apassStars = [];
        string apassMsg = "";
        Log($"[Stone]", logProgress);
        Log($"[Stone] ── Step 3/8: APASS DR9 query ───────────────────────────────────", logProgress);
        Log($"[Stone]   Filter: {filterCode}  |  Cone: r={fovArcmin * 1.1 / 120.0:F3}°", logProgress);
        bool apassFailed = false;
        try
        {
            (apassStars, apassMsg, apassFailed) = await apassTask;
            Log($"[Stone]   Result: {apassMsg}", logProgress);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            apassFailed = true;
            apassMsg = ex.Message;
            Log($"[Stone]   ✗  APASS query failed: {ex.Message}", logProgress);
        }

        if (apassFailed)
        {
            bool cont = await ConfirmContinueAsync("APASS DR9",
                $"APASS DR9 (VizieR) failed on all mirrors:\n\n{apassMsg}\n\nWithout it, comp magnitudes for stars with no VSP or GSPC value will fall straight through to the least-precise Gaia G→V conversion.");
            if (!cont)
                return new CompResult([], "✗  Cancelled by user — APASS DR9 query failed");
        }

        // ── Merge all candidates ──────────────────────────────────────────────
        var allCandidates = vspCandidates.Concat(gaiaCandidates).ToList();
        Log($"[Stone]", logProgress);
        Log($"[Stone] ── Step 4/8: Candidate pool ───────────────────────────────────", logProgress);
        Log($"[Stone]   Total: {allCandidates.Count} ({vspCandidates.Count} VSP + {gaiaCandidates.Count} Gaia)", logProgress);
        if (allCandidates.Count == 0)
        {
            string why = (!gaiaOk && !string.IsNullOrEmpty(gaiaError))
                ? $"Gaia: {gaiaError}" : "";
            if (!string.IsNullOrEmpty(vspMsg))
                why += (why.Length > 0 ? "; " : "") + $"VSP: {vspMsg}";
            Log($"[Stone] ✗  No candidates — {(string.IsNullOrEmpty(why) ? "unknown reason" : why)}", logProgress);
            return new CompResult([], $"✗  No candidates found — {why}", GaiaUsed: gaiaOk);
        }

        // ── PM correction + WCS projection + frame filter ─────────────────────
        Log($"[Stone]", logProgress);
        Log($"[Stone] ── Step 5/8: Frame projection & isolation ───────────────────────", logProgress);
        Log($"[Stone]   PM correction: ref_epoch 2016.0 → obs_epoch {obsEpoch:F1}", logProgress);
        int marginX = Math.Max(20, (int)(naxis1 * 0.05));
        int marginY = Math.Max(20, (int)(naxis2 * 0.05));

        var inFrame = new List<GaiaCandidate>();
        foreach (var c in allCandidates)
        {
            if (c.IsVariable) continue;

            var (ra, dec) = ApplyPm(c.Ra, c.Dec, c.PmRa, c.PmDec, c.RefEpoch, obsEpoch);
            var px = WcsService.SkyToPixel(wcs, ra, dec);
            if (px is null) continue;

            var (x, y) = px.Value;
            if (naxis1 > 0 && naxis2 > 0 &&
                (x < marginX || x > naxis1 - marginX ||
                 y < marginY || y > naxis2 - marginY)) continue;

            // Persist the PM-corrected (observation-epoch) sky position — this is
            // what downstream multi-frame photometry should re-project via each
            // frame's own WCS, not the raw catalog-epoch Ra/Dec.
            c.Ra               = ra;
            c.Dec              = dec;
            c.XPixel           = x;
            c.YPixel           = y;
            c.SeparationArcsec = SepArcsec(targetRa, targetDec, ra, dec);
            if (c.SeparationArcsec < 30.0) continue;  // too close to target

            inFrame.Add(c);
        }

        Log($"[Stone]   WCS projection (5% edge margin): {allCandidates.Count} → {inFrame.Count} in-frame", logProgress);
        if (inFrame.Count == 0)
        {
            Log($"[Stone] ✗  No candidates project within the image frame ({naxis1}×{naxis2})", logProgress);
            return new CompResult([], "⚠  No candidates fall within the image frame", GaiaUsed: gaiaOk);
        }

        // ── Isolation check ───────────────────────────────────────────────────
        int    nstars        = hdr?.GetInt("NSTARS") ?? 0;
        double isoThreshold  = nstars > 500 ? 10.0 : 15.0;

        var isolated = new List<GaiaCandidate>();
        foreach (var c in inFrame)
        {
            if (!gaiaOk || gaiaTable is null) { isolated.Add(c); continue; }

            bool ok = true;
            foreach (var nb in gaiaTable)
            {
                if (nb.GaiaId == c.GaiaId && c.GaiaId is not null) continue;
                if (!nb.GMag.HasValue) continue;
                double nbSep  = SepArcsec(c.Ra, c.Dec, nb.Ra, nb.Dec);
                if (nbSep < 0.5) continue;
                double diff   = 12.0 - nb.GMag.Value;
                double excl   = diff > 0 ? isoThreshold + diff * 30.0 : isoThreshold;
                if (nbSep < excl) { ok = false; break; }
            }
            if (ok) isolated.Add(c);
        }

        Log($"[Stone]   Isolation check (threshold={isoThreshold:F0}\"): {inFrame.Count} → {isolated.Count} isolated", logProgress);
        if (isolated.Count < inFrame.Count)
            Log($"[Stone]     {inFrame.Count - isolated.Count} rejected: bright neighbor within exclusion radius", logProgress);
        if (isolated.Count == 0)
        {
            isolated = inFrame;
            Log("[Stone]   ⚠  All candidates failed isolation — relaxing filter", logProgress);
        }

        // ── Score ─────────────────────────────────────────────────────────────
        Log($"[Stone]", logProgress);
        Log($"[Stone] ── Step 6/8: Scoring ─────────────────────────────────────────", logProgress);
        Log($"[Stone]   {isolated.Count} candidates to score", logProgress);
        Log(targetBpRp.HasValue
            ? $"[Stone]   Color matching: ACTIVE  (target BP-RP={targetBpRp:F2})"
            : $"[Stone]   Color matching: DISABLED  (target not in Gaia / no BP-RP)",
            logProgress);
        Log(targetBpRp.HasValue
            ? "[Stone]   Weights: color×0.30 + mag×0.25 + flux×0.15 + ruwe×0.10 + centrality×0.20 + VSP+0.10"
            : "[Stone]   Weights: mag×0.30 + flux×0.30 + ruwe×0.15 + centrality×0.25 + VSP+0.10",
            logProgress);
        double fovRadiusArcsec = fovArcmin * 60.0 / 2.0;

        foreach (var c in isolated)
        {
            double colorScore = targetBpRp.HasValue && c.BpRp.HasValue
                ? 1.0 - Math.Min(1.0, Math.Abs(c.BpRp.Value - targetBpRp.Value) / 0.6)
                : 0.0;

            double fluxScore = Math.Min(1.0, (c.FluxOverError ?? 0.0) / 500.0);

            double ruwe      = c.Ruwe ?? 1.0;
            double ruweScore = Math.Max(0.0, 1.0 - (ruwe - 1.0) / 0.4);

            double centrality = fovArcmin > 0
                ? Math.Max(0.0, 1.0 - Math.Max(0.0,
                    c.SeparationArcsec - fovRadiusArcsec * 0.5) / (fovRadiusArcsec * 0.5))
                : 1.0;

            double magScore = 0.5;
            if (targetGMag.HasValue && c.GMag.HasValue)
            {
                double md = c.GMag.Value - targetGMag.Value;
                magScore  = md <= 0 ? 1.0 : Math.Max(0.0, 1.0 - md / 1.5);
            }

            double score = targetBpRp.HasValue
                ? colorScore * 0.30 + magScore * 0.25 + fluxScore * 0.15
                  + ruweScore * 0.10 + centrality * 0.20
                : magScore * 0.30 + fluxScore * 0.30
                  + ruweScore * 0.15 + centrality * 0.25;

            if (c.Source == "VSP")                            score += 0.10;
            if ((c.IpdFracMultiPeak ?? 0) > 2)                score *= 0.95;
            if ((c.IpdGofHarmonic   ?? 0) >= 0.1)             score *= 0.97;
            if (c.DuplicatedSource)                           score *= 0.97;

            c.Score = score;
        }

        isolated.Sort((a, b) => b.Score.CompareTo(a.Score));

        // Log top-10 scored candidates
        Log($"[Stone]   Top candidates (pre-PSF, showing top {maxCompStars}):", logProgress);
        Log($"[Stone]   {"Rank",-5} {"Score",-7} {"Gmag",-6} {"ΔGmag",-7} {"BP-RP",-6} {"RUWE",-5} {"Sep\"",-7} Source", logProgress);
        Log($"[Stone]   {"────",-5} {"─────",-7} {"────",-6} {"─────",-7} {"─────",-6} {"────",-5} {"────",-7} ──────", logProgress);
        int _rank = 1;
        foreach (var c in isolated.Take(maxCompStars))
        {
            double dMag = (targetGMag.HasValue && c.GMag.HasValue)
                ? c.GMag.Value - targetGMag.Value : double.NaN;
            string dMagStr = double.IsNaN(dMag) ? "   —  " : $"{dMag:+0.00;-0.00}";
            Log($"[Stone]   {_rank,-5} {c.Score:F3,-7} {c.GMag?.ToString("F2") ?? "—",-6} {dMagStr,-7} " +
                $"{c.BpRp?.ToString("F2") ?? "—",-6} {c.Ruwe?.ToString("F2") ?? "—",-5} " +
                $"{c.SeparationArcsec:F0,-7} {CandLabel(c)}",
                logProgress);
            _rank++;
        }

        // ── Spatial deduplication → selected (top 10) + backfill pool ─────────
        //
        // Stone + VSP: VSP stars have guaranteed priority — all valid VSP stars
        // fill the first slots, then Stone (Gaia+APASS) fills what remains.
        //
        // Stone: pure score-order selection (no VSP candidates present anyway).
        //
        var selected = new List<GaiaCandidate>();
        var backfill = new List<GaiaCandidate>();

        if (useVsp)
        {
            // Pass 1 — VSP stars (already score-sorted within this subset)
            foreach (var c in isolated.Where(c => c.Source == "VSP"))
            {
                bool tooClose = selected.Any(s =>
                    s.XPixel.HasValue && s.YPixel.HasValue &&
                    c.XPixel.HasValue && c.YPixel.HasValue &&
                    Math.Abs(s.XPixel.Value - c.XPixel.Value) <= 20 &&
                    Math.Abs(s.YPixel.Value - c.YPixel.Value) <= 20);

                if (!tooClose && selected.Count < maxCompStars)
                    selected.Add(c);
                else
                    backfill.Add(c);
            }

            // Pass 2 — Stone candidates fill remaining slots
            foreach (var c in isolated.Where(c => c.Source != "VSP"))
            {
                bool tooClose = selected.Any(s =>
                    s.XPixel.HasValue && s.YPixel.HasValue &&
                    c.XPixel.HasValue && c.YPixel.HasValue &&
                    Math.Abs(s.XPixel.Value - c.XPixel.Value) <= 20 &&
                    Math.Abs(s.YPixel.Value - c.YPixel.Value) <= 20);

                if (!tooClose && selected.Count < maxCompStars)
                    selected.Add(c);
                else
                    backfill.Add(c);
            }
        }
        else
        {
            // Stone only — pure score-order
            foreach (var c in isolated)
            {
                bool tooClose = selected.Any(s =>
                    s.XPixel.HasValue && s.YPixel.HasValue &&
                    c.XPixel.HasValue && c.YPixel.HasValue &&
                    Math.Abs(s.XPixel.Value - c.XPixel.Value) <= 20 &&
                    Math.Abs(s.YPixel.Value - c.YPixel.Value) <= 20);

                if (!tooClose && selected.Count < maxCompStars)
                    selected.Add(c);
                else
                    backfill.Add(c);
            }
        }

        if (selected.Count == 0)
        {
            SessionLogService.Write("[GaiaComp] No candidates survived spatial deduplication");
            return new CompResult([], "⚠  Pipeline produced no valid pixel positions", GaiaUsed: gaiaOk);
        }

        // ── Progress: catalog queries done, starting GSPC ────────────────────
        progress?.Report("⟳  Calibrating magnitudes (GSPC)…");

        // ── GSPC synthetic photometry query (batched by source_id) ────────────
        Log($"[Stone]", logProgress);
        Log($"[Stone] ── Step 7/8: GSPC synthetic photometry ──────────────────────────", logProgress);
        var sourceIds = isolated
            .Where(c => c.GaiaId != null)
            .Select(c => c.GaiaId!)
            .Distinct()
            .ToList();

        Dictionary<string, (double Mag, double Err)> gspcMags = [];
        if (sourceIds.Count > 0)
        {
            Log($"[Stone]   Querying {filterCode}-band GSPC for {sourceIds.Count} source IDs", logProgress);
            try
            {
                gspcMags = await QueryGspcAsync(sourceIds, filterCode, ct);
                Log($"[Stone]   GSPC matched: {gspcMags.Count}/{sourceIds.Count}", logProgress);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Log($"[Stone]   ✗  GSPC query failed: {ex.Message}", logProgress);
            }
        }
        else
        {
            Log($"[Stone]   No Gaia source IDs — GSPC skipped", logProgress);
        }

        // ── Assign magnitudes before PSF (needed for backfill comparison) ─────
        AssignMagnitudes(isolated, filterCode, gspcMags, apassStars);

        // Log magnitude source breakdown
        int _nGspc  = isolated.Count(c => c.FinalMagSource == "GSPC");
        int _nApass = isolated.Count(c => c.FinalMagSource == "APASS");
        int _nGtoV  = isolated.Count(c => c.FinalMagSource == "G→V");
        int _nVsp   = isolated.Count(c => c.FinalMagSource == "VSP");
        int _nNone  = isolated.Count(c => c.FinalMagSource is null);
        Log($"[Stone]   Magnitude sources: VSP:{_nVsp}  GSPC:{_nGspc}  APASS:{_nApass}  G→V:{_nGtoV}" +
            (_nNone > 0 ? $"  none:{_nNone}" : ""), logProgress);

        // ── Progress: GSPC done, starting PSF ────────────────────────────────
        progress?.Report($"⟳  Validating {selected.Count} comp candidates (PSF)…");

        // ── PSF measurement + validation loop (up to 3 passes) ───────────────
        Log($"[Stone]", logProgress);
        Log($"[Stone] ── Step 8/8: PSF validation ─────────────────────────────────────", logProgress);

        float[,]? imageData     = null;
        double    saturationAdu = 60_000;
        double    gainEPerAdu   = 1.0;

        try
        {
            imageData     = await Task.Run(() => PsfService.ReadFitsPixels(fitsPath), ct);
            if (hdr != null)
            {
                saturationAdu = PsfService.EstimateSaturation(hdr);
                gainEPerAdu   = PsfService.GetGain(hdr);
            }
            if (imageData != null)
                Log($"[Stone]   FITS pixels: {imageData.GetLength(1)}×{imageData.GetLength(0)} px  |  " +
                    $"sat={saturationAdu:F0} ADU  |  gain={gainEPerAdu:F2} e-/ADU",
                    logProgress);
            else
                Log("[Stone]   ⚠  FITS pixel read returned null — PSF validation skipped", logProgress);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log($"[Stone]   ✗  FITS pixel read error: {ex.Message}", logProgress);
        }

        int psfRejected = 0;
        if (imageData != null && selected.Count > 0)
        {
            for (int pass = 0; pass < 3; pass++)
            {
                Log($"[Stone]   Pass {pass + 1}:", logProgress);

                // Measure PSF for all currently selected stars
                var psfResults = new List<(GaiaCandidate Star, PsfService.PsfResult Psf)>();
                foreach (var s in selected)
                {
                    if (!s.XPixel.HasValue || !s.YPixel.HasValue) continue;
                    var psf = PsfService.Measure(
                        imageData, s.XPixel.Value, s.YPixel.Value,
                        saturationAdu, gainEPerAdu);
                    s.FwhmX   = psf.FwhmX;
                    s.FwhmY   = psf.FwhmY;
                    s.FwhmPx  = psf.FwhmMean;
                    s.PeakAdu = psf.PeakAdu;
                    s.Snr     = psf.Snr;
                    s.Saturated = psf.Saturated;
                    psfResults.Add((s, psf));
                }

                double medFwhm = PsfService.MedianFwhm(psfResults.Select(p => p.Psf));
                if (medFwhm > 0)
                    Log($"[Stone]     Median FWHM: {medFwhm:F1} px", logProgress);

                // Identify stars to reject — log every star's PSF result
                var toReject = new List<GaiaCandidate>();
                int compRank = 1;
                foreach (var (star, psf) in psfResults)
                {
                    string? reason = null;
                    if (psf.Saturated)
                        reason = $"SATURATED (peak {psf.PeakAdu:F0} ADU)";
                    else if (!psf.Success)
                        reason = $"PSF failed: {psf.Message}";
                    else if (psf.Snr is > 0 and < 75)
                        reason = $"low SNR ({psf.Snr:F0} < 75)";
                    else if (medFwhm > 0 && psf.FwhmMean > medFwhm * 1.8)
                        reason = $"FWHM outlier ({psf.FwhmMean:F1}px vs median {medFwhm:F1}px)";
                    else if (psf.FwhmX > 0.1 && psf.FwhmY > 0.1)
                    {
                        double ratio = Math.Max(psf.FwhmX, psf.FwhmY) /
                                       Math.Max(0.1, Math.Min(psf.FwhmX, psf.FwhmY));
                        if (ratio > 2.0)
                            reason = $"elongated PSF (ratio {ratio:F1})";
                    }

                    string statusIcon = reason is null ? "✓" : "✗";
                    string psfLine =
                        $"[Stone]     C{compRank}  {CandLabel(star),-20}  " +
                        $"FWHM={psf.FwhmMean:F1}px  SNR={psf.Snr:F0}  peak={psf.PeakAdu:F0}  " +
                        $"{statusIcon}{(reason != null ? " " + reason : " OK")}";
                    Log(psfLine, logProgress);

                    if (reason != null)
                    {
                        star.RejectReason = reason;
                        toReject.Add(star);
                    }
                    compRank++;
                }

                if (toReject.Count == 0)
                {
                    Log($"[Stone]     All {psfResults.Count} comp stars passed PSF check", logProgress);
                    break;   // all pass
                }

                psfRejected += toReject.Count;
                int replaced = 0;

                foreach (var bad in toReject)
                {
                    selected.Remove(bad);
                    backfill.Add(bad);

                    // Find next backfill candidate that passes spatial dedup against survivors
                    for (int bi = 0; bi < backfill.Count - 1; bi++)  // -1 excludes the one just added
                    {
                        var candidate = backfill[bi];
                        if (!candidate.XPixel.HasValue || !candidate.YPixel.HasValue) continue;

                        bool tooClose = selected.Any(s =>
                            s.XPixel.HasValue && s.YPixel.HasValue &&
                            Math.Abs(s.XPixel.Value - candidate.XPixel.Value) <= 20 &&
                            Math.Abs(s.YPixel.Value - candidate.YPixel.Value) <= 20);

                        if (!tooClose)
                        {
                            backfill.RemoveAt(bi);
                            selected.Add(candidate);
                            replaced++;
                            Log($"[Stone]     Backfill: added {CandLabel(candidate)} (score={candidate.Score:F3})", logProgress);
                            break;
                        }
                    }
                }

                Log($"[Stone]     Pass {pass + 1}: {toReject.Count} rejected, {replaced} backfilled", logProgress);
                if (replaced == 0) break;  // backfill exhausted
            }

            if (psfRejected > 0)
                Log($"[Stone]   PSF validation total: {psfRejected} rejected, {selected.Count} final comp stars", logProgress);
        }

        // ── Target PSF measurement ────────────────────────────────────────────
        PsfService.PsfResult? targetPsf = null;
        if (imageData != null && targetXPx.HasValue && targetYPx.HasValue)
        {
            try
            {
                targetPsf = PsfService.Measure(
                    imageData, targetXPx.Value, targetYPx.Value,
                    saturationAdu, gainEPerAdu);
                Log($"[Stone]", logProgress);
                Log($"[Stone] ── Target PSF ────────────────────────────────────────────────────", logProgress);
                Log($"[Stone]   pixel: ({targetXPx},{targetYPx})  FWHM={targetPsf.FwhmMean:F1}px  " +
                    $"SNR={targetPsf.Snr:F0}  peak={targetPsf.PeakAdu:F0} ADU" +
                    $"{(targetPsf.Saturated ? "  ⚠ SATURATED" : "")}",
                    logProgress);
                if (targetPsf.Snr > 0)
                    Log($"[Stone]   Photon-limited precision ≈ {1000.0 / targetPsf.Snr:F1} mmag", logProgress);
            }
            catch (Exception ex)
            {
                Log($"[Stone]   Target PSF failed: {ex.Message}", logProgress);
            }
        }

        // ── Compute image quality metrics ─────────────────────────────────────
        var _compFwhms = selected
            .Where(c => c.FwhmPx.HasValue && c.FwhmPx > 0)
            .Select(c => c.FwhmPx!.Value)
            .ToList();

        double? _fwhmMean = _compFwhms.Count > 0 ? _compFwhms.Average() : null;
        double? _fwhmMin  = _compFwhms.Count > 0 ? _compFwhms.Min()     : null;
        double? _fwhmMax  = _compFwhms.Count > 0 ? _compFwhms.Max()     : null;
        string  _fwhmUniformity = "—";
        if (_fwhmMean is > 0 && _fwhmMin.HasValue && _fwhmMax.HasValue)
        {
            double _pct = (_fwhmMax.Value - _fwhmMin.Value) / _fwhmMean.Value;
            _fwhmUniformity = _pct < 0.15 ? "uniform" : _pct < 0.35 ? "mild" : "significant";
        }

        double? _targetSnr     = targetPsf?.Snr is > 0 ? targetPsf.Snr : null;
        double? _precisionMmag = _targetSnr is > 0 ? Math.Round(1000.0 / _targetSnr!.Value, 1) : null;
        bool    _targetSat     = targetPsf?.Saturated ?? false;

        string _grade;
        if (_targetSat)                                    _grade = "poor";
        else if (selected.Count < 2)                       _grade = "marginal";
        else if (_fwhmUniformity == "significant")         _grade = "marginal";
        else if (_compFwhms.Count == 0)                    _grade = "marginal";   // all PSF measurements failed
        else if (psfRejected > 2 || !targetBpRp.HasValue)  _grade = "acceptable";
        else if (_fwhmUniformity == "mild")                _grade = "acceptable";
        else                                               _grade = "good";

        string _qSummary = $"{selected.Count} comp stars | {(targetBpRp.HasValue ? "color match active" : "no color match")}";
        if (_precisionMmag.HasValue) _qSummary += $" | ~{_precisionMmag:F1} mmag";
        if (_fwhmMean.HasValue) _qSummary += $" | FWHM {_fwhmMean:F1}px {_fwhmUniformity}";

        var quality = new ImageQualityInfo(
            ColorMatchActive: targetBpRp.HasValue,
            TargetSnr:        _targetSnr.HasValue ? Math.Round(_targetSnr.Value, 0) : null,
            PrecisionMmag:    _precisionMmag,
            FwhmMeanPx:       _fwhmMean.HasValue ? Math.Round(_fwhmMean.Value, 1) : null,
            FwhmMinPx:        _fwhmMin.HasValue  ? Math.Round(_fwhmMin.Value, 1)  : null,
            FwhmMaxPx:        _fwhmMax.HasValue  ? Math.Round(_fwhmMax.Value, 1)  : null,
            FwhmUniformity:   _fwhmUniformity,
            TargetSaturated:  _targetSat,
            OverallGrade:     _grade,
            Summary:          _qSummary);

        // ── Build pixel-coordinate pairs and CompStarInfo list ────────────────
        var pairs = new List<(int X, int Y)>();
        var stars = new List<CompStarInfo>();

        foreach (var c in selected.Where(c => c.XPixel.HasValue && c.YPixel.HasValue))
        {
            pairs.Add((c.XPixel!.Value, c.YPixel!.Value));
            stars.Add(new CompStarInfo(
                X:             c.XPixel.Value,
                Y:             c.YPixel.Value,
                Ra:            c.Ra,
                Dec:           c.Dec,
                Mag:           c.FinalMag,
                MagErr:        c.FinalMagErr,
                MagSource:     c.FinalMagSource,
                FilterBand:    filterCode,
                FwhmPx:        c.FwhmPx,
                Snr:           c.Snr,
                Saturated:     c.Saturated,
                CatalogSource: c.Source,
                GaiaId:        c.GaiaId,
                Auid:          c.Auid,
                SepArcsec:     c.SeparationArcsec,
                BpRp:          c.BpRp,
                Ruwe:          c.Ruwe));
        }

        if (pairs.Count == 0)
        {
            SessionLogService.Write("[GaiaComp] Selected stars had no valid pixel coordinates after PSF pass");
            return new CompResult([], "⚠  Pipeline produced no valid pixel positions", GaiaUsed: gaiaOk);
        }

        // ── Rejected candidates — tested (selected, then PSF-validated) but excluded ──
        var rejected = backfill
            .Where(c => c.RejectReason != null && c.XPixel.HasValue && c.YPixel.HasValue)
            .Select(c => new RejectedCompInfo(
                X:             c.XPixel!.Value,
                Y:             c.YPixel!.Value,
                Mag:           c.FinalMag,
                MagSource:     c.FinalMagSource,
                CatalogSource: c.Source,
                Reason:        c.RejectReason!))
            .ToList();

        // ── Status message ────────────────────────────────────────────────────
        int nVsp  = stars.Count(s => s.CatalogSource == "VSP");
        int nGaia = stars.Count(s => s.CatalogSource == "Gaia");

        string src;
        if (!gaiaOk && useVsp)          src = "AAVSO VSP (Gaia unavailable)";
        else if (!gaiaOk)               src = "✗  Gaia unavailable";
        else if (!useVsp)               src = "Stone (Gaia + APASS)";
        else if (nVsp > 0 && nGaia > 0) src = $"Stone + VSP ({nVsp} VSP, {nGaia} Stone)";
        else if (nVsp > 0)              src = "VSP-only (Gaia-enriched)";
        else                            src = "Stone (Gaia + APASS)";

        // Magnitude source breakdown
        int nGspc  = stars.Count(s => s.MagSource == "GSPC");
        int nApass = stars.Count(s => s.MagSource == "APASS");
        int nGtoV  = stars.Count(s => s.MagSource == "G→V");
        int nVspM  = stars.Count(s => s.MagSource == "VSP");
        int nNoMag = stars.Count(s => s.MagSource is null);

        var magBits = new List<string>();
        if (nVspM  > 0) magBits.Add($"VSP:{nVspM}");
        if (nGspc  > 0) magBits.Add($"GSPC:{nGspc}");
        if (nApass > 0) magBits.Add($"APASS:{nApass}");
        if (nGtoV  > 0) magBits.Add($"G→V:{nGtoV}");
        if (nNoMag > 0) magBits.Add($"no mag:{nNoMag}");
        string magSummary = magBits.Count > 0 ? " | mags: " + string.Join(" ", magBits) : "";

        // PSF summary
        string psfSummary = "";
        if (imageData != null)
        {
            if (psfRejected > 0)
                psfSummary = $" | PSF: {psfRejected} rejected";
            else
                psfSummary = $" | PSF: {pairs.Count}/{pairs.Count} OK";
        }

        var failedSources = new List<string>();
        if (!gaiaOk && !string.IsNullOrEmpty(gaiaError)) failedSources.Add("Gaia DR3");
        if (vspFailed)   failedSources.Add("AAVSO VSP");
        if (apassFailed) failedSources.Add("APASS DR9");
        string degradedNote = failedSources.Count > 0
            ? $"  ⚠ continued after {string.Join(" + ", failedSources)} failure — ensemble may be smaller/less precise than usual"
            : "";

        string msg = $"✓  {pairs.Count} comp star{(pairs.Count == 1 ? "" : "s")} — {src}{magSummary}{psfSummary}{degradedNote}";

        // Itemized final comp list — RA/Dec/catalog ID/mag/source for each selected star,
        // enough to diff directly against an independent implementation's output star-by-star.
        Log($"[Stone]", logProgress);
        Log($"[Stone]   Final comp list ({stars.Count}):", logProgress);
        foreach (var s in stars)
            Log($"[Stone]     RA={s.Ra:F6}  Dec={s.Dec:F6}  Gaia={s.GaiaId ?? "—"}  " +
                $"Mag={s.Mag?.ToString("F3") ?? "—"} ({s.MagSource ?? "—"})  " +
                $"Src={s.CatalogSource}  Sep={s.SepArcsec:F1}\"  BP-RP={s.BpRp?.ToString("F2") ?? "—"}  " +
                $"RUWE={s.Ruwe?.ToString("F2") ?? "—"}", logProgress);

        // Final verbose summary
        Log($"[Stone]", logProgress);
        Log($"[Stone] ══════════════════════════════════════════════════════════", logProgress);
        Log($"[Stone] ✓  {pairs.Count} comp star{(pairs.Count == 1 ? "" : "s")} selected  ({(nVsp > 0 ? $"{nVsp} VSP" : "")}{(nVsp > 0 && nGaia > 0 ? " + " : "")}{(nGaia > 0 ? $"{nGaia} Stone" : "")})", logProgress);
        Log($"[Stone]    Filter: {filterCode}  |  Mags: {string.Join("  ", magBits)}", logProgress);
        if (_fwhmMean.HasValue)
            Log($"[Stone]    FWHM: {_fwhmMean:F1}px mean  [{_fwhmMin:F1}..{_fwhmMax:F1}]  →  {_fwhmUniformity}", logProgress);
        if (psfRejected > 0)
            Log($"[Stone]    PSF: {psfRejected} rejected, {pairs.Count} final", logProgress);
        Log($"[Stone]    Color match: {(_grade != "—" && targetBpRp.HasValue ? $"ACTIVE (target BP-RP={targetBpRp:F2})" : "DISABLED")}  |  Grade: {_grade}", logProgress);
        if (_precisionMmag.HasValue)
            Log($"[Stone]    Target precision ≈ {_precisionMmag:F1} mmag  (SNR={_targetSnr:F0})", logProgress);
        Log($"[Stone]    Total elapsed: {_sw.Elapsed.TotalSeconds:F1}s", logProgress);
        Log($"[Stone] ══════════════════════════════════════════════════════════", logProgress);

        return new CompResult(pairs, msg, GaiaUsed: gaiaOk, Stars: stars, Quality: quality, Rejected: rejected,
            TargetNeighborWarning: targetNeighborWarning);
    }

    // ── Gaia DR3 TAP query ────────────────────────────────────────────────────

    private static async Task<List<GaiaCandidate>?> QueryGaiaAsync(
        double             ra,          double dec,
        double             radiusDeg,   double magFloor,
        IProgress<string>? progress,
        CancellationToken  ct)
    {
        string adql = $"""
            SELECT source_id, ra, dec, phot_g_mean_mag, bp_rp,
                   ruwe, phot_g_mean_flux_over_error, phot_variable_flag,
                   pmra, pmdec, ref_epoch,
                   ipd_frac_multi_peak, ipd_gof_harmonic_amplitude,
                   non_single_star, duplicated_source
            FROM gaiadr3.gaia_source
            WHERE CONTAINS(
                POINT('ICRS', ra, dec),
                CIRCLE('ICRS', {ra:F6}, {dec:F6}, {radiusDeg:F6})
            ) = 1
            AND phot_g_mean_mag < {magFloor:F2}
            ORDER BY phot_g_mean_mag ASC
            """;

        string? csv = null;
        Exception? lastEx = null;

        for (int i = 0; i < TapEndpoints.Length; i++)
        {
            var endpoint = TapEndpoints[i];
            if (i > 0)
                progress?.Report($"⟳  Gaia mirror {i} timed out — trying mirror {i + 1}…");

            try
            {
                using var form = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["REQUEST"] = "doQuery",
                    ["LANG"]    = "ADQL",
                    ["FORMAT"]  = "csv",
                    ["MAXREC"]  = "2000",
                    ["QUERY"]   = adql,
                });
                using var resp = await Http.PostAsync(endpoint, form, ct);
                resp.EnsureSuccessStatusCode();
                var body = await resp.Content.ReadAsStringAsync(ct);

                if (!string.IsNullOrWhiteSpace(body) &&
                    body.TrimStart().StartsWith("source_id", StringComparison.OrdinalIgnoreCase))
                {
                    csv = body;
                    break;
                }
                SessionLogService.Write($"[GaiaComp] TAP {endpoint}: unexpected response, trying next mirror");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                lastEx = ex;
                SessionLogService.Write($"[GaiaComp] TAP {endpoint} failed: {ex.Message}");
            }
        }

        if (csv is null)
            throw lastEx ?? new InvalidOperationException("All Gaia TAP mirrors failed");

        return ParseGaiaCsv(csv);
    }

    // ── Gaia GSPC synthetic photometry query ──────────────────────────────────

    private static async Task<Dictionary<string, (double Mag, double Err)>> QueryGspcAsync(
        List<string> sourceIds, string filterCode, CancellationToken ct)
    {
        var result = new Dictionary<string, (double, double)>();

        if (!GspcMagColMap.TryGetValue(filterCode, out var magCol) || sourceIds.Count == 0)
            return result;

        // Derive flux column names: v_jkc_mag → v_jkc_flux, v_jkc_flux_error
        string baseCol     = magCol.EndsWith("_mag", StringComparison.Ordinal)
            ? magCol[..^4] : magCol;
        string fluxCol     = $"{baseCol}_flux";
        string fluxErrCol  = $"{baseCol}_flux_error";

        string idList = string.Join(",", sourceIds.Select(s =>
        {
            if (long.TryParse(s, out var l)) return l.ToString();
            return s;
        }));

        string adql = $"""
            SELECT source_id, {magCol}, {fluxCol}, {fluxErrCol}
            FROM gaiadr3.synthetic_photometry_gspc
            WHERE source_id IN ({idList})
            """;

        string? csv = null;
        foreach (var endpoint in TapEndpoints)
        {
            try
            {
                using var form = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["REQUEST"] = "doQuery",
                    ["LANG"]    = "ADQL",
                    ["FORMAT"]  = "csv",
                    ["MAXREC"]  = (sourceIds.Count + 10).ToString(),
                    ["QUERY"]   = adql,
                });
                using var resp = await Http.PostAsync(endpoint, form, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    SessionLogService.Write($"[GaiaComp] GSPC {endpoint}: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
                    continue;
                }
                var body = await resp.Content.ReadAsStringAsync(ct);
                if (!string.IsNullOrWhiteSpace(body) &&
                    body.TrimStart().StartsWith("source_id", StringComparison.OrdinalIgnoreCase))
                {
                    csv = body;
                    break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                SessionLogService.Write($"[GaiaComp] GSPC {endpoint} failed: {ex.Message}");
            }
        }

        if (csv is null) return result;

        var lines   = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2) return result;

        var headers = SplitCsv(lines[0]);
        int iSid  = ColIdx(headers, "source_id");
        int iMag  = ColIdx(headers, magCol);
        int iFlux = ColIdx(headers, fluxCol);
        int iFErr = ColIdx(headers, fluxErrCol);

        for (int i = 1; i < lines.Length; i++)
        {
            var cols = SplitCsv(lines[i]);
            if (cols.Count < 2) continue;

            var sid = GetS(cols, iSid);
            var mag = ParseD(cols, iMag);
            if (string.IsNullOrEmpty(sid) || mag is null) continue;

            // σ_mag = 1.0857 × σ_flux / flux  (floor 0.005)
            double err = 0.015;
            var flux  = ParseD(cols, iFlux);
            var fErr  = ParseD(cols, iFErr);
            if (flux is > 0 && fErr is > 0)
            {
                double sigma = 1.0857 * fErr.Value / flux.Value;
                err = Math.Max(sigma, 0.005);
            }

            result[sid] = (mag.Value, err);
        }

        return result;
    }

    // ── APASS DR9 VizieR query ────────────────────────────────────────────────

    private static async Task<(List<ApassStar> Stars, string Message, bool Failed)> QueryApassAsync(
        double ra, double dec, double fovArcmin, string filterCode,
        CancellationToken ct)
    {
        if (!ApassFilterMap.TryGetValue(filterCode, out var colPair))
            return ([], $"Filter '{filterCode}' not mapped in APASS — skipped", false);

        double radiusDeg = fovArcmin / 120.0;   // half-FOV in degrees

        // Request all common APASS magnitude columns; parse what arrives.
        // The Sloan-like g/r/i columns carry a literal apostrophe in VizieR's own schema
        // (confirmed via TAP_SCHEMA.columns for "II/336/apass9": g'mag, r'mag, i'mag and
        // their e_*'mag error columns) — not g_mag/r_mag/i_mag, which VizieR rejects with a
        // 400 "unresolved identifiers" error on every query regardless of filter, since all
        // six magnitude columns are always requested up front.
        string adql = $"""
            SELECT RAJ2000, DEJ2000, Bmag, e_Bmag, Vmag, e_Vmag,
                   "g'mag", "e_g'mag", "r'mag", "e_r'mag", "i'mag", "e_i'mag"
            FROM "II/336/apass9"
            WHERE CONTAINS(
                POINT('ICRS', RAJ2000, DEJ2000),
                CIRCLE('ICRS', {ra:F6}, {dec:F6}, {radiusDeg:F6})
            ) = 1
            """;

        string? csv = null;
        foreach (var endpoint in VizierEndpoints)
        {
            try
            {
                using var form = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["REQUEST"] = "doQuery",
                    ["LANG"]    = "ADQL",
                    ["FORMAT"]  = "csv",
                    ["MAXREC"]  = "2000",
                    ["QUERY"]   = adql,
                });
                using var resp = await Http.PostAsync(endpoint, form, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    SessionLogService.Write($"[GaiaComp] APASS {endpoint}: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
                    continue;
                }
                var body = await resp.Content.ReadAsStringAsync(ct);
                // VizieR returns headers starting with "RAJ2000" or "_RAJ2000"
                if (!string.IsNullOrWhiteSpace(body) && body.Length > 10)
                {
                    var firstLine = body.Split('\n', 2)[0].ToUpperInvariant();
                    if (firstLine.Contains("RAJ2000") || firstLine.Contains("DEJ2000"))
                    {
                        csv = body;
                        break;
                    }
                }
                SessionLogService.Write($"[GaiaComp] APASS {endpoint}: unexpected response");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                SessionLogService.Write($"[GaiaComp] APASS {endpoint} failed: {ex.Message}");
            }
        }

        if (csv is null)
            return ([], "APASS query failed on all VizieR mirrors", true);

        return ParseApassCsv(csv, colPair.MagCol, colPair.ErrCol);
    }

    // ── APASS CSV parser ──────────────────────────────────────────────────────

    private static (List<ApassStar> Stars, string Message, bool Failed) ParseApassCsv(
        string csv, string magCol, string errCol)
    {
        var stars = new List<ApassStar>();
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        // A header-only response (reached this point because the caller already confirmed
        // the header contains RAJ2000/DEJ2000) means the query succeeded but matched zero
        // rows — a real, successful outcome (e.g. a gap in APASS DR9's sky coverage), not a
        // failure. Must not be flagged Failed=true, or the caller pops a misleading
        // "failed on all mirrors" confirmation dialog for a query that didn't actually fail.
        if (lines.Length < 2) return (stars, "No APASS DR9 stars in this field (possible survey coverage gap)", false);

        var headers = SplitCsv(lines[0]);

        // VizieR may return _RAJ2000 / _DEJ2000 (computed) or RAJ2000 / DEJ2000 (native)
        int iRa  = ColIdx(headers, "RAJ2000");
        if (iRa < 0) iRa = ColIdx(headers, "_RAJ2000");
        int iDec = ColIdx(headers, "DEJ2000");
        if (iDec < 0) iDec = ColIdx(headers, "_DEJ2000");

        int iMag = ColIdx(headers, magCol);
        int iErr = ColIdx(headers, errCol);

        if (iRa < 0 || iDec < 0 || iMag < 0)
            return (stars, $"APASS CSV missing required columns (ra={iRa} dec={iDec} mag={iMag})", true);

        for (int i = 1; i < lines.Length; i++)
        {
            var cols = SplitCsv(lines[i]);
            var starRa  = ParseD(cols, iRa);
            var starDec = ParseD(cols, iDec);
            var mag     = ParseD(cols, iMag);
            if (starRa is null || starDec is null || mag is null) continue;

            double err = ParseD(cols, iErr) ?? 0.03;
            stars.Add(new ApassStar(starRa.Value, starDec.Value, mag.Value, err));
        }

        return (stars, $"{stars.Count} stars ({magCol})", false);
    }

    // ── VSP query ─────────────────────────────────────────────────────────────

    private static async Task<(List<GaiaCandidate> Stars, string Message, bool Failed)> QueryVspAsync(
        double targetRa, double targetDec, double fovArcmin,
        string filterCode, double magLimit, CancellationToken ct)
    {
        if (!VspFilterMap.TryGetValue(filterCode, out var vspFilter))
            return ([], $"Filter '{filterCode}' not in VSP — using Gaia only", false);

        double fov = Math.Clamp(fovArcmin * 1.2, 10.0, 90.0);
        // Same AAVSO subdomain move as QueryVsxAsync above — www.aavso.org/apps/vsp/... now
        // redirects through a Cloudflare bot-challenge; apps.aavso.org/vsp/... (no "/apps"
        // prefix on this host) is the working replacement, confirmed live 2026-09-06.
        string url = $"https://apps.aavso.org/vsp/api/chart/?ra={targetRa:F6}&dec={targetDec:F6}" +
                     $"&fov={fov:F1}&maglimit={magLimit:F1}&format=json";

        string json;
        try
        {
            using var resp = await Http.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();
            json = await resp.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            SessionLogService.Write($"[GaiaComp] VSP request failed: {ex.Message}");
            return ([], $"VSP request failed: {ex.Message}", true);
        }

        var vspStars = new List<GaiaCandidate>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("photometry", out var phot))
            {
                SessionLogService.Write("[GaiaComp] VSP response contained no 'photometry' field");
                return ([], "No comparison stars in VSP response", false);
            }

            foreach (var star in phot.EnumerateArray())
            {
                if (!star.TryGetProperty("ra",  out var raEl) ||
                    !star.TryGetProperty("dec", out var decEl)) continue;

                if (!TryParseAngle(raEl.GetString()  ?? "", false, out double sRa) ||
                    !TryParseAngle(decEl.GetString() ?? "", true,  out double sDec)) continue;

                double sep = SepArcsec(targetRa, targetDec, sRa, sDec);
                if (sep < 30.0) continue;

                double? mag = null;
                if (star.TryGetProperty("bands", out var bands))
                {
                    foreach (var b in bands.EnumerateArray())
                    {
                        if (!b.TryGetProperty("band", out var bandEl)) continue;
                        if (bandEl.GetString() != vspFilter) continue;
                        if (!b.TryGetProperty("mag", out var magEl)) continue;
                        var ms = magEl.ValueKind == JsonValueKind.String
                            ? magEl.GetString() ?? "" : magEl.GetRawText();
                        if (double.TryParse(ms, NumberStyles.Any,
                                            CultureInfo.InvariantCulture, out var mv))
                            mag = mv;
                        break;
                    }
                }
                if (mag is null) continue;

                string? auid = star.TryGetProperty("auid", out var aEl) ? aEl.GetString() : null;

                vspStars.Add(new GaiaCandidate
                {
                    Source           = "VSP",
                    Ra               = sRa,
                    Dec              = sDec,
                    Auid             = auid,
                    VspMag           = mag,
                    SeparationArcsec = sep,
                });
            }
        }
        catch (Exception ex)
        {
            SessionLogService.Write($"[GaiaComp] VSP parse error: {ex.Message}");
            return ([], $"VSP parse error: {ex.Message}", true);
        }

        return vspStars.Count > 0
            ? (vspStars, $"{vspStars.Count} stars (VSP {vspFilter} band)", false)
            : ([], "No VSP stars in this field with the selected filter", false);
    }

    // ── VSX (AAVSO Variable Star Index) query ─────────────────────────────────

    /// <summary>
    /// Returns all known variables in the AAVSO Variable Star Index (VSX) within
    /// <paramref name="radiusDeg"/> of the given position, up to
    /// <paramref name="magLimit"/>. Coordinates returned in decimal degrees.
    /// Exceptions propagate — the caller is responsible for fail-open handling.
    /// </summary>
    private static async Task<List<(double Ra, double Dec, string Name)>> QueryVsxAsync(
        double ra, double dec, double radiusDeg, double magLimit, CancellationToken ct)
    {
        // AAVSO moved VSX to vsx.aavso.org — the old www.aavso.org/vsx/... path now redirects
        // through a Cloudflare bot-challenge no plain HTTP client can pass (confirmed live,
        // 2026-09-06 — every call here was silently failing). Same fix TargetResolverService's
        // own VSX lookup already got on 2026-09-04.
        string url = "https://vsx.aavso.org/index.php?view=api.list" +
                     $"&ra={ra:F6}&dec={dec:F6}&radius={radiusDeg:F6}" +
                     $"&tomag={magLimit:F1}&format=json";

        using var resp = await Http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);

        var result = new List<(double Ra, double Dec, string Name)>();
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("VSXObjects", out var vsxObjects) ||
            !vsxObjects.TryGetProperty("VSXObject",  out var vsxObject))
            return result;  // 0 results or empty field

        // VSX API quirk: single result → JSON object; multiple results → JSON array
        IEnumerable<JsonElement> elements = vsxObject.ValueKind == JsonValueKind.Array
            ? vsxObject.EnumerateArray()
            : new[] { vsxObject };

        foreach (var star in elements)
        {
            string? raStr  = star.TryGetProperty("RA2000",          out var raEl)   ? raEl.GetString()   : null;
            string? decStr = star.TryGetProperty("Declination2000", out var decEl)  ? decEl.GetString()  : null;
            string  name   = star.TryGetProperty("Name",            out var nameEl) ? nameEl.GetString() ?? "?" : "?";

            if (raStr is null || decStr is null) continue;
            // RA2000 / Declination2000 are sexagesimal ("HH MM SS.S" / "±DD MM SS.S");
            // TryParseAngle handles both sexagesimal and decimal-degree strings.
            if (!TryParseAngle(raStr,  false, out double sRa))  continue;
            if (!TryParseAngle(decStr, true,  out double sDec)) continue;

            result.Add((sRa, sDec, name));
        }

        return result;
    }

    // ── Magnitude preference chain ────────────────────────────────────────────

    /// <summary>
    /// Assign filter-matched magnitudes to all candidates in-place.
    /// Priority: VSP catalog → GSPC synthetic photometry → APASS DR9 → G→V polynomial.
    /// VSP goes first (not ranked by raw numerical precision) because it's the AAVSO-vetted
    /// sequence value — the same reference every other observer submitting for this star uses,
    /// which matters more for AID cross-observer consistency than shaving a few mmag via GSPC.
    /// G→V is last-resort only: a hardcoded polynomial (Evans et al. 2018) applied regardless
    /// of filter, so it's the one source that can be filter-mismatched against a non-V observation.
    /// </summary>
    private static void AssignMagnitudes(
        IEnumerable<GaiaCandidate>                 candidates,
        string                                     filterCode,
        Dictionary<string, (double Mag, double Err)> gspcMags,
        List<ApassStar>                            apassStars)
    {
        foreach (var c in candidates)
        {
            // Priority 1: VSP catalog magnitude (AAVSO-vetted reference sequence)
            if (c.VspMag.HasValue)
            {
                c.FinalMag       = c.VspMag;
                c.FinalMagErr    = null;
                c.FinalMagSource = "VSP";
                continue;
            }

            // Priority 2: GSPC synthetic photometry (~0.005-0.015 mag accuracy)
            if (c.GaiaId != null && gspcMags.TryGetValue(c.GaiaId, out var gspc))
            {
                c.GspcMag        = gspc.Mag;
                c.GspcMagErr     = gspc.Err;
                c.FinalMag       = gspc.Mag;
                c.FinalMagErr    = gspc.Err;
                c.FinalMagSource = "GSPC";
                continue;
            }

            // Priority 3: APASS DR9 position match (~0.02-0.05 mag accuracy)
            if (apassStars.Count > 0)
            {
                ApassStar? bestApass = null;
                double bestSep = double.MaxValue;
                foreach (var a in apassStars)
                {
                    double sep = SepArcsec(c.Ra, c.Dec, a.Ra, a.Dec);
                    if (sep < 5.0 && sep < bestSep) { bestSep = sep; bestApass = a; }
                }
                if (bestApass is not null)
                {
                    c.ApassMag       = bestApass.Mag;
                    c.ApassMagErr    = bestApass.MagErr;
                    c.FinalMag       = bestApass.Mag;
                    c.FinalMagErr    = bestApass.MagErr;
                    c.FinalMagSource = "APASS";
                    continue;
                }
            }

            // Priority 4: Hardcoded G→V polynomial (Evans et al. 2018) — true last resort,
            // applied regardless of filter, so labelled "G→V" so the user knows when it's used.
            if (c.GMag.HasValue && c.BpRp.HasValue)
            {
                double x      = c.BpRp.Value;
                double vMinG  = -0.01760 + 0.006860 * x + 0.1732 * x * x;
                c.FinalMag       = c.GMag.Value + vMinG;
                c.FinalMagErr    = null;
                c.FinalMagSource = "G→V";
            }
        }
    }

    // ── Gaia CSV parser ───────────────────────────────────────────────────────

    private static List<GaiaCandidate> ParseGaiaCsv(string csv)
    {
        var result = new List<GaiaCandidate>();
        var lines  = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2) return result;

        var headers = SplitCsv(lines[0]);
        int iSid  = ColIdx(headers, "source_id");
        int iRa   = ColIdx(headers, "ra");
        int iDec  = ColIdx(headers, "dec");
        int iGmag = ColIdx(headers, "phot_g_mean_mag");
        int iBpRp = ColIdx(headers, "bp_rp");
        int iRuwe = ColIdx(headers, "ruwe");
        int iFoe  = ColIdx(headers, "phot_g_mean_flux_over_error");
        int iVar  = ColIdx(headers, "phot_variable_flag");
        int iPmra = ColIdx(headers, "pmra");
        int iPmdc = ColIdx(headers, "pmdec");
        int iEpch = ColIdx(headers, "ref_epoch");
        int iFmp  = ColIdx(headers, "ipd_frac_multi_peak");
        int iGof  = ColIdx(headers, "ipd_gof_harmonic_amplitude");
        int iNss  = ColIdx(headers, "non_single_star");
        int iDup  = ColIdx(headers, "duplicated_source");

        for (int i = 1; i < lines.Length; i++)
        {
            var c = SplitCsv(lines[i]);
            if (c.Count < 3) continue;
            try
            {
                double? ra  = ParseD(c, iRa);
                double? dec = ParseD(c, iDec);
                if (ra is null || dec is null) continue;

                string varFlag = GetS(c, iVar);
                bool isVar = varFlag.Contains("VARIABLE", StringComparison.OrdinalIgnoreCase)
                          && !varFlag.Contains("NOT_AVAILABLE", StringComparison.OrdinalIgnoreCase);

                result.Add(new GaiaCandidate
                {
                    GaiaId           = GetS(c, iSid),
                    Source           = "Gaia",
                    Ra               = ra.Value,
                    Dec              = dec.Value,
                    GMag             = ParseD(c, iGmag),
                    BpRp             = ParseD(c, iBpRp),
                    Ruwe             = ParseD(c, iRuwe),
                    FluxOverError    = ParseD(c, iFoe),
                    IsVariable       = isVar,
                    PmRa             = ParseD(c, iPmra),
                    PmDec            = ParseD(c, iPmdc),
                    RefEpoch         = ParseD(c, iEpch),
                    IpdFracMultiPeak = ParseD(c, iFmp),
                    IpdGofHarmonic   = ParseD(c, iGof),
                    DuplicatedSource = ParseI(c, iNss) != 0 || ParseI(c, iDup) != 0,
                });
            }
            catch { /* skip malformed rows */ }
        }
        return result;
    }

    // ── Utility ───────────────────────────────────────────────────────────────

    private static double SepArcsec(double ra1, double dec1, double ra2, double dec2)
    {
        double dra  = (ra2  - ra1)  * Math.PI / 180.0;
        double ddec = (dec2 - dec1) * Math.PI / 180.0;
        double a    = Math.Sin(ddec / 2) * Math.Sin(ddec / 2)
                    + Math.Cos(dec1 * Math.PI / 180) * Math.Cos(dec2 * Math.PI / 180)
                    * Math.Sin(dra  / 2) * Math.Sin(dra  / 2);
        return 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a)) * 180.0 / Math.PI * 3600.0;
    }

    private static (double Ra, double Dec) ApplyPm(
        double ra, double dec,
        double? pmra, double? pmdec, double? refEpoch, double obsEpoch)
    {
        if (pmra is null || pmdec is null || refEpoch is null) return (ra, dec);
        double dt     = obsEpoch - refEpoch.Value;
        double cosDec = Math.Cos(dec * Math.PI / 180.0);
        double dra    = cosDec > 0 ? pmra.Value  * dt / (3_600_000.0 * cosDec) : 0.0;
        double ddec   = pmdec.Value * dt / 3_600_000.0;
        return (ra + dra, dec + ddec);
    }

    private static double DateObsToEpoch(string dateObs)
    {
        if (!DateTime.TryParse(dateObs, null,
                               System.Globalization.DateTimeStyles.AssumeUniversal |
                               System.Globalization.DateTimeStyles.AdjustToUniversal,
                               out var dt))
            return 2025.0;

        var y0 = new DateTime(dt.Year,     1, 1, 0, 0, 0, DateTimeKind.Utc);
        var y1 = new DateTime(dt.Year + 1, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return dt.Year + (dt - y0).TotalDays / (y1 - y0).TotalDays;
    }

    private static bool TryParseAngle(string s, bool isDec, out double value)
    {
        value = 0;
        s = s.Trim();
        if (string.IsNullOrEmpty(s)) return false;

        if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out value))
            return true;

        var parts = s.Replace(':', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3) return false;

        if (!double.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var d0)) return false;
        if (!double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var d1)) return false;
        if (!double.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out var d2)) return false;

        double abs  = Math.Abs(d0) + d1 / 60.0 + d2 / 3600.0;
        double sign = s.TrimStart()[0] == '-' ? -1.0 : 1.0;
        value = isDec ? sign * abs : abs * 15.0;
        return true;
    }

    // ── CSV helpers ───────────────────────────────────────────────────────────

    private static int ColIdx(List<string> headers, string name)
    {
        for (int i = 0; i < headers.Count; i++)
            if (headers[i].Trim('"').Equals(name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static List<string> SplitCsv(string line)
    {
        var parts = new List<string>();
        var cur   = new StringBuilder();
        bool inQ  = false;
        foreach (char ch in line.TrimEnd('\r'))
        {
            if      (ch == '"')           inQ = !inQ;
            else if (ch == ',' && !inQ) { parts.Add(cur.ToString()); cur.Clear(); }
            else                          cur.Append(ch);
        }
        parts.Add(cur.ToString());
        return parts;
    }

    private static string GetS(List<string> cols, int idx)
        => idx >= 0 && idx < cols.Count ? cols[idx].Trim().Trim('"') : "";

    private static double? ParseD(List<string> cols, int idx)
    {
        var s = GetS(cols, idx);
        if (string.IsNullOrEmpty(s) || s == "--" ||
            s.Equals("null", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("nan",  StringComparison.OrdinalIgnoreCase) ||
            s.Equals("inf",  StringComparison.OrdinalIgnoreCase)) return null;
        return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    private static int ParseI(List<string> cols, int idx)
    {
        var s = GetS(cols, idx);
        return int.TryParse(s, out var i) ? i : 0;
    }

    // ── Verbose pipeline logger ───────────────────────────────────────────────
    /// Writes to both the session log and the caller-supplied logProgress channel.
    private static void Log(string msg, IProgress<string>? logProgress)
    {
        SessionLogService.Write(msg);
        logProgress?.Report(msg);
    }

    /// Short label for a candidate used in log lines.
    private static string CandLabel(GaiaCandidate c)
    {
        if (c.Source == "VSP" && c.Auid is not null) return $"VSP:{c.Auid}";
        if (c.GaiaId is not null)
        {
            var id = c.GaiaId.Length > 10 ? c.GaiaId[^10..] : c.GaiaId;
            return $"Gaia:…{id}";
        }
        return "?";
    }
}
