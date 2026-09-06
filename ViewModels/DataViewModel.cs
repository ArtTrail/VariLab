using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using VariLab.Services;

namespace VariLab.ViewModels;

/// <summary>
/// Data tab — input folder of pre-calibrated, plate-solved FITS frames,
/// plus the target star's sky position and observation filter.
/// </summary>
public partial class DataViewModel : ViewModelBase
{
    private readonly AppConfig _config = ConfigService.Load();

    [ObservableProperty] private string _inputDirectory = "";
    [ObservableProperty] private string _outputDirectory = "";  // where VariLab_ResultsN export folders go
    [ObservableProperty] private string _targetName     = "";   // star designation, for AAVSO export
    [ObservableProperty] private string _targetRaText    = "";   // decimal degrees or sexagesimal (HH:MM:SS)
    [ObservableProperty] private string _targetDecText   = "";   // decimal degrees or sexagesimal (+DD:MM:SS)
    [ObservableProperty] private string _filterCode      = "V";
    [ObservableProperty] private string _aavsoObserverCode = "";   // persisted across sessions — see constructor
    [ObservableProperty] private string _status          = "";
    [ObservableProperty] private bool   _isLookingUp     = false;

    /// <summary>True only after a successful name→RA/Dec lookup for the CURRENT TargetName —
    /// reset to false whenever TargetName changes (a resolution for the old name doesn't carry
    /// over) or a lookup fails. Comp Stars' Run button is gated on this, since running against
    /// an unresolved/stale RA/Dec is guaranteed to fail downstream anyway.</summary>
    [ObservableProperty] private bool   _targetResolved  = false;

    /// <summary>True only right after a failed lookup — distinct from simply "!TargetResolved",
    /// which is also true before any lookup has ever been attempted (nothing wrong yet, just
    /// nothing tried). Drives the Status text turning red; TargetResolved alone drives the Run
    /// button gating.</summary>
    [ObservableProperty] private bool   _lookupFailed    = false;

    // Matches TransitLab's full filter-code list (EquipmentTargetViewModel.FilterCodes), plus
    // Rc/Ic/C which VariLab already recognized and TransitLab doesn't separately list. Most of
    // these (SU, U, Y, J, H, K, MA, MB, MI, N/A, ST*) have no comp-star magnitude source in
    // GaiaCompService's VSP/APASS/GSPC maps — selecting one just falls through to the G→V
    // last resort, same as in TransitLab.
    public string[] FilterOptions { get; } =
    [
        "SU", "SG", "SR", "SI", "SZ",
        "U", "B", "V", "R", "Rc", "I", "Ic",
        "Y", "J", "H", "K",
        "CBB", "C", "CV", "IJ", "L", "MA", "MB", "MI", "N/A",
        "RJ", "STB", "STHBN", "STHBW", "STU", "STV", "STY",
    ];

    public DataViewModel()
    {
        _aavsoObserverCode = _config.AavsoObserverCode;

        // Restore the last-used Target Directory (via the property setter, not the backing
        // field, so it drives the same auto-fill/auto-detect as a fresh Browse… pick). This
        // also fires OnInputDirectoryChanged below, which — since OutputDirUserSet is still
        // false at this point — sets OutputDirectory to match, exactly like a fresh session's
        // auto-follow would.
        if (!string.IsNullOrEmpty(_config.LastTargetDirectory))
            InputDirectory = _config.LastTargetDirectory;

        // Now restore Output Directory's own independently-remembered value, if the user has
        // ever explicitly browsed to one (BrowseOutputDirectory is the only place that saves
        // this) — overriding whatever the auto-follow above just set, and marking it user-set
        // so this session's own Target Directory changes don't silently overwrite it again.
        if (!string.IsNullOrEmpty(_config.LastOutputDirectory))
        {
            OutputDirectory = _config.LastOutputDirectory;
            OutputDirUserSet = true;
        }
    }

    partial void OnAavsoObserverCodeChanged(string value)
    {
        _config.AavsoObserverCode = value;
        ConfigService.Save(_config);
    }

    /// <summary>Set by the view's code-behind to the platform folder picker. Second arg is the
    /// starting directory to suggest — each field passes its own current value, not a value
    /// shared across fields (see BrowseInputDirectory/BrowseOutputDirectory).</summary>
    public System.Func<string, string?, Task<string?>>? FolderPickerFunc { get; set; }

    private string? _lastAutoDetectedDir;

    /// <summary>True once the user has explicitly browsed to an Output Directory — after that,
    /// changing the Target Directory no longer overwrites it. Mirrors TransitLab's
    /// SaveDirUserSet pattern for the same "Directory to Save Plots" auto-fill-once behavior.</summary>
    public bool OutputDirUserSet { get; set; } = false;

    [RelayCommand]
    private async Task BrowseInputDirectory()
    {
        if (FolderPickerFunc is null) return;
        var path = await FolderPickerFunc("Select folder of pre-calibrated, plate-solved FITS frames", InputDirectory);
        if (!string.IsNullOrEmpty(path))
            InputDirectory = path;
    }

    [RelayCommand]
    private async Task BrowseOutputDirectory()
    {
        if (FolderPickerFunc is null) return;
        var path = await FolderPickerFunc("Select folder for VariLab's exported results", OutputDirectory);
        if (!string.IsNullOrEmpty(path))
        {
            OutputDirectory = path;
            OutputDirUserSet = true;
            _config.LastOutputDirectory = path;
            ConfigService.Save(_config);
        }
    }

    /// <summary>Raised whenever InputDirectory is set to a new value, so the other tabs (Comp
    /// Stars, Photometry, Results) can clear their previous dataset's stale results.</summary>
    public event Action? InputDirectoryChanged;

    /// <summary>Raised whenever TargetName is set to a new value, so the other tabs (Comp Stars,
    /// Photometry, Results) can clear their previous target's stale results — covers the case
    /// where the Target Directory is unchanged but the user has switched to a different star
    /// within the same field (e.g. a different variable in the same cluster).</summary>
    public event Action? TargetNameChanged;

    /// <summary>
    /// Reads the first FITS file's header for OBJECT (→ Target Name, planet-letter suffix
    /// stripped) and FILTER, then automatically looks up RA/Dec once a name is filled in.
    /// Also defaults Output Directory to match, unless the user has already browsed their own.
    /// Persists the directory so the next launch defaults to this one.
    /// </summary>
    partial void OnInputDirectoryChanged(string value)
    {
        InputDirectoryChanged?.Invoke();

        if (!OutputDirUserSet)
            OutputDirectory = value;

        _config.LastTargetDirectory = value;
        ConfigService.Save(_config);

        if (value == _lastAutoDetectedDir) return;

        var fitsPath = FitsHeaderService.FindFirstFits(value);
        if (fitsPath is null) return;

        _lastAutoDetectedDir = value;
        _ = AutoDetectFromFitsAsync(fitsPath);
    }

    partial void OnTargetNameChanged(string value)
    {
        TargetResolved = false;
        LookupFailed   = false;
        TargetNameChanged?.Invoke();
    }

    private async Task AutoDetectFromFitsAsync(string fitsPath)
    {
        FitsHeaderService.FitsHeader hdr;
        try
        {
            hdr = FitsHeaderService.Read(fitsPath);
        }
        catch (Exception ex)
        {
            Status = $"✗  Could not read FITS header: {ex.Message}";
            SessionLogService.Write($"[Data] FITS header read failed: {ex}");
            return;
        }

        var filter = hdr.Get("FILTER");
        if (!string.IsNullOrWhiteSpace(filter))
            FilterCode = MatchFilterOption(filter);

        var obj = hdr.Get("OBJECT");
        if (!string.IsNullOrWhiteSpace(obj))
        {
            TargetName = StripPlanetSuffix(obj);
            await LookUpCoordinates();
        }
    }

    /// <summary>Strips a trailing single lowercase planet-letter designator, e.g. "KELT-8 b" → "KELT-8",
    /// "TOI-4463 A b" → "TOI-4463 A" (the uppercase host-star component letter is kept).</summary>
    private static string StripPlanetSuffix(string name)
        => Regex.Replace(name.Trim(), @"\s+[a-z]$", "");

    private string MatchFilterOption(string headerFilter)
    {
        foreach (var opt in FilterOptions)
            if (opt.Equals(headerFilter, StringComparison.OrdinalIgnoreCase))
                return opt;

        if (headerFilter.Equals("Clear", StringComparison.OrdinalIgnoreCase))
            return "CV";

        return headerFilter;
    }

    /// <summary>Resolves TargetName to RA/Dec via AAVSO VSX → NASA Exoplanet Archive → SIMBAD, in order.</summary>
    [RelayCommand]
    private async Task LookUpCoordinates()
    {
        if (string.IsNullOrWhiteSpace(TargetName))
        {
            Status = "Enter a target name first.";
            return;
        }

        IsLookingUp = true;
        Status = "⟳  Trying AAVSO VSX…";
        var progress = new Progress<string>(s => Status = s);

        try
        {
            var result = await TargetResolverService.ResolveAsync(TargetName, progress);
            if (result is null)
            {
                TargetResolved = false;
                LookupFailed   = true;
                Status = $"✗  '{TargetName}' not found in VSX, NASA Exoplanet Archive, or SIMBAD.";
                return;
            }

            TargetRaText  = result.Ra.ToString("F6");
            TargetDecText = result.Dec.ToString("F6");
            TargetResolved = true;
            LookupFailed   = false;
            Status = $"✓  Resolved via {result.Source} as '{result.MatchedName}'  " +
                      $"(RA={result.Ra:F6}, Dec={result.Dec:F6})";
        }
        finally
        {
            IsLookingUp = false;
        }
    }
}
