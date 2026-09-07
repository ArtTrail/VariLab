using System;
using System.Collections.Generic;
using System.Linq;
using VariLab.Models;

namespace VariLab.Services;

/// <summary>
/// Generalized (floating-mean) Lomb-Scargle periodogram — Zechmeister &amp; Kürster (2009).
/// This is the same formulation astropy.timeseries.LombScargle uses by default
/// (fit_mean=True, normalization="standard"), implemented natively so VariLab has
/// no Python dependency for period search.
/// </summary>
public static class PeriodSearchService
{
    public record LombScargleResult(
        List<PlotPoint> Periodogram,   // X = period (days), Y = power (0..1)
        double           BestPeriod,
        double           BestPower);

    /// <summary>
    /// Compute the periodogram over a linear frequency grid spanning
    /// [1/maxPeriodDays, 1/minPeriodDays].
    /// </summary>
    public static LombScargleResult Compute(
        IReadOnlyList<double> jd,
        IReadOnlyList<double> mag,
        IReadOnlyList<double> magErr,
        double minPeriodDays,
        double maxPeriodDays,
        int    nSamples = 3000)
    {
        int n = jd.Count;
        if (n < 4 || minPeriodDays <= 0 || maxPeriodDays <= minPeriodDays)
            return new LombScargleResult([], 0, 0);

        // Normalized weights w_i = (1/σ_i²) / Σ(1/σ_j²)
        var w = new double[n];
        double wsum = 0;
        for (int i = 0; i < n; i++)
        {
            double err = magErr[i] > 0 ? magErr[i] : 0.01;
            w[i] = 1.0 / (err * err);
            wsum += w[i];
        }
        for (int i = 0; i < n; i++) w[i] /= wsum;

        // Time relative to the first observation, for numerical stability at high frequency.
        double t0 = jd[0];
        var t = new double[n];
        for (int i = 0; i < n; i++) t[i] = jd[i] - t0;

        double fMin = 1.0 / maxPeriodDays;
        double fMax = 1.0 / minPeriodDays;

        var periodogram = new List<PlotPoint>(nSamples);
        double bestPower = -1;
        double bestPeriod = minPeriodDays;

        var cos = new double[n];
        var sin = new double[n];

        for (int k = 0; k < nSamples; k++)
        {
            double frac  = nSamples == 1 ? 0.0 : (double)k / (nSamples - 1);
            double f     = fMin + frac * (fMax - fMin);
            double omega = 2.0 * Math.PI * f;

            double chat = 0, shat = 0, yhat = 0;
            for (int i = 0; i < n; i++)
            {
                cos[i] = Math.Cos(omega * t[i]);
                sin[i] = Math.Sin(omega * t[i]);
                chat  += w[i] * cos[i];
                shat  += w[i] * sin[i];
                yhat  += w[i] * mag[i];
            }

            double yy = 0, yc = 0, ys = 0, cc = 0, ss = 0, cs = 0;
            for (int i = 0; i < n; i++)
            {
                double yd = mag[i] - yhat;
                double cd = cos[i] - chat;
                double sd = sin[i] - shat;
                yy += w[i] * yd * yd;
                yc += w[i] * yd * cd;
                ys += w[i] * yd * sd;
                cc += w[i] * cd * cd;
                ss += w[i] * sd * sd;
                cs += w[i] * cd * sd;
            }

            double d     = cc * ss - cs * cs;
            double power = (yy > 0 && d > 1e-14)
                ? (ss * yc * yc + cc * ys * ys - 2.0 * cs * yc * ys) / (yy * d)
                : 0.0;
            power = Math.Clamp(power, 0.0, 1.0);

            double period = 1.0 / f;
            periodogram.Add(new PlotPoint(period, power));

            if (power > bestPower) { bestPower = power; bestPeriod = period; }
        }

        return new LombScargleResult(periodogram, bestPeriod, Math.Max(bestPower, 0));
    }

    /// <summary>Phase-fold at the given period, epoch = the first observation's JD.</summary>
    public static List<PlotPoint> PhaseFold(
        IReadOnlyList<double> jd, IReadOnlyList<double> mag, double period, double epoch)
    {
        var points = new List<PlotPoint>(jd.Count * 2);
        for (int i = 0; i < jd.Count; i++)
        {
            double phase = (jd[i] - epoch) / period;
            phase -= Math.Floor(phase);
            points.Add(new PlotPoint(phase, mag[i]));
            points.Add(new PlotPoint(phase + 1.0, mag[i]));   // repeat cycle for readability
        }
        return points.OrderBy(p => p.X).ToList();
    }

    /// <summary>Same fold as <see cref="PhaseFold"/>, but also returns per-point error and label
    /// (source filename) arrays aligned to the returned points — so the Results-tab hover tooltip
    /// (issue #27) can show mag ± uncertainty and the source frame on the folded plot too. Each
    /// input frame is duplicated (phase and phase+1) and the whole set sorted by phase; the meta
    /// arrays are carried through that duplication+sort so index i lines up across all three.</summary>
    public static (List<PlotPoint> Points, List<double> Errors, List<string> Labels) PhaseFoldWithMeta(
        IReadOnlyList<double> jd, IReadOnlyList<double> mag, IReadOnlyList<double> err,
        IReadOnlyList<string> labels, double period, double epoch)
    {
        var rows = new List<(PlotPoint P, double E, string L)>(jd.Count * 2);
        for (int i = 0; i < jd.Count; i++)
        {
            double phase = (jd[i] - epoch) / period;
            phase -= Math.Floor(phase);
            double e = i < err.Count ? err[i] : 0.0;
            string l = i < labels.Count ? labels[i] : "";
            rows.Add((new PlotPoint(phase, mag[i]), e, l));
            rows.Add((new PlotPoint(phase + 1.0, mag[i]), e, l));
        }
        rows.Sort((a, b) => a.P.X.CompareTo(b.P.X));
        return (rows.Select(r => r.P).ToList(),
                rows.Select(r => r.E).ToList(),
                rows.Select(r => r.L).ToList());
    }

    /// <summary>Per-frame residual from a binned phase-folded curve, in the same order as the
    /// input jd/mag arrays (unlike <see cref="PhaseFold"/>, which duplicates/reorders points
    /// for plotting). Bins the [0,1) phase range into <paramref name="nBins"/> and subtracts
    /// each bin's median magnitude from every point in it — a coarse, assumption-light stand-in
    /// for a real light-curve template fit, just enough to detrend real periodic variability
    /// before checking whether what's left still correlates with something else (seeing —
    /// see CrowdingFlagService). An empty bin's points pass through with a zero residual rather
    /// than being dropped, since a sparse fold shouldn't silently shrink the sample being
    /// correlated.</summary>
    public static double[] PhaseResiduals(
        IReadOnlyList<double> jd, IReadOnlyList<double> mag, double period, double epoch, int nBins = 25)
    {
        int n = jd.Count;
        var residuals = new double[n];
        if (n == 0 || period <= 0 || nBins < 1) return residuals;

        var phases  = new double[n];
        var binMags = new List<double>[nBins];
        for (int b = 0; b < nBins; b++) binMags[b] = [];

        for (int i = 0; i < n; i++)
        {
            double phase = (jd[i] - epoch) / period;
            phase -= Math.Floor(phase);
            phases[i] = phase;
            int bin = Math.Clamp((int)(phase * nBins), 0, nBins - 1);
            binMags[bin].Add(mag[i]);
        }

        var binMedian = new double[nBins];
        for (int b = 0; b < nBins; b++)
            binMedian[b] = binMags[b].Count > 0 ? Median(binMags[b]) : double.NaN;

        for (int i = 0; i < n; i++)
        {
            int bin = Math.Clamp((int)(phases[i] * nBins), 0, nBins - 1);
            residuals[i] = double.IsNaN(binMedian[bin]) ? 0.0 : mag[i] - binMedian[bin];
        }
        return residuals;
    }

    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2.0 : sorted[mid];
    }
}
