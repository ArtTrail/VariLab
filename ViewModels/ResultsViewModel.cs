using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using VariLab.Models;
using VariLab.Services;

namespace VariLab.ViewModels;

/// <summary>
/// Results tab — Lomb-Scargle period search over the Photometry tab's light curve
/// (used only to find a fold period and a rough significance indicator — the
/// periodogram itself isn't shown; VSX's archive-scale, multi-observer analysis is
/// what actually determines long-period variability, not a single VariLab session),
/// phase folding at the best period, and export to AAVSO Extended Format, a Stellar
/// Variability PNG, and an Excel report — all three saved automatically as soon as
/// the period search completes, no export button required.
/// </summary>
public partial class ResultsViewModel : ViewModelBase
{
    private readonly DataViewModel       _data;
    private readonly CompStarsViewModel  _compStars;
    private readonly PhotometryViewModel _photometry;

    public ResultsViewModel(DataViewModel data, CompStarsViewModel compStars, PhotometryViewModel photometry)
    {
        _data       = data;
        _compStars  = compStars;
        _photometry = photometry;
    }

    [ObservableProperty] private string  _status         = "Run Photometry first.";
    [ObservableProperty] private double  _minPeriodDays   = 0.02;
    [ObservableProperty] private double  _maxPeriodDays   = 0.25;
    [ObservableProperty] private double  _bestPeriod;
    [ObservableProperty] private double  _bestPower;
    [ObservableProperty] private double  _foldPeriod = 0.10;
    [ObservableProperty] private string  _summary         = "";
    [ObservableProperty] private IReadOnlyList<PlotPoint>? _lightCurvePoints;
    [ObservableProperty] private IReadOnlyList<PlotPoint>? _phaseFoldedPoints;

    /// <summary>Path of the most recently auto-saved Stellar Variability PNG, if any —
    /// used to enable the "View PNG" button.</summary>
    [ObservableProperty] private string? _lastVariabilityPngPath;

    private double[] _jd = [];
    private double[] _mag = [];
    private double[] _magErr = [];
    private double[] _airmass = [];
    private double? _lastAutoMaxPeriod;
    private double _epoch;

    /// <summary>Set by the view's code-behind to open an image file in an in-app viewer window.</summary>
    public Func<string, Task>? ViewImageFunc { get; set; }

    private static string Timestamp() => DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");

    /// <summary>Finds the next unused "VariLab_ResultsN" subfolder under baseDir (VariLab_Results1,
    /// VariLab_Results2, ...) and creates it, so each export run gets its own folder instead of
    /// piling loose files directly into the target directory.</summary>
    private static string GetNextResultsDir(string baseDir)
    {
        int n = 1;
        if (Directory.Exists(baseDir))
        {
            var existing = Directory.GetDirectories(baseDir)
                .Select(Path.GetFileName)
                .Select(fname => Regex.Match(fname ?? "", @"^VariLab_Results(\d+)$"))
                .Where(m => m.Success)
                .Select(m => int.Parse(m.Groups[1].Value))
                .ToList();
            if (existing.Count > 0)
                n = existing.Max() + 1;
        }

        var resultsDir = Path.Combine(baseDir, $"VariLab_Results{n}");
        Directory.CreateDirectory(resultsDir);
        return resultsDir;
    }

    /// <summary>Builds a "N comp star ensemble (Gaia + APASS + VSP)" label for the PNG header,
    /// naming only the catalogs actually used as a magnitude source for at least one comp —
    /// GSPC and G→V are both Gaia-derived, so either one counts as "Gaia".</summary>
    private static string BuildCompSourceLabel(IReadOnlyList<GaiaCompService.CompStarInfo> comps)
    {
        var sources = new List<string>();
        if (comps.Any(c => c.MagSource is "GSPC" or "G→V")) sources.Add("Gaia");
        if (comps.Any(c => c.MagSource == "APASS"))          sources.Add("APASS");
        if (comps.Any(c => c.MagSource == "VSP"))            sources.Add("VSP");
        var srcText = sources.Count > 0 ? string.Join(" + ", sources) : "—";
        return $"{comps.Count} comp star ensemble ({srcText})";
    }

    [RelayCommand]
    private void Run()
    {
        var used = _photometry.Points.Where(p => !p.Rejected && !p.ExcludedFromTransit).OrderBy(p => p.Jd).ToList();
        if (used.Count < 4)
        {
            Status = "Need at least 4 accepted frames — run Photometry first.";
            return;
        }

        _jd      = used.Select(p => p.Jd).ToArray();
        _mag     = used.Select(p => p.TargetMag).ToArray();
        _magErr  = used.Select(p => p.TargetMagErr).ToArray();
        _airmass = used.Select(p => p.Airmass ?? double.NaN).ToArray();
        _epoch   = _jd[0];

        LightCurvePoints = _jd.Select((j, i) => new PlotPoint(j - _epoch, _mag[i])).ToList();

        // Auto-suggest Max period from the measured baseline (need ≥2 cycles to resolve a
        // period reliably, so cap at baseline/2) — unless the user has since typed their own
        // value, in which case leave it alone.
        double baseline = _jd[^1] - _jd[0];
        bool autoSuggested = _lastAutoMaxPeriod is null || Math.Abs(MaxPeriodDays - _lastAutoMaxPeriod.Value) < 1e-9;
        if (autoSuggested)
        {
            MaxPeriodDays = Math.Max(baseline / 2.0, MinPeriodDays * 2.0);
            _lastAutoMaxPeriod = MaxPeriodDays;
        }

        Status = "⟳  Running period search…";
        var result = PeriodSearchService.Compute(_jd, _mag, _magErr, MinPeriodDays, MaxPeriodDays);

        if (result.Periodogram.Count == 0)
        {
            Status = "✗  Period search failed — check the min/max period range.";
            return;
        }

        BestPeriod = result.BestPeriod;
        BestPower  = result.BestPower;
        FoldPeriod = result.BestPeriod;

        Fold();

        double mean = _mag.Average();
        double min  = _mag.Min();
        double max  = _mag.Max();
        Summary = $"Mean mag: {mean:F3}   |   Amplitude: {max - min:F3}   |   " +
                  $"N points: {used.Count}   |   Baseline: {baseline:F2} d   |   " +
                  $"Best period: {BestPeriod:F5} d (power {BestPower:F3})   |   " +
                  $"Max period: {MaxPeriodDays:F3} d{(autoSuggested ? " (auto-suggested from baseline)" : "")}";

        AutoExportAll();
    }

    [RelayCommand]
    private void Fold()
    {
        if (_jd.Length == 0 || FoldPeriod <= 0) return;
        PhaseFoldedPoints = PeriodSearchService.PhaseFold(_jd, _mag, FoldPeriod, _epoch);
    }

    /// <summary>Writes the AAVSO Extended Format, Stellar Variability PNG, and Excel report
    /// into a numbered "VariLab_ResultsN" subfolder of the Data tab's Output Directory (falls
    /// back to the Target Directory if Output Directory is somehow blank) — no save
    /// dialogs. Called automatically every time the period search runs (whether
    /// auto-triggered after Photometry or re-run manually after changing period settings),
    /// so exports always reflect the current light curve/fold. Each run gets the next
    /// available VariLab_ResultsN folder (VariLab_Results1, VariLab_Results2, ...), so separate
    /// runs never mix their output together. All three files from one run share a single
    /// millisecond-precision timestamp computed once (not one call to Timestamp() per file), so
    /// they can't drift apart or collide with another run's files even if two runs land in the
    /// same second. If any file fails to write, the partially-written VariLab_ResultsN folder is
    /// deleted rather than left behind as a misleading mix of fresh and missing/stale files.</summary>
    private void AutoExportAll()
    {
        if (_jd.Length == 0) return;

        var baseDir = string.IsNullOrWhiteSpace(_data.OutputDirectory)
            ? (string.IsNullOrWhiteSpace(_data.InputDirectory) ? "." : _data.InputDirectory)
            : _data.OutputDirectory;
        var name    = string.IsNullOrWhiteSpace(_data.TargetName) ? "target" : _data.TargetName;
        var previousPngPath = LastVariabilityPngPath;

        string? dir = null;
        try
        {
            Directory.CreateDirectory(baseDir);
            dir = GetNextResultsDir(baseDir);
            var ts = Timestamp();

            var rows = new List<AavsoExportService.ExportRow>(_jd.Length);
            var excelRows = new List<ExcelExportService.ExportRow>(_jd.Length);
            for (int i = 0; i < _jd.Length; i++)
            {
                double? am = double.IsNaN(_airmass[i]) ? null : _airmass[i];
                rows.Add(new AavsoExportService.ExportRow(_jd[i], _mag[i], _magErr[i], am));
                excelRows.Add(new ExcelExportService.ExportRow(_jd[i], _mag[i], _magErr[i], am));
            }

            // AAVSO Extended Format
            var compNotes = AavsoExportService.BuildCompNotes(_compStars.Stars.ToList());
            var flipNote  = AavsoExportService.BuildFlipNote(_photometry.Points);
            var notes = string.Join("; ", new[] { compNotes, flipNote }.Where(s => !string.IsNullOrWhiteSpace(s)));
            var aavsoText = AavsoExportService.Build(
                _data.TargetName, _data.AavsoObserverCode, _data.FilterCode, $"VariLab v{VariLab.AppVersion.Version}",
                rows, notes);
            var aavsoPath = Path.Combine(dir, $"{name}_aavso_{ts}.txt");
            File.WriteAllText(aavsoPath, aavsoText);

            // Stellar Variability PNG
            var points = _jd.Select((j, i) => new PlotPoint(j, _mag[i])).ToList();
            var title  = $"{name} (Label: VariLab)";
            var yLabel = $"{_data.FilterCode}mag";
            double magMean = _mag.Average();
            double magMin  = _mag.Min();
            double magMax  = _mag.Max();
            var subtitle = $"{BuildCompSourceLabel(_compStars.Stars)}  |  Filter: {_data.FilterCode}\n" +
                            $"Mean mag: {magMean:F3}  |  Amplitude: {magMax - magMin:F3}  |  Range: {magMin:F3}–{magMax:F3}";
            var pngPath = Path.Combine(dir, $"{name}_Stellar_Variability_{ts}.png");
            PlotExportService.ExportStellarVariabilityPng(pngPath, points, _magErr, title, "Time [JD]", yLabel, subtitle: subtitle);
            LastVariabilityPngPath = pngPath;

            // Excel report
            var plots = new List<ExcelExportService.PlotImage>
            {
                new("Light Curve",
                    PlotExportService.RenderPngBytes(points, _magErr, title, "Time [JD]", yLabel),
                    900, 600),
                new("Phase-Folded Light Curve",
                    PlotExportService.RenderPngBytes(PhaseFoldedPoints ?? [], null, $"Phase-Folded (P={FoldPeriod:F5} d)", "Phase", yLabel),
                    900, 600),
            };
            var excelPath = Path.Combine(dir, $"{name}_Report_{ts}.xlsx");
            ExcelExportService.Export(excelPath, _data.FilterCode, excelRows, plots,
                _compStars.Stars.ToList(), _compStars.RejectedStars.ToList());

            int nCompFiles = 0;
            if (_photometry.PerCompSeries is { Count: > 0 } perComp)
            {
                nCompFiles = ExportPerCompDiagnosticFiles(dir, perComp, _photometry.TargetSeries);
            }

            Status = $"✓  Saved to {dir}:  {Path.GetFileName(aavsoPath)}, {Path.GetFileName(pngPath)}, {Path.GetFileName(excelPath)}" +
                     (nCompFiles > 0 ? $", CompDiagnostics/ ({nCompFiles} stars)" : "");
        }
        catch (Exception ex)
        {
            Status = $"✗  Auto-export failed: {ex.Message}";
            SessionLogService.Write($"[Results] Auto-export failed: {ex}");

            // Don't leave a partial export behind — e.g. a correct AAVSO .txt sitting next to
            // a missing or stale PNG/Excel file, which is exactly the kind of silent
            // inconsistency that's easy to miss until it's mistaken for real data later.
            // This run's VariLab_ResultsN folder is unique to this call, so it's safe to remove
            // entirely; a subsequent run gets its own fresh VariLab_ResultsN+1 either way.
            LastVariabilityPngPath = previousPngPath;
            if (dir is not null)
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
                catch (Exception cleanupEx)
                {
                    SessionLogService.Write($"[Results] Cleanup of failed export folder {dir} also failed: {cleanupEx}");
                }
            }
        }
    }

    /// <summary>Writes one CSV plus one quick-look PNG per star (target first, then every
    /// comp) into a CompDiagnostics subfolder — the same bias-corrected magnitude, flux,
    /// peak ADU, sky background, and per-frame FWHM values already computed during the main
    /// photometry pass (FWHM aside, none of this costs anything extra — it's threaded through
    /// instead of discarded). Filtered to the same accepted frames (not statistically rejected,
    /// not transit-excluded) as the main light curve export, so every file lines up frame-for-
    /// frame with the AAVSO/PNG/Excel output and with each other. Returns the number of stars
    /// (target + comps) written.</summary>
    private int ExportPerCompDiagnosticFiles(
        string dir,
        IReadOnlyDictionary<string, List<PhotometryService.PerCompPoint>> perComp,
        List<PhotometryService.PerCompPoint>? targetSeries)
    {
        var acceptedJds = new HashSet<double>(_jd);
        var compDir = Path.Combine(dir, "CompDiagnostics");
        Directory.CreateDirectory(compDir);

        var all = new List<(string Label, List<PhotometryService.PerCompPoint> Series)>();
        if (targetSeries is { Count: > 0 }) all.Add(("Target", targetSeries));
        all.AddRange(perComp.Select(kv => (kv.Key, kv.Value)));

        string yLabel = $"{_data.FilterCode}mag";
        int n = 0;
        foreach (var (label, series) in all)
        {
            var accepted = series.Where(p => acceptedJds.Contains(p.Jd)).OrderBy(p => p.Jd).ToList();
            if (accepted.Count == 0) continue;
            string safeName = SanitizeFileName(label);

            var sb = new StringBuilder();
            sb.AppendLine("JD,Mag,Weight,Airmass,Flux,PeakADU,Background,FWHM_X,FWHM_Y,FWHM_Mean");
            foreach (var p in accepted)
            {
                string am    = p.Airmass.HasValue ? p.Airmass.Value.ToString("F2", CultureInfo.InvariantCulture) : "";
                string fwhmX = p.FwhmX.HasValue ? p.FwhmX.Value.ToString("F2", CultureInfo.InvariantCulture) : "";
                string fwhmY = p.FwhmY.HasValue ? p.FwhmY.Value.ToString("F2", CultureInfo.InvariantCulture) : "";
                string fwhm  = p.Fwhm.HasValue ? p.Fwhm.Value.ToString("F2", CultureInfo.InvariantCulture) : "";
                sb.AppendLine(string.Join(",",
                    p.Jd.ToString("F5", CultureInfo.InvariantCulture),
                    p.Mag.ToString("F4", CultureInfo.InvariantCulture),
                    p.Weight.ToString("F1", CultureInfo.InvariantCulture),
                    am,
                    p.Flux.ToString("F1", CultureInfo.InvariantCulture),
                    p.PeakAdu.ToString("F0", CultureInfo.InvariantCulture),
                    p.Background.ToString("F1", CultureInfo.InvariantCulture),
                    fwhmX, fwhmY, fwhm));
            }
            File.WriteAllText(Path.Combine(compDir, $"{safeName}.csv"), sb.ToString());

            var jd0   = accepted[0].Jd;
            var plotPoints = accepted.Select(p => new PlotPoint(p.Jd - jd0, p.Mag)).ToList();
            var yErrors    = accepted.Select(p => 1.0857 / Math.Max(1.0, p.Weight)).ToList();
            var title      = label == "Target" ? $"{_data.TargetName} — Target" : $"{_data.TargetName} — Comp {label}";
            PlotExportService.ExportStellarVariabilityPng(
                Path.Combine(compDir, $"{safeName}.png"), plotPoints, yErrors,
                title, $"Time [JD - {jd0:F2}]", yLabel);

            n++;
        }
        return n;
    }

    private static string SanitizeFileName(string label) => Regex.Replace(label, @"[^\w\-]+", "_").Trim('_');

    [RelayCommand]
    private async Task ViewPng()
    {
        if (LastVariabilityPngPath is null || ViewImageFunc is null) return;
        await ViewImageFunc(LastVariabilityPngPath);
    }

    /// <summary>Resets period-search results back to their pre-run state — called when the Data
    /// tab's input directory changes, so a new dataset doesn't show a stale light curve, period,
    /// or "View PNG" link left over from the previous one. Min/Max period range and Fold Period
    /// are left as-is (user-adjustable settings, not run output) — they self-correct on the next
    /// Run() regardless, via the existing auto-suggest-from-baseline logic.</summary>
    public void Clear()
    {
        Status                  = "Run Photometry first.";
        Summary                 = "";
        BestPeriod              = 0;
        BestPower               = 0;
        LightCurvePoints        = null;
        PhaseFoldedPoints       = null;
        LastVariabilityPngPath  = null;
        _jd      = [];
        _mag     = [];
        _magErr  = [];
        _airmass = [];
        _epoch   = 0;
    }
}
