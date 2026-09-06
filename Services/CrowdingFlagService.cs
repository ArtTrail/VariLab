using System;
using System.Collections.Generic;
using System.Linq;

namespace VariLab.Services;

/// <summary>
/// FWHM-residual crowding regression: correlates a star's brightness residual — relative to
/// the comp ensemble's own shared trend, not raw — against per-frame FWHM. A significant
/// correlation means a star's brightness tracks the seeing disk in a way its peers don't share,
/// the signature of a blended neighbor bleeding in more as seeing worsens (blend fraction
/// scales with seeing-disk area). Catches contamination even when the light curve looks clean,
/// because it tests the mechanism directly instead of the symptom.
///
/// Correlating raw (non-relative) brightness against FWHM does NOT work: confirmed directly on
/// V1655 Cen, a known-isolated validation target, where every single comp flagged simultaneously
/// at r~0.4-0.41 — not real contamination, but ordinary fixed-aperture flux loss growing with
/// seeing, a universal effect every star shares regardless of blending. See
/// <see cref="ComputeCommonMode"/> for the fix: subtract that shared trend before correlating.
///
/// Source: a colleague-provided critique of a globular-cluster RR Lyrae observing program,
/// which calls this regression "not optional" and "the program's most important safeguard"
/// even in a difference-imaging pipeline — see USER_GUIDE.md Appendix B. Confirmed to matter
/// on real VariLab data: two close-neighbor targets (V1786 Cen, V1615 Cen) both showed
/// amplitude inflation traced to this exact mechanism.
/// </summary>
public static class CrowdingFlagService
{
    /// <summary>A flagged star: |r| significant at p &lt; <see cref="SignificanceP"/>. R and
    /// PValue are always reported (not just when flagged) so a borderline case can be judged
    /// by eye rather than only seeing a binary pass/fail.</summary>
    public record CrowdingFlagResult(string StarLabel, double R, double PValue, int N, string Message);

    private const double SignificanceP = 0.01;
    private const int    MinPoints     = 8;   // below this, a correlation is noise-dominated

    // A p-value alone isn't the right bar at the frame counts VariLab actually sees (a full
    // night is 200-300+ points): confirmed directly on V1655 Cen post-common-mode-correction —
    // 5 of 10 comps still reached p < 0.01 on residual |r| as low as 0.17, values that small
    // being "statistically significant" purely because n is large, not because the effect is
    // large enough to matter. Requiring |r| at least "moderate" (Cohen's conventional cutoff)
    // in addition to significance is what actually separates a real, meaningfully-sized
    // seeing-correlated bias from ordinary residual noise that a big-N dataset will always
    // find "significant" if you only look at p.
    private const double MinAbsR = 0.3;

    /// <summary>Pearson correlation coefficient between two equal-length series.</summary>
    public static double PearsonR(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        int n = x.Count;
        if (n == 0 || n != y.Count) return 0.0;

        double mx = x.Average(), my = y.Average();
        double sxy = 0, sxx = 0, syy = 0;
        for (int i = 0; i < n; i++)
        {
            double dx = x[i] - mx, dy = y[i] - my;
            sxy += dx * dy;
            sxx += dx * dx;
            syy += dy * dy;
        }
        if (sxx <= 0 || syy <= 0) return 0.0;
        return sxy / Math.Sqrt(sxx * syy);
    }

    /// <summary>Two-tailed p-value for a Pearson r on n points, via the standard
    /// t = r·sqrt((n-2)/(1-r²)) transform and the regularized incomplete beta function
    /// (Numerical Recipes' betai) — no external statistics library, so no new dependency.</summary>
    public static double PValueFromR(double r, int n)
    {
        if (n < 4) return 1.0;
        double rc = Math.Clamp(r, -0.9999999, 0.9999999);
        double df = n - 2;
        double t  = rc * Math.Sqrt(df / (1.0 - rc * rc));
        return IncompleteBeta(df / 2.0, 0.5, df / (df + t * t));
    }

    /// <summary>Per-frame common-mode brightness trend shared by the whole comp ensemble —
    /// confirmed on real data (V1655 Cen, a known-isolated validation target) to be dominated
    /// by ordinary fixed-aperture flux loss growing with seeing: a wider PSF puts more of a
    /// star's light outside a fixed aperture radius, a real, universal, and completely benign
    /// effect that hits every star equally regardless of blending. It shows up whether you look
    /// at a comp's own instrumental flux directly or at its ratio-derived implied target
    /// magnitude (r ~ 0.4 either way on that test case) — which is exactly why correlating raw
    /// brightness against FWHM has no power to discriminate a genuinely contaminated star from
    /// an ordinary one: everything correlates. Computed from each comp's own flux (not the
    /// PerCompPoint.Mag field, which is the *target's* magnitude as implied through that comp,
    /// not the comp's own independent brightness) — de-meaned per comp, then the per-frame
    /// median across comps isolates the shared component. Subtracting this from a star's own
    /// de-meaned series before correlating with FWHM is what actually isolates star-specific
    /// (crowding) signal from this universal one.</summary>
    private static Dictionary<double, double> ComputeCommonMode(
        IReadOnlyDictionary<string, List<PhotometryService.PerCompPoint>> perComp,
        IReadOnlySet<double> acceptedJds)
    {
        var perFrame = new Dictionary<double, List<double>>();
        foreach (var series in perComp.Values)
        {
            var pts = series.Where(p => acceptedJds.Contains(p.Jd) && p.Flux > 0).ToList();
            if (pts.Count < MinPoints) continue;

            double meanInstMag = pts.Average(p => -2.5 * Math.Log10(p.Flux));
            foreach (var p in pts)
            {
                double dev = -2.5 * Math.Log10(p.Flux) - meanInstMag;
                if (!perFrame.TryGetValue(p.Jd, out var list)) perFrame[p.Jd] = list = [];
                list.Add(dev);
            }
        }
        return perFrame.ToDictionary(kv => kv.Key, kv => Median(kv.Value));
    }

    /// <summary>Checks every comp star for FWHM-correlated brightness that the star does NOT
    /// share with the rest of the ensemble — i.e. deviates from <see cref="ComputeCommonMode"/>,
    /// not raw correlation with FWHM (which the whole ensemble shares from ordinary aperture
    /// flux loss and would otherwise always fire). Uses each comp's own flux-derived
    /// instrumental magnitude, not PerCompPoint.Mag (the target's implied magnitude via that
    /// comp — contaminated by the target's own flux, not a measure of the comp's own
    /// brightness). Only comps already isolation-checked (Stone method) are here, so a flagged
    /// one means that isolation held on the single reference frame but broke down as seeing
    /// changed across the run.</summary>
    public static List<CrowdingFlagResult> CheckComps(
        IReadOnlyDictionary<string, List<PhotometryService.PerCompPoint>> perComp,
        IReadOnlySet<double> acceptedJds)
    {
        var results = new List<CrowdingFlagResult>();
        var commonMode = ComputeCommonMode(perComp, acceptedJds);
        if (commonMode.Count < MinPoints) return results;

        foreach (var (label, series) in perComp)
        {
            var pts = series.Where(p => acceptedJds.Contains(p.Jd) && p.Fwhm is > 0 &&
                                         p.Flux > 0 && commonMode.ContainsKey(p.Jd)).ToList();
            if (pts.Count < MinPoints) continue;

            double meanInstMag = pts.Average(p => -2.5 * Math.Log10(p.Flux));
            var residuals = pts.Select(p => (-2.5 * Math.Log10(p.Flux) - meanInstMag) - commonMode[p.Jd]).ToList();
            var fwhms     = pts.Select(p => p.Fwhm!.Value).ToList();

            double r = PearsonR(fwhms, residuals);
            double pv = PValueFromR(r, pts.Count);
            if (pv < SignificanceP && Math.Abs(r) >= MinAbsR)
                results.Add(new CrowdingFlagResult(label, r, pv, pts.Count,
                    $"⚠ {label}: brightness diverges from the comp ensemble's shared trend in a way " +
                    $"that correlates with FWHM (r={r:F2}, p={pv:F4}, n={pts.Count}) — possible " +
                    "seeing-dependent blend contamination specific to this star, distinct from the " +
                    "universal seeing-vs-aperture-flux effect shared by the whole ensemble."));
        }
        return results;
    }

    /// <summary>Checks the target: unlike a comp, the target is expected to vary, so real
    /// periodic variability is detrended first via <see cref="PeriodSearchService.PhaseResiduals"/>.
    /// The target went through the same fixed-aperture photometry as the comps, so it shares the
    /// same universal seeing-vs-aperture-flux effect — that's also removed, using the comp
    /// ensemble's own <see cref="ComputeCommonMode"/> trend, before correlating what's left
    /// against FWHM. Skipped (empty result) if the period search didn't find a credible period —
    /// a residual computed from a noise "period" is meaningless.</summary>
    public static List<CrowdingFlagResult> CheckTarget(
        List<PhotometryService.PerCompPoint> targetSeries,
        IReadOnlyDictionary<string, List<PhotometryService.PerCompPoint>> perComp,
        IReadOnlySet<double> acceptedJds,
        double bestPeriod, double bestPower, double minPowerToTrust,
        double epoch)
    {
        var results = new List<CrowdingFlagResult>();
        if (bestPeriod <= 0 || bestPower < minPowerToTrust) return results;

        var pts = targetSeries.Where(p => acceptedJds.Contains(p.Jd) && p.Fwhm is > 0)
                               .OrderBy(p => p.Jd).ToList();
        if (pts.Count < MinPoints) return results;

        var jd    = pts.Select(p => p.Jd).ToList();
        var mag   = pts.Select(p => p.Mag).ToList();
        var fwhms = pts.Select(p => p.Fwhm!.Value).ToList();

        var phaseResiduals = PeriodSearchService.PhaseResiduals(jd, mag, bestPeriod, epoch);
        var commonMode = ComputeCommonMode(perComp, acceptedJds);

        var residuals = new List<double>();
        var fwhmsUsed = new List<double>();
        for (int i = 0; i < jd.Count; i++)
        {
            if (!commonMode.TryGetValue(jd[i], out var cm)) continue;
            residuals.Add(phaseResiduals[i] - cm);
            fwhmsUsed.Add(fwhms[i]);
        }
        if (residuals.Count < MinPoints) return results;

        double r = PearsonR(fwhmsUsed, residuals);
        double pv = PValueFromR(r, residuals.Count);
        if (pv < SignificanceP && Math.Abs(r) >= MinAbsR)
            results.Add(new CrowdingFlagResult("Target", r, pv, residuals.Count,
                $"⚠ Target: phase- and ensemble-detrended residual correlates with FWHM " +
                $"(r={r:F2}, p={pv:F4}, n={residuals.Count}) — possible seeing-dependent blend " +
                "contamination on the target itself, distinct from the universal seeing-vs-aperture-" +
                "flux effect already removed via the comp ensemble."));
        return results;
    }

    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2.0 : sorted[mid];
    }

    // ── Incomplete beta function (Numerical Recipes' betai), self-contained ────────────

    private static double LogGamma(double x)
    {
        double[] cof =
        [
            76.18009172947146, -86.50532032941677, 24.01409824083091,
            -1.231739572450155, 0.1208650973866179e-2, -0.5395239384953e-5,
        ];
        double y = x, tmp = x + 5.5;
        tmp -= (x + 0.5) * Math.Log(tmp);
        double ser = 1.000000000190015;
        for (int j = 0; j < 6; j++) { y += 1.0; ser += cof[j] / y; }
        return -tmp + Math.Log(2.5066282746310005 * ser / x);
    }

    private static double BetaCf(double a, double b, double x)
    {
        const int    maxIt = 200;
        const double eps   = 3e-9, fpMin = 1e-300;

        double qab = a + b, qap = a + 1.0, qam = a - 1.0;
        double c = 1.0, d = 1.0 - qab * x / qap;
        if (Math.Abs(d) < fpMin) d = fpMin;
        d = 1.0 / d;
        double h = d;

        for (int m = 1; m <= maxIt; m++)
        {
            int m2 = 2 * m;
            double aa = m * (b - m) * x / ((qam + m2) * (a + m2));
            d = 1.0 + aa * d; if (Math.Abs(d) < fpMin) d = fpMin;
            c = 1.0 + aa / c; if (Math.Abs(c) < fpMin) c = fpMin;
            d = 1.0 / d;
            h *= d * c;

            aa = -(a + m) * (qab + m) * x / ((a + m2) * (qap + m2));
            d = 1.0 + aa * d; if (Math.Abs(d) < fpMin) d = fpMin;
            c = 1.0 + aa / c; if (Math.Abs(c) < fpMin) c = fpMin;
            d = 1.0 / d;
            double del = d * c;
            h *= del;

            if (Math.Abs(del - 1.0) < eps) break;
        }
        return h;
    }

    private static double IncompleteBeta(double a, double b, double x)
    {
        if (x <= 0.0) return 0.0;
        if (x >= 1.0) return 1.0;

        double bt = Math.Exp(LogGamma(a + b) - LogGamma(a) - LogGamma(b) +
                              a * Math.Log(x) + b * Math.Log(1.0 - x));

        return x < (a + 1.0) / (a + b + 2.0)
            ? bt * BetaCf(a, b, x) / a
            : 1.0 - bt * BetaCf(b, a, 1.0 - x) / b;
    }
}
