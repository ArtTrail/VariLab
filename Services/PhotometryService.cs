using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VariLab.Services;

/// <summary>
/// Multi-frame differential photometry: given a target sky position and a Stone-method
/// comparison-star ensemble, extracts a calibrated light curve from a folder of
/// pre-calibrated, plate-solved FITS frames.
///
/// Pipeline (per exotic-proto's PHOTOMETRY_ROADMAP.md section 2):
///   1. Fix one aperture/annulus size from the reference (first) frame's measured FWHM —
///      a size that adapts per-frame would inject spurious flux changes into the light curve.
///   2. Per frame: WCS-project target + each comp star to pixel, fixed-aperture photometry.
///   3. Per comp: target_mag_i = comp_mag_i − 2.5·log10(target_flux / comp_flux_i).
///   4. Per-comp bias removal: each comp's catalog magnitude may come from a different
///      source (GSPC/APASS/G→V/VSP) with its own small systematic offset relative to the
///      others. That offset is constant across the run, not per-frame noise, so it is
///      estimated once (each comp's median implied-target-magnitude vs. the ensemble grand
///      median) and subtracted before combining — otherwise it inflates the reported error.
///   5. Ensemble-combine bias-corrected estimates via weighted median (weight = comp SNR).
///   6. Sigma-clip outlier frames (3σ, 2 passes).
/// </summary>
public static class PhotometryService
{
    // Real plate-solve drift for this class of data is well under this. RefineCentroid is a
    // flux-weighted centroid over its whole search window, so if a bright neighbor's PSF falls
    // inside that window — confirmed in a dense cluster field, where a Gaia source ~2.2 mag
    // brighter only 4.4" from the target pulled the centroid onto itself in ~65% of frames,
    // producing a bogus, ~1.6 mag-brighter light curve — the far more luminous neighbor can
    // dominate the weighted sum and drag the centroid most of the way onto it. A refined
    // position that's moved further than this from its WCS-projected starting point is far
    // more likely sitting on a different star than legitimately refined, so it's rejected
    // rather than trusted.
    private const double MaxCentroidDriftPx = 6.0;

    public record CompStarRef(double Ra, double Dec, double? CatalogMag, string Label);

    public record FramePoint(
        string  FileName,
        double  Jd,
        double? Airmass,
        double  TargetMag,
        double  TargetMagErr,
        double  TargetFlux,
        double  TargetSnr,
        int     NCompsUsed,
        bool    Rejected,
        string? RejectReason,
        string? PierSide = null,
        bool    ExcludedFromTransit = false);

    public record PhotometryResult(
        List<FramePoint> Points,
        string            StatusMessage,
        double            ApertureRadiusPx,
        double            AnnulusInnerPx,
        double            AnnulusOuterPx,
        IReadOnlyDictionary<string, List<PerCompPoint>>? PerCompSeries = null,
        List<PerCompPoint>? TargetSeries = null);

    /// <summary>One star's own per-frame diagnostic snapshot — for a comp star, the same
    /// bias-corrected magnitude estimate that feeds the weighted-median combination in Pass 2
    /// (kept instead of discarded); for the target, the same values as the main light curve
    /// point for that frame. Flux/PeakAdu/Background come for free from the aperture-photometry
    /// call already being made; FwhmX/FwhmY/Fwhm are an extra per-frame moment measurement
    /// (PsfService.Measure, a cheap fixed-window calculation, not an iterative fit) taken only
    /// for this diagnostic — none of the main pipeline (aperture sizing, target magnitude,
    /// ensemble combination) reads them. FwhmX/FwhmY (not just the isotropic mean) matter
    /// specifically because differential chromatic refraction elongates a PSF along one
    /// direction rather than broadening it symmetrically — a directional effect an averaged
    /// FWHM would wash out. Always exported (see ResultsViewModel) — this is what's used when
    /// investigating a suspected systematic, e.g. whether a light curve's drift tracks a
    /// position-dependent PSF elongation rather than a real magnitude change.</summary>
    public record PerCompPoint(
        double  Jd, double? Airmass, double Mag, double Weight,
        double  Flux, double PeakAdu, double Background,
        double? FwhmX, double? FwhmY, double? Fwhm);

    /// <summary>One comp's raw magnitude estimate for one frame, before bias correction.</summary>
    private record CompEstimate(
        int CompIndex, double Mag, double Weight,
        double Flux, double PeakAdu, double Background,
        double? FwhmX, double? FwhmY, double? Fwhm);

    /// <summary>Raw per-frame photometry, before ensemble bias correction/combination.</summary>
    private record RawFrame(
        string  FileName,
        double  Jd,
        double? Airmass,
        double  TargetFlux,
        double  TargetSnr,
        double  TargetPeakAdu,
        double  TargetBackground,
        double? TargetFwhmX,
        double? TargetFwhmY,
        double? TargetFwhm,
        List<CompEstimate>? Estimates,   // null if rejected
        string? RejectReason,
        string? PierSide = null);

    public static async Task<PhotometryResult> RunAsync(
        string                      inputDirectory,
        double                      targetRa,
        double                      targetDec,
        IReadOnlyList<CompStarRef>  comps,
        IProgress<string>?          progress = null,
        CancellationToken           ct       = default)
    {
        var files = FitsHeaderService.FindAllFits(inputDirectory);
        if (files.Length == 0)
            return new PhotometryResult([], "✗  No FITS files found in the input directory.", 0, 0, 0);
        if (comps.Count == 0)
            return new PhotometryResult([], "✗  No comparison stars — run Comp Stars first.", 0, 0, 0);

        // ── Fix one aperture/annulus size from the reference (first) frame ────
        double apertureRadiusPx = 8.0, annulusInnerPx = 14.0, annulusOuterPx = 22.0;
        progress?.Report("⟳  Measuring reference FWHM…");

        var refHdr = SafeReadHeader(files[0]);
        var refWcs = refHdr is null ? null : WcsService.ReadWcs(refHdr);
        var refImg = await Task.Run(() => PsfService.ReadFitsPixels(files[0]), ct);

        if (refHdr != null && refWcs != null && refImg != null)
        {
            var tpx = WcsService.SkyToPixel(refWcs, targetRa, targetDec);
            if (tpx is not null)
            {
                double sat  = PsfService.EstimateSaturation(refHdr);
                double gain = PsfService.GetGain(refHdr);
                // Refine before measuring: Measure() does a single-shot moment calculation
                // around the given position with no re-centering search of its own, so if
                // this reference frame's own WCS solution is off by even a couple of pixels
                // (exactly the failure mode this whole fix targets), the FWHM it computes —
                // and therefore the aperture size used for the *entire* run — inherits that
                // error. A fixed, generous 8px search here is safe (no aperture size is known
                // yet to derive it from) since real plate-solve drift for this class of data
                // has been observed well under that.
                var (rcx, rcy) = PsfService.RefineCentroid(refImg, tpx.Value.X, tpx.Value.Y, 8.0);
                bool driftedOntoWrongStar = Distance(tpx.Value.X, tpx.Value.Y, rcx, rcy) > MaxCentroidDriftPx;
                var psf = driftedOntoWrongStar
                    ? null
                    : PsfService.Measure(refImg, (int)Math.Round(rcx), (int)Math.Round(rcy), sat, gain);
                if (psf is { Success: true, FwhmMean: > 0 })
                {
                    apertureRadiusPx = Math.Clamp(1.7 * psf.FwhmMean, 3.0, 30.0);
                    annulusInnerPx   = Math.Clamp(3.0 * psf.FwhmMean, apertureRadiusPx + 2, 45.0);
                    annulusOuterPx   = Math.Clamp(5.0 * psf.FwhmMean, annulusInnerPx + 4, 70.0);
                }
            }
        }

        progress?.Report($"Aperture: {apertureRadiusPx:F1}px  |  Annulus: {annulusInnerPx:F1}-{annulusOuterPx:F1}px  |  {files.Length} frames");
        SessionLogService.Write($"[Photometry] Reference frame: {Path.GetFileName(files[0])}  |  Aperture: {apertureRadiusPx:F2}px  |  Annulus: {annulusInnerPx:F2}-{annulusOuterPx:F2}px");

        // ── Pass 1: raw per-comp estimates for every frame ─────────────────────
        var raw = new List<RawFrame>(files.Length);
        for (int i = 0; i < files.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"⟳  Frame {i + 1}/{files.Length}: {Path.GetFileName(files[i])}");

            var frame = await Task.Run(() => ProcessFrameRaw(
                files[i], targetRa, targetDec, comps,
                apertureRadiusPx, annulusInnerPx, annulusOuterPx), ct);
            raw.Add(frame);
        }

        // ── Determine each comp's fixed catalog-calibration bias ──────────────
        // A comp's magnitude source (GSPC/APASS/G→V/VSP) can disagree with the others by a
        // roughly constant amount across the whole run — that's a calibration offset, not
        // per-frame noise, so it's removed once here rather than left to inflate the MAD below.
        int minSamples = Math.Max(3, raw.Count(f => f.Estimates is not null) / 4);
        var perCompMags = new Dictionary<int, List<double>>();
        foreach (var frame in raw)
        {
            if (frame.Estimates is null) continue;
            foreach (var e in frame.Estimates)
            {
                if (!perCompMags.TryGetValue(e.CompIndex, out var list))
                    perCompMags[e.CompIndex] = list = [];
                list.Add(e.Mag);
            }
        }

        var compMedians = perCompMags
            .Where(kv => kv.Value.Count >= minSamples)
            .ToDictionary(kv => kv.Key, kv => Median(kv.Value));

        double grandMedian = compMedians.Count > 0 ? Median(compMedians.Values.ToList()) : 0.0;
        var compOffsets = compMedians.ToDictionary(kv => kv.Key, kv => kv.Value - grandMedian);

        // ── Pass 2: bias-correct and combine ───────────────────────────────────
        var points = new List<FramePoint>(raw.Count);
        var perCompSeries = new Dictionary<int, List<PerCompPoint>>();
        var targetSeries = new List<PerCompPoint>();
        foreach (var frame in raw)
        {
            if (frame.Estimates is null)
            {
                points.Add(Reject(frame.FileName, frame.RejectReason ?? "unknown"));
                continue;
            }

            var corrected = frame.Estimates
                .Select(e => (Mag: e.Mag - compOffsets.GetValueOrDefault(e.CompIndex, 0.0), e.Weight))
                .ToList();

            for (int ei = 0; ei < frame.Estimates.Count; ei++)
            {
                var est = frame.Estimates[ei];
                if (!perCompSeries.TryGetValue(est.CompIndex, out var series))
                    perCompSeries[est.CompIndex] = series = new List<PerCompPoint>();
                series.Add(new PerCompPoint(frame.Jd, frame.Airmass, corrected[ei].Mag, corrected[ei].Weight,
                    est.Flux, est.PeakAdu, est.Background, est.FwhmX, est.FwhmY, est.Fwhm));
            }

            double targetMag    = WeightedMedian(corrected);
            double targetMagErr = corrected.Count >= 2
                ? WeightedMad(corrected, targetMag)
                : 1.0857 / Math.Max(1.0, frame.TargetSnr);   // photon-limited fallback for a single comp

            points.Add(new FramePoint(frame.FileName, frame.Jd, frame.Airmass, targetMag, targetMagErr,
                                       frame.TargetFlux, frame.TargetSnr, corrected.Count, false, null, frame.PierSide));

            targetSeries.Add(new PerCompPoint(frame.Jd, frame.Airmass, targetMag, frame.TargetSnr,
                frame.TargetFlux, frame.TargetPeakAdu, frame.TargetBackground,
                frame.TargetFwhmX, frame.TargetFwhmY, frame.TargetFwhm));
        }

        SigmaClip(points, sigma: 3.0, passes: 2);

        int nOk = points.Count(p => !p.Rejected);
        string msg = $"✓  {nOk}/{points.Count} frames used" +
                     (points.Count - nOk > 0 ? $"  ({points.Count - nOk} rejected)" : "");

        var perCompByLabel = perCompSeries.ToDictionary(
            kv => comps[kv.Key].Label,
            kv => kv.Value);

        return new PhotometryResult(points, msg, apertureRadiusPx, annulusInnerPx, annulusOuterPx, perCompByLabel, targetSeries);
    }

    private static FitsHeaderService.FitsHeader? SafeReadHeader(string path)
    {
        try { return FitsHeaderService.Read(path); }
        catch { return null; }
    }

    private static RawFrame ProcessFrameRaw(
        string file, double targetRa, double targetDec,
        IReadOnlyList<CompStarRef> comps,
        double aperR, double annIn, double annOut)
    {
        string name = Path.GetFileName(file);

        var hdr = SafeReadHeader(file);
        if (hdr is null) return RawReject(name, "header read failed");

        var wcs = WcsService.ReadWcs(hdr);
        if (wcs is null) return RawReject(name, "no WCS / plate solution");

        var image = PsfService.ReadFitsPixels(file);
        if (image is null) return RawReject(name, "pixel read failed (compressed or unreadable)");

        double sat  = PsfService.EstimateSaturation(hdr);
        double gain = PsfService.GetGain(hdr);

        // Centroid search radius: generous enough to absorb a plate solve that's only
        // accurate to a few pixels (e.g. ASTAP on undersampled data), but capped so it
        // can't wander onto a different star — see PsfService.RefineCentroid.
        double searchR = Math.Max(5.0, 1.5 * aperR);

        var tpx = WcsService.SkyToPixel(wcs, targetRa, targetDec);
        if (tpx is null) return RawReject(name, "target projects outside tangent plane");
        var (tcx, tcy) = PsfService.RefineCentroid(image, tpx.Value.X, tpx.Value.Y, searchR);
        double targetDrift = Distance(tpx.Value.X, tpx.Value.Y, tcx, tcy);
        if (targetDrift > MaxCentroidDriftPx)
            return RawReject(name, $"target centroid drifted {targetDrift:F1}px from WCS position " +
                                    "(likely a nearby star, not a plate-solve error)");

        var targetPhot = PsfService.MeasureAperture(image, tcx, tcy, aperR, annIn, annOut, sat, gain);
        if (!targetPhot.Success) return RawReject(name, $"target: {targetPhot.Message}");

        // Diagnostic-only: a per-frame FWHM (X, Y, and mean) for the target, alongside the
        // aperture photometry already being done. PsfService.Measure is a fixed-window moment
        // calculation (not an iterative fit), cheap enough to run every frame — nothing in the
        // main pipeline reads these values, they only feed the diagnostic export.
        var targetPsf = PsfService.Measure(image, (int)Math.Round(tcx), (int)Math.Round(tcy), sat, gain);
        double? targetFwhmX = targetPsf is { Success: true, FwhmX: > 0 } ? targetPsf.FwhmX : null;
        double? targetFwhmY = targetPsf is { Success: true, FwhmY: > 0 } ? targetPsf.FwhmY : null;
        double? targetFwhm  = targetPsf is { Success: true, FwhmMean: > 0 } ? targetPsf.FwhmMean : null;

        var estimates = new List<CompEstimate>();
        for (int ci = 0; ci < comps.Count; ci++)
        {
            var comp = comps[ci];
            if (comp.CatalogMag is null) continue;
            var cpx = WcsService.SkyToPixel(wcs, comp.Ra, comp.Dec);
            if (cpx is null) continue;
            var (ccx, ccy) = PsfService.RefineCentroid(image, cpx.Value.X, cpx.Value.Y, searchR);
            if (Distance(cpx.Value.X, cpx.Value.Y, ccx, ccy) > MaxCentroidDriftPx) continue;

            var compPhot = PsfService.MeasureAperture(image, ccx, ccy, aperR, annIn, annOut, sat, gain);
            if (!compPhot.Success || compPhot.Saturated) continue;

            double magFromComp = comp.CatalogMag.Value - 2.5 * Math.Log10(targetPhot.Flux / compPhot.Flux);
            var compPsf = PsfService.Measure(image, (int)Math.Round(ccx), (int)Math.Round(ccy), sat, gain);
            double? compFwhmX = compPsf is { Success: true, FwhmX: > 0 } ? compPsf.FwhmX : null;
            double? compFwhmY = compPsf is { Success: true, FwhmY: > 0 } ? compPsf.FwhmY : null;
            double? compFwhm  = compPsf is { Success: true, FwhmMean: > 0 } ? compPsf.FwhmMean : null;
            estimates.Add(new CompEstimate(ci, magFromComp, Math.Max(1.0, compPhot.Snr),
                compPhot.Flux, compPhot.PeakAdu, compPhot.Background, compFwhmX, compFwhmY, compFwhm));
        }

        if (estimates.Count == 0) return RawReject(name, "no usable comparison stars in this frame");

        double  jd      = ToJulianDate(ParseDateObs(hdr.Get("DATE-OBS")));
        double? airmass = hdr.GetDouble("AIRMASS");
        string? pierSide = hdr.Get("PIERSIDE") is { Length: > 0 } ps ? ps : null;

        return new RawFrame(name, jd, airmass, targetPhot.Flux, targetPhot.Snr,
            targetPhot.PeakAdu, targetPhot.Background, targetFwhmX, targetFwhmY, targetFwhm,
            estimates, null, pierSide);
    }

    private static double Distance(double x1, double y1, double x2, double y2)
    {
        double dx = x2 - x1, dy = y2 - y1;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static RawFrame RawReject(string name, string reason) =>
        new(name, 0, null, 0, 0, 0, 0, null, null, null, null, reason);

    private static FramePoint Reject(string name, string reason) =>
        new(name, 0, null, 0, 0, 0, 0, 0, true, reason);

    private static DateTime ParseDateObs(string dateObs)
    {
        if (DateTime.TryParse(dateObs, null,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
            return dt;
        return DateTime.UtcNow;
    }

    /// <summary>UTC DateTime → Julian Date, via the OLE Automation date epoch (JD 2415018.5).</summary>
    private static double ToJulianDate(DateTime utc) => utc.ToOADate() + 2415018.5;

    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2.0 : sorted[mid];
    }

    private static double WeightedMedian(List<(double Mag, double Weight)> items)
    {
        var sorted = items.OrderBy(i => i.Mag).ToList();
        double total = sorted.Sum(i => i.Weight);
        double cum = 0;
        foreach (var (mag, weight) in sorted)
        {
            cum += weight;
            if (cum >= total / 2.0) return mag;
        }
        return sorted[^1].Mag;
    }

    private static double WeightedMad(List<(double Mag, double Weight)> items, double center)
    {
        var devs = items.Select(i => (Dev: Math.Abs(i.Mag - center), i.Weight)).ToList();
        double mad = WeightedMedian(devs.Select(d => (d.Dev, d.Weight)).ToList());
        return Math.Max(mad * 1.4826, 0.001);
    }

    /// <summary>Sigma-clip outlier frames in-place by marking Rejected; 2 passes, 3σ.</summary>
    private static void SigmaClip(List<FramePoint> points, double sigma, int passes)
    {
        for (int pass = 0; pass < passes; pass++)
        {
            var active = points.Where(p => !p.Rejected).ToList();
            if (active.Count < 4) break;

            double mean = active.Average(p => p.TargetMag);
            double sd   = Math.Sqrt(active.Average(p => (p.TargetMag - mean) * (p.TargetMag - mean)));
            if (sd <= 0) break;

            bool anyClipped = false;
            for (int i = 0; i < points.Count; i++)
            {
                if (points[i].Rejected) continue;
                if (Math.Abs(points[i].TargetMag - mean) > sigma * sd)
                {
                    points[i] = points[i] with { Rejected = true, RejectReason = $"{sigma:F0}σ outlier" };
                    anyClipped = true;
                }
            }
            if (!anyClipped) break;
        }
    }
}
