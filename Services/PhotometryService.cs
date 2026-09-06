using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace VariLab.Services;

public enum PhotometryMode { Aperture, PsfFit }

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

    // A neighbor close enough to bias the target's own reference-frame FWHM measurement
    // without pulling the centroid past MaxCentroidDriftPx (so the guard above doesn't catch
    // it) can still blend into a fixed aperture sized from that inflated FWHM — confirmed on a
    // real case (KELT-8, a G=11.79 neighbor 9" away): target FWHM measured 11.0px vs. the comp
    // ensemble's clean 4.6-5.5px, and the resulting oversized aperture (sized from the target's
    // own inflated value) captured enough of the neighbor's flux to bias the light curve
    // ~0.17 mag bright. Comps are chosen to be isolated (Stone method's isolation check), so
    // their FWHM is a more trustworthy basis for aperture sizing than a single measurement
    // taken directly on the target — see the reference-frame sizing step below, which now
    // prefers the comp ensemble's median FWHM. This ratio is the threshold for a non-blocking
    // warning when the target's own FWHM still looks anomalously large relative to that
    // trustworthy comp baseline (case above was ~2.2x; comfortably below that to catch milder
    // contamination too, without flagging ordinary measurement noise).
    private const double FwhmMismatchWarningRatio = 1.5;

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
        List<PerCompPoint>? TargetSeries = null,
        string? NearbyStarWarning = null);

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
        PhotometryMode              mode       = PhotometryMode.Aperture,
        IProgress<string>?          progress   = null,
        CancellationToken           ct         = default)
    {
        var files = FitsHeaderService.FindAllFits(inputDirectory);
        if (files.Length == 0)
            return new PhotometryResult([], "✗  No FITS files found in the input directory.", 0, 0, 0);
        if (comps.Count == 0)
            return new PhotometryResult([], "✗  No comparison stars — run Comp Stars first.", 0, 0, 0);

        return mode == PhotometryMode.PsfFit
            ? await RunPsfFitAsync(inputDirectory, targetRa, targetDec, comps, files, progress, ct)
            : await RunApertureAsync(targetRa, targetDec, comps, files, progress, ct);
    }

    private static async Task<PhotometryResult> RunApertureAsync(
        double                      targetRa,
        double                      targetDec,
        IReadOnlyList<CompStarRef>  comps,
        string[]                    files,
        IProgress<string>?          progress,
        CancellationToken           ct)
    {
        // ── Fix one aperture/annulus size from the reference (first) frame ────
        // Sized from the comp ensemble's FWHM, not the target's own — see
        // FwhmMismatchWarningRatio above for why. The target's own reference-frame FWHM is
        // still measured, both as a fallback (if no comp is measurable at all) and to compare
        // against the comp baseline for the nearby-star warning below.
        double apertureRadiusPx = 8.0, annulusInnerPx = 14.0, annulusOuterPx = 22.0;
        string? nearbyStarWarning = null;
        progress?.Report("⟳  Measuring reference FWHM…");

        var refHdr = SafeReadHeader(files[0]);
        var refWcs = refHdr is null ? null : WcsService.ReadWcs(refHdr);
        var refImg = await Task.Run(() => PsfService.ReadFitsPixels(files[0]), ct);

        if (refHdr != null && refWcs != null && refImg != null)
        {
            double sat  = PsfService.EstimateSaturation(refHdr);
            double gain = PsfService.GetGain(refHdr);

            // Comp ensemble FWHM — comps are chosen to be isolated, clean point sources
            // (Stone method's isolation check), so their FWHM is a more trustworthy basis for
            // sizing the aperture than a single measurement taken directly on the target.
            var compFwhms = new List<double>();
            foreach (var comp in comps)
            {
                var cpx = WcsService.SkyToPixel(refWcs, comp.Ra, comp.Dec);
                if (cpx is null) continue;
                var (ccx, ccy) = PsfService.RefineCentroid(refImg, cpx.Value.X, cpx.Value.Y, 8.0);
                if (Distance(cpx.Value.X, cpx.Value.Y, ccx, ccy) > MaxCentroidDriftPx) continue;
                var cpsf = PsfService.Measure(refImg, (int)Math.Round(ccx), (int)Math.Round(ccy), sat, gain);
                if (cpsf is { Success: true, FwhmMean: > 0 }) compFwhms.Add(cpsf.FwhmMean);
            }
            double? compMedianFwhm = compFwhms.Count > 0 ? Median(compFwhms) : null;

            // Target's own reference-frame FWHM — refine before measuring: Measure() does a
            // single-shot moment calculation around the given position with no re-centering
            // search of its own, so if this reference frame's own WCS solution is off by even
            // a couple of pixels, the FWHM it computes inherits that error. A fixed, generous
            // 8px search here is safe (no aperture size is known yet to derive it from) since
            // real plate-solve drift for this class of data has been observed well under that.
            double? targetFwhm = null;
            var tpx = WcsService.SkyToPixel(refWcs, targetRa, targetDec);
            if (tpx is not null)
            {
                var (rcx, rcy) = PsfService.RefineCentroid(refImg, tpx.Value.X, tpx.Value.Y, 8.0);
                bool driftedOntoWrongStar = Distance(tpx.Value.X, tpx.Value.Y, rcx, rcy) > MaxCentroidDriftPx;
                var psf = driftedOntoWrongStar
                    ? null
                    : PsfService.Measure(refImg, (int)Math.Round(rcx), (int)Math.Round(rcy), sat, gain);
                if (psf is { Success: true, FwhmMean: > 0 }) targetFwhm = psf.FwhmMean;
            }

            // Comp median is the primary sizing basis; the target's own measurement is only a
            // fallback for the rare case no comp could be measured on this particular frame.
            double? sizingFwhm = compMedianFwhm ?? targetFwhm;
            if (sizingFwhm is > 0)
            {
                apertureRadiusPx = Math.Clamp(1.7 * sizingFwhm.Value, 3.0, 30.0);
                annulusInnerPx   = Math.Clamp(3.0 * sizingFwhm.Value, apertureRadiusPx + 2, 45.0);
                annulusOuterPx   = Math.Clamp(5.0 * sizingFwhm.Value, annulusInnerPx + 4, 70.0);
            }

            // Always logged (regardless of whether the warning threshold is met) so a run can be
            // diagnosed after the fact — in particular, whether targetFwhm came back null (e.g.
            // the reference frame's own centroid refinement decided it drifted onto the neighbor
            // rather than the target, per the 6px check above) is otherwise invisible.
            SessionLogService.Write(
                $"[Photometry] Ref-frame FWHM check — target: {(targetFwhm is > 0 ? $"{targetFwhm:F2}px" : "n/a")}" +
                $"  |  comp median: {(compMedianFwhm is > 0 ? $"{compMedianFwhm:F2}px" : "n/a")}" +
                $"  |  ratio: {(targetFwhm is > 0 && compMedianFwhm is > 0 ? $"{targetFwhm / compMedianFwhm:F2}" : "n/a")}");

            // Nearby-star flag: the target's own FWHM reading anomalously large relative to the
            // (trustworthy) comp baseline is the signature a close neighbor is blending into the
            // measurement — confirmed on the KELT-8 case cited above. Non-blocking — this is a
            // warning to go investigate, not a reason to stop the run.
            if (targetFwhm is > 0 && compMedianFwhm is > 0)
            {
                double ratio = targetFwhm.Value / compMedianFwhm.Value;
                if (ratio >= FwhmMismatchWarningRatio)
                {
                    nearbyStarWarning =
                        $"⚠ Target FWHM ({targetFwhm.Value:F1}px) is {ratio:F1}x the comp ensemble's " +
                        $"median ({compMedianFwhm.Value:F1}px) — possible nearby star blending into the " +
                        "aperture. Check the field for a close neighbor.";
                    SessionLogService.Write($"[Photometry] {nearbyStarWarning}");
                }
            }
        }

        progress?.Report($"Aperture: {apertureRadiusPx:F1}px  |  Annulus: {annulusInnerPx:F1}-{annulusOuterPx:F1}px  |  {files.Length} frames");
        SessionLogService.Write($"[Photometry] Reference frame: {Path.GetFileName(files[0])}  |  Aperture: {apertureRadiusPx:F2}px  |  Annulus: {annulusInnerPx:F2}-{annulusOuterPx:F2}px");

        // ── Pass 1: raw per-comp estimates for every frame ─────────────────────
        // Each frame is processed independently (aperture/annulus size is already fixed above,
        // and results are only combined afterward in CombineAndBuildResult), so this is safe to
        // run across multiple cores at once — previously a strict one-frame-at-a-time loop, which
        // left every core but one idle for the whole run regardless of machine size. Written into
        // a pre-sized array (not a List) since concurrent writers can't safely share one List.
        // Capped well below core count on purpose: each frame's dominant cost is a large
        // synchronous pixel read (a full-size frame decodes to several hundred MB of doubles),
        // not the aperture/centroid math afterward, so this is really I/O-bound work wearing a
        // CPU-bound loop's clothes. Measured directly on real data (450-frame run, 24 logical
        // cores): MaxDegreeOfParallelism = cores-1 (23) thrashed disk I/O badly enough that it
        // was both SLOWER (671.4s) than a cap of 4 (371.5s) and made cancellation far worse
        // (13-33s latency, since ProcessFrameRaw can't abort a read that's already started, so
        // cancelling has to wait for the slowest of however many concurrent reads are in
        // flight) — vs. 0.7-2.5s at a cap of 4, back in line with the old strictly-serial
        // loop's ~1.5s worst case. 4 wins on both throughput and responsiveness at once; don't
        // "optimize" this back up toward core count without re-measuring both on real data.
        var raw = new RawFrame[files.Length];
        int completedFrames = 0;
        var parallelOptions = new ParallelOptions
        {
            CancellationToken      = ct,
            MaxDegreeOfParallelism = Math.Min(4, Math.Max(1, Environment.ProcessorCount - 1)),
        };
        await Parallel.ForEachAsync(Enumerable.Range(0, files.Length), parallelOptions, async (i, frameCt) =>
        {
            var frame = await Task.Run(() => ProcessFrameRaw(
                files[i], targetRa, targetDec, comps,
                apertureRadiusPx, annulusInnerPx, annulusOuterPx, frameCt), frameCt);
            raw[i] = frame;
            int done = Interlocked.Increment(ref completedFrames);
            progress?.Report($"⟳  Frame {done}/{files.Length}: {Path.GetFileName(files[i])}");
        });

        return CombineAndBuildResult(raw.ToList(), comps, apertureRadiusPx, annulusInnerPx, annulusOuterPx, nearbyStarWarning);
    }

    /// <summary>Pass 2, shared by both Aperture and PSF-fit modes: per-comp catalog-calibration
    /// bias removal, SNR-weighted-median combination, and sigma-clipping. Operates purely on
    /// <see cref="RawFrame"/>/<see cref="CompEstimate"/> values, so it doesn't care which
    /// engine measured the raw flux — aperture summing and PSF fitting both feed it the same
    /// shape of data.</summary>
    private static PhotometryResult CombineAndBuildResult(
        List<RawFrame> raw, IReadOnlyList<CompStarRef> comps,
        double apertureRadiusPx, double annulusInnerPx, double annulusOuterPx,
        string? nearbyStarWarning)
    {
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
                     (points.Count - nOk > 0 ? $"  ({points.Count - nOk} rejected)" : "") +
                     (nearbyStarWarning is not null ? $"   {nearbyStarWarning}" : "");

        var perCompByLabel = perCompSeries.ToDictionary(
            kv => comps[kv.Key].Label,
            kv => kv.Value);

        return new PhotometryResult(points, msg, apertureRadiusPx, annulusInnerPx, annulusOuterPx, perCompByLabel, targetSeries, nearbyStarWarning);
    }

    /// <summary>PSF-fit mode's Pass 1: invokes the bundled Python/Photutils engine ONCE for
    /// the whole dataset (not per-frame — matching how EXOTIC itself is invoked once for a
    /// whole reduction), reads its output CSV back into the same RawFrame/CompEstimate shape
    /// RunApertureAsync builds directly, then hands off to the identical, unmodified
    /// CombineAndBuildResult Pass 2. Aperture/annulus fields don't apply in this mode — 0 here
    /// signals "not applicable" to callers (ApertureRadiusPx etc. on PhotometryResult).</summary>
    private static async Task<PhotometryResult> RunPsfFitAsync(
        string                      inputDirectory,
        double                      targetRa,
        double                      targetDec,
        IReadOnlyList<CompStarRef>  comps,
        string[]                    files,
        IProgress<string>?          progress,
        CancellationToken           ct)
    {
        progress?.Report("⟳  Checking PSF-fit engine…");
        var runtime = await PsfEngineRuntimeService.ResolveAsync(ct);
        if (runtime is null)
        {
            return new PhotometryResult([],
                "✗  PSF-fit engine isn't set up yet — run PSF Engine Setup first (Photometry tab).",
                0, 0, 0);
        }

        string jobPath = Path.Combine(Path.GetTempPath(), $"varilab_psf_job_{Guid.NewGuid():N}.json");
        string csvPath = Path.Combine(Path.GetTempPath(), $"varilab_psf_out_{Guid.NewGuid():N}.csv");

        var targetNode = new JsonObject { ["ra"] = targetRa, ["dec"] = targetDec, ["label"] = "Target" };
        var compsArray = new JsonArray();
        foreach (var c in comps)
            compsArray.Add(new JsonObject
            {
                ["ra"] = c.Ra, ["dec"] = c.Dec,
                ["mag"] = c.CatalogMag ?? 0.0, ["label"] = c.Label,
            });

        var job = new JsonObject
        {
            ["input_dir"]   = inputDirectory,
            ["output_csv"]  = csvPath,
            ["target"]      = targetNode,
            ["comps"]       = compsArray,
        };
        await File.WriteAllTextAsync(jobPath, job.ToJsonString(), ct);

        SessionLogService.Write($"[PsfFit] Job: {jobPath}  |  Output: {csvPath}  |  {files.Length} frames  |  {comps.Count} comps");
        progress?.Report($"⟳  Running PSF-fit engine on {files.Length} frames…");

        var psi = new ProcessStartInfo(runtime.PythonExePath)
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false,       CreateNoWindow        = true,
        };
        psi.ArgumentList.Add(runtime.ScriptPath);
        psi.ArgumentList.Add(jobPath);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Cannot start the PSF-fit engine process.");

        // WaitForExitAsync(ct) only stops *waiting* on cancellation — it does not touch the
        // child process itself, so without this the engine (and, once parallelized, its whole
        // worker pool) would keep running to completion in the background even after the user
        // cancels and the UI moves on, wasting CPU for however long was left and leaving the
        // process orphaned (Process.Dispose from `using` above doesn't kill it either).
        using var killOnCancel = ct.Register(() =>
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
            catch { /* already exited between the check and the kill — fine */ }
        });

        var stdoutTask = DrainEngineOutputAsync(proc.StandardOutput, progress, ct);
        var stderrTask = DrainEngineOutputAsync(proc.StandardError, null, ct);
        await Task.WhenAll(stdoutTask, stderrTask);
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0)
        {
            SessionLogService.Write($"[PsfFit] Engine exited with code {proc.ExitCode}.");
            return new PhotometryResult([],
                $"✗  PSF-fit engine exited with code {proc.ExitCode} — check the session log.", 0, 0, 0);
        }

        if (!File.Exists(csvPath))
            return new PhotometryResult([], "✗  PSF-fit engine finished but wrote no output.", 0, 0, 0);

        var raw = ParsePsfFitCsv(csvPath, comps);

        try { File.Delete(jobPath); } catch (Exception ex) { SessionLogService.Write($"[PsfFit] Could not delete temp job file '{jobPath}': {ex.Message}"); }
        try { File.Delete(csvPath); } catch (Exception ex) { SessionLogService.Write($"[PsfFit] Could not delete temp CSV '{csvPath}': {ex.Message}"); }

        return CombineAndBuildResult(raw, comps, 0, 0, 0, null);
    }

    private static async Task DrainEngineOutputAsync(StreamReader reader, IProgress<string>? progress, CancellationToken ct)
    {
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (line.Length == 0) continue;
            SessionLogService.Write($"[PsfFit] {line}");
            if (line.StartsWith("Frame ", StringComparison.Ordinal))
                progress?.Report($"⟳  {line}");
        }
    }

    /// <summary>Reads the PSF engine's output CSV (jd,airmass,fwhm_px,{Label}_flux,
    /// {Label}_flux_err,... — one Target + one pair per comp, in job order) into the same
    /// RawFrame/CompEstimate shape RunApertureAsync's ProcessFrameRaw builds, computing each
    /// comp's implied target magnitude with the identical formula
    /// (comp_mag - 2.5*log10(target_flux/comp_flux)) — the only difference is where the raw
    /// flux came from.</summary>
    private static List<RawFrame> ParsePsfFitCsv(string csvPath, IReadOnlyList<CompStarRef> comps)
    {
        var lines = File.ReadAllLines(csvPath);
        var raw = new List<RawFrame>(Math.Max(0, lines.Length - 1));
        if (lines.Length < 2) return raw;

        var header = lines[0].Split(',');
        int Col(string name) => Array.IndexOf(header, name);

        int jdCol = Col("jd"), airmassCol = Col("airmass"), fwhmCol = Col("fwhm_px");
        int targetFluxCol = Col("Target_flux"), targetErrCol = Col("Target_flux_err");

        for (int li = 1; li < lines.Length; li++)
        {
            var cells = lines[li].Split(',');
            string name = $"row{li}";

            double? jd = jdCol >= 0 && double.TryParse(cells[jdCol], NumberStyles.Float, CultureInfo.InvariantCulture, out var jdv) ? jdv : null;
            if (jd is null) { raw.Add(RawReject(name, "no JD in PSF-fit output row")); continue; }

            double? airmass = airmassCol >= 0 && double.TryParse(cells[airmassCol], NumberStyles.Float, CultureInfo.InvariantCulture, out var amv) ? amv : null;
            double? fwhm = fwhmCol >= 0 && double.TryParse(cells[fwhmCol], NumberStyles.Float, CultureInfo.InvariantCulture, out var fwv) ? fwv : null;

            double? targetFlux = targetFluxCol >= 0 && double.TryParse(cells[targetFluxCol], NumberStyles.Float, CultureInfo.InvariantCulture, out var tfv) ? tfv : null;
            double? targetErr  = targetErrCol >= 0 && double.TryParse(cells[targetErrCol], NumberStyles.Float, CultureInfo.InvariantCulture, out var tev) ? tev : null;
            if (targetFlux is null || targetFlux <= 0 || targetErr is null || targetErr <= 0)
            {
                raw.Add(RawReject(name, "target not measurable this frame (PSF-fit)"));
                continue;
            }
            double targetSnr = targetFlux.Value / targetErr.Value;

            var estimates = new List<CompEstimate>();
            for (int ci = 0; ci < comps.Count; ci++)
            {
                var comp = comps[ci];
                if (comp.CatalogMag is null) continue;
                int fluxCol = Col($"{comp.Label}_flux");
                int errCol  = Col($"{comp.Label}_flux_err");
                if (fluxCol < 0 || errCol < 0) continue;
                if (!double.TryParse(cells[fluxCol], NumberStyles.Float, CultureInfo.InvariantCulture, out var cflux) || cflux <= 0) continue;
                if (!double.TryParse(cells[errCol], NumberStyles.Float, CultureInfo.InvariantCulture, out var cerr) || cerr <= 0) continue;

                double magFromComp = comp.CatalogMag.Value - 2.5 * Math.Log10(targetFlux.Value / cflux);
                estimates.Add(new CompEstimate(ci, magFromComp, Math.Max(1.0, cflux / cerr),
                    cflux, 0.0, 0.0, null, null, null));
            }

            if (estimates.Count == 0) { raw.Add(RawReject(name, "no usable comparison stars in this frame (PSF-fit)")); continue; }

            raw.Add(new RawFrame(name, jd.Value, airmass, targetFlux.Value, targetSnr,
                0.0, 0.0, null, null, fwhm, estimates, null));
        }

        return raw;
    }

    private static FitsHeaderService.FitsHeader? SafeReadHeader(string path)
    {
        try { return FitsHeaderService.Read(path); }
        catch (Exception ex)
        {
            SessionLogService.Write($"[Photometry] Header read failed for '{Path.GetFileName(path)}': {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static RawFrame ProcessFrameRaw(
        string file, double targetRa, double targetDec,
        IReadOnlyList<CompStarRef> comps,
        double aperR, double annIn, double annOut,
        CancellationToken ct = default)
    {
        string name = Path.GetFileName(file);
        ct.ThrowIfCancellationRequested();

        var hdr = SafeReadHeader(file);
        if (hdr is null) return RawReject(name, "header read failed");

        var wcs = WcsService.ReadWcs(hdr);
        if (wcs is null) return RawReject(name, "no WCS / plate solution");

        // Cheap checks (header/WCS) are done — the pixel read below is the expensive part of
        // this frame (a full-size frame decodes to several hundred MB of doubles), so this is
        // the last point a queued-but-not-yet-really-started frame can bail out before paying
        // that cost. Once the read itself begins there's no further check inside it, so a
        // cancellation that lands mid-read still has to wait for this one frame to finish —
        // that's an accepted, much smaller latency than waiting on the comp loop too.
        ct.ThrowIfCancellationRequested();

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
            ct.ThrowIfCancellationRequested();
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
