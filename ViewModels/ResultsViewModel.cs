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

    /// <summary>FWHM-residual crowding regression results (CrowdingFlagService) — set after
    /// every run, empty if nothing was flagged. See USER_GUIDE.md Appendix B for the source
    /// and reasoning.</summary>
    [ObservableProperty] private string _crowdingFlagsSummary = "";

    private List<CrowdingFlagService.CrowdingFlagResult> _crowdingFlags = [];

    // No formal false-alarm-probability is computed for the period search (see
    // PeriodSearchService), so this is a heuristic confidence floor, not a statistical
    // threshold: below it, a "best period" is too likely to be noise for its phase-fold
    // residuals to mean anything, and the target crowding check is skipped rather than run
    // against a meaningless detrend.
    private const double MinPeriodPowerForCrowdingCheck = 0.3;

    private double[] _jd = [];
    private double[] _mag = [];
    private double[] _magErr = [];
    private double[] _airmass = [];
    private double? _lastAutoMaxPeriod;
    private double _epoch;

    /// <summary>Set by the view's code-behind to open an image file in an in-app viewer window.</summary>
    public Func<string, Task>? ViewImageFunc { get; set; }

    private static string Timestamp() => DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");

    /// <summary>Replaces characters that are invalid in a Windows file/folder name with "_" —
    /// needed once TargetName starts feeding into a directory name rather than just a filename
    /// prefix, since an unsanitized value (e.g. containing ":" or "/") would otherwise throw
    /// instead of producing a usable path.</summary>
    private static string SanitizeForPath(string s) =>
        string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    /// <summary>The observing night's calendar date (yyyyMMdd) for a given JD, using the common
    /// "UT date shifted back 12h" convention so a post-midnight-UTC frame still reports the
    /// evening date observers actually call that night — e.g. a frame at 2026-07-24 02:45 UTC
    /// reports as 20260723, matching how observers name the session, not the UTC calendar day.</summary>
    private static string ObsDateStamp(double jd) =>
        DateTime.UnixEpoch.AddDays(jd - 0.5 - 2440587.5).ToString("yyyyMMdd");

    /// <summary>Finds the next unused "VariLab_Results_{Target}_{ObsDate}_{Mode}_N" subfolder
    /// under baseDir and creates it, so each export run gets its own folder instead of piling
    /// loose files directly into the target directory. Target name, obs date, and photometry
    /// mode make the folder identifiable at a glance instead of requiring you to open it —
    /// the mode tag specifically exists so an Aperture run and a PSF Fit run on the same
    /// target/night never look like the same result (confirmed as a real point of confusion:
    /// without it, the two are indistinguishable except by opening files and comparing
    /// numbers). The trailing N still disambiguates repeat runs of the same target/night/mode
    /// combo.</summary>
    private static string GetNextResultsDir(string baseDir, string targetName, double firstJd, string modeTag)
    {
        var safeName = SanitizeForPath(targetName);
        var obsDate  = ObsDateStamp(firstJd);
        var prefix   = $"VariLab_Results_{safeName}_{obsDate}_{modeTag}_";

        int n = 1;
        if (Directory.Exists(baseDir))
        {
            var existing = Directory.GetDirectories(baseDir)
                .Select(Path.GetFileName)
                .Select(fname => Regex.Match(fname ?? "", $"^{Regex.Escape(prefix)}(\\d+)$"))
                .Where(m => m.Success)
                .Select(m => int.Parse(m.Groups[1].Value))
                .ToList();
            if (existing.Count > 0)
                n = existing.Max() + 1;
        }

        var resultsDir = Path.Combine(baseDir, $"{prefix}{n}");
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

    private string FailureOutputDir() =>
        string.IsNullOrWhiteSpace(_data.OutputDirectory)
            ? (string.IsNullOrWhiteSpace(_data.InputDirectory) ? "." : _data.InputDirectory)
            : _data.OutputDirectory;

    private void ReportFailure(string stage, string reason) =>
        FailureReportService.Write(FailureOutputDir(), _data.TargetName,
            _photometry.Mode == PhotometryMode.PsfFit ? "PsfFit" : "Aperture",
            _data.InputDirectory, stage, reason);

    [RelayCommand]
    private void Run()
    {
        var used = _photometry.Points.Where(p => !p.Rejected && !p.ExcludedFromTransit).OrderBy(p => p.Jd).ToList();
        if (used.Count < 4)
        {
            Status = $"Need at least 4 accepted frames — run Photometry first. (Have {used.Count}.)";
            ReportFailure("Results", Status);
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
            ReportFailure("Results", Status);
            return;
        }

        BestPeriod = result.BestPeriod;
        BestPower  = result.BestPower;
        FoldPeriod = result.BestPeriod;

        Fold();

        // ── Crowding flags (FWHM-residual regression) ──────────────────────────
        _crowdingFlags = [];
        var acceptedJds = new HashSet<double>(_jd);
        if (_photometry.PerCompSeries is { Count: > 0 } perComp)
        {
            _crowdingFlags.AddRange(CrowdingFlagService.CheckComps(perComp, acceptedJds));
            if (_photometry.TargetSeries is { Count: > 0 } targetSeries)
                _crowdingFlags.AddRange(CrowdingFlagService.CheckTarget(
                    targetSeries, perComp, acceptedJds, BestPeriod, BestPower, MinPeriodPowerForCrowdingCheck, _epoch));
        }

        CrowdingFlagsSummary = _crowdingFlags.Count > 0
            ? string.Join("\n", _crowdingFlags.Select(f => f.Message))
            : "";

        // Full per-star detail to the session log, not a condensed one-liner — the log is the
        // record you'd actually go back to when deciding whether a flagged star's data is
        // trustworthy, so it needs the same explanation the Results tab shows, not just the
        // bare numbers.
        SessionLogService.Write(_crowdingFlags.Count > 0
            ? $"[Results] Crowding flags: {_crowdingFlags.Count} star(s) flagged:"
            : "[Results] Crowding flags: none.");
        foreach (var f in _crowdingFlags)
            SessionLogService.Write($"[Results]   {f.Message}");

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
    /// into a "VariLab_Results_{Target}_{ObsDate}_N" subfolder of the Data tab's Output
    /// Directory (falls back to the Target Directory if Output Directory is somehow blank) —
    /// no save dialogs. Called automatically every time the period search runs (whether
    /// auto-triggered after Photometry or re-run manually after changing period settings),
    /// so exports always reflect the current light curve/fold. Each run gets the next available
    /// numbered folder for that target/night (..._1, ..._2, ...), so separate runs never mix
    /// their output together, and the folder name alone identifies which target and night it
    /// holds without having to open it. All three files from one run share a single
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

        string modeTag   = _photometry.Mode == PhotometryMode.PsfFit ? "PsfFit" : "Aperture";
        string modeLabel = _photometry.Mode == PhotometryMode.PsfFit ? "PSF Fit" : "Aperture";

        string? dir = null;
        try
        {
            Directory.CreateDirectory(baseDir);
            dir = GetNextResultsDir(baseDir, name, _jd[0], modeTag);
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
            var methodNote  = $"Photometry method: {modeLabel}";
            var crowdingNote = _crowdingFlags.Count > 0
                ? $"Crowding flags: {string.Join(", ", _crowdingFlags.Select(f => $"{f.StarLabel} (r={f.R:F2}, p={f.PValue:F4})"))} — see USER_GUIDE.md Appendix B"
                : "";
            var notes = string.Join("; ", new[] { methodNote, compNotes, flipNote, crowdingNote }.Where(s => !string.IsNullOrWhiteSpace(s)));
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
            var subtitle = $"{BuildCompSourceLabel(_compStars.Stars)}  |  Filter: {_data.FilterCode}  |  Method: {modeLabel}\n" +
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
                _compStars.Stars.ToList(), _compStars.RejectedStars.ToList(), modeLabel, _crowdingFlags);

            int nCompFiles = 0;
            if (_photometry.PerCompSeries is { Count: > 0 } perComp)
            {
                nCompFiles = ExportPerCompDiagnosticFiles(dir, perComp, _photometry.TargetSeries);
            }

            // Field image — best-effort: re-reads the reference frame from disk, so this can
            // fail on its own (moved/deleted file) without invalidating the AAVSO/PNG/Excel/
            // CompDiagnostics files already written above, which don't depend on the FITS file
            // still being there.
            string? fieldImageName = TryExportFieldImage(dir, name, ts);

            Status = $"✓  Saved to {dir}:  {Path.GetFileName(aavsoPath)}, {Path.GetFileName(pngPath)}, {Path.GetFileName(excelPath)}" +
                     (nCompFiles > 0 ? $", CompDiagnostics/ ({nCompFiles} stars)" : "") +
                     (fieldImageName is not null ? $", {fieldImageName}" : "");
        }
        catch (Exception ex)
        {
            Status = $"✗  Auto-export failed: {ex.Message}";
            SessionLogService.Write($"[Results] Auto-export failed: {ex}");
            ReportFailure("Results", $"Auto-export failed: {ex.Message}");

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

    /// <summary>Renders the annotated field image (reference frame + target/comp circles and
    /// aperture/annulus footprint) into the results folder. Best-effort: re-reads the reference
    /// frame from disk (the same first file <see cref="PhotometryService.RunAsync"/> used to
    /// size the aperture), so a moved/deleted FITS file, missing WCS, etc. just skips this file
    /// rather than failing the whole export — returns null in that case, or the exported
    /// filename on success.</summary>
    private string? TryExportFieldImage(string dir, string name, string ts)
    {
        try
        {
            if (!CoordinateParser.TryParseRa(_data.TargetRaText, out var ra) ||
                !CoordinateParser.TryParseDec(_data.TargetDecText, out var dec))
                return null;

            var files = FitsHeaderService.FindAllFits(_data.InputDirectory);
            if (files.Length == 0) return null;

            var hdr   = FitsHeaderService.Read(files[0]);
            var wcs   = WcsService.ReadWcs(hdr);
            var image = PsfService.ReadFitsPixels(files[0]);
            if (wcs is null || image is null) return null;

            var tpx = WcsService.SkyToPixel(wcs, ra, dec);
            if (tpx is null) return null;

            var comps = _compStars.Stars
                .Select((s, i) => new FieldMarker(s.X, s.Y, $"C{i + 1}"))
                .ToList();
            if (comps.Count == 0) return null;

            var target = new FieldMarker(tpx.Value.X, tpx.Value.Y, _data.TargetName);
            var title = $"{_data.TargetName} — Field ({comps.Count} comps)";
            // Annulus doesn't apply to PSF Fit mode (each frame's PSF fit has its own footprint,
            // not one fixed sky-background ring for the whole run) — annulus radii stay 0, which
            // FieldImageControl already correctly skips drawing (its own `if (AnnulusOuterPx > 0)`
            // guards). The *aperture* circle is different: PSF Fit still has a real star position
            // worth marking, so it gets a small fixed *visual* marker radius here — not tied to
            // any real photometric footprint, just "here's the star" — instead of 0, which
            // previously meant no circle drew at all (issue #6: PSF Fit field images had no
            // circles around target or comps whatsoever).
            const double PsfFitMarkerRadiusPx = 10.0;
            bool isPsfFit = _photometry.Mode == PhotometryMode.PsfFit;
            var subtitle = isPsfFit
                ? $"{Path.GetFileName(files[0])}\nMethod: PSF Fit"
                : $"{Path.GetFileName(files[0])}\n" +
                  $"Aperture: {_photometry.ApertureRadiusPx:F1}px  |  Annulus: {_photometry.AnnulusInnerPx:F1}-{_photometry.AnnulusOuterPx:F1}px";

            var fieldPath = Path.Combine(dir, $"{name}_FieldImage_{ts}.png");
            bool ok = FieldImageService.ExportFieldImagePng(
                fieldPath, image, target, comps,
                isPsfFit ? PsfFitMarkerRadiusPx : _photometry.ApertureRadiusPx,
                isPsfFit ? 0 : _photometry.AnnulusInnerPx,
                isPsfFit ? 0 : _photometry.AnnulusOuterPx,
                title, subtitle);
            return ok ? Path.GetFileName(fieldPath) : null;
        }
        catch (Exception ex)
        {
            SessionLogService.Write($"[Results] Field image export failed: {ex}");
            return null;
        }
    }

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
        CrowdingFlagsSummary    = "";
        _crowdingFlags = [];
        _jd      = [];
        _mag     = [];
        _magErr  = [];
        _airmass = [];
        _epoch   = 0;
    }
}
