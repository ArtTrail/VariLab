using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VariLab.Services;

namespace VariLab.ViewModels;

/// <summary>
/// Batch Process window (Tools menu) — runs the Data/Comp Stars/Photometry/Results pipeline
/// unattended across a list of targets via BatchRunService, instead of one target at a time
/// through the main tabs.
/// </summary>
public partial class BatchViewModel : ViewModelBase
{
    private readonly AppConfig _config = ConfigService.Load();

    // Same fixed list DataViewModel.FilterOptions exposes — duplicated rather than shared
    // since this window builds its own headless ViewModel graph inside BatchRunService and
    // has no DataViewModel instance of its own to read it from until Start is clicked.
    public string[] FilterOptions { get; } =
    [
        "SU", "SG", "SR", "SI", "SZ",
        "U", "B", "V", "R", "Rc", "I", "Ic",
        "Y", "J", "H", "K",
        "CBB", "C", "CV", "IJ", "L", "MA", "MB", "MI", "N/A",
        "RJ", "STB", "STHBN", "STHBW", "STU", "STV", "STY",
    ];

    [ObservableProperty] private string _inputDirectory       = "";
    [ObservableProperty] private string _resultsDirectory      = "";

    /// <summary>Each of Input/Results Directory remembers its own last-used value
    /// independently, mirroring DataViewModel's Target/Output Directory persistence — not
    /// shared with each other or with the Data tab's own directories.</summary>
    public BatchViewModel()
    {
        if (!string.IsNullOrEmpty(_config.LastBatchInputDirectory))
            InputDirectory = _config.LastBatchInputDirectory;
        if (!string.IsNullOrEmpty(_config.LastBatchResultsDirectory))
            ResultsDirectory = _config.LastBatchResultsDirectory;
        if (!string.IsNullOrEmpty(_config.AavsoObserverCode))
            AavsoObserverCode = _config.AavsoObserverCode;
    }

    partial void OnInputDirectoryChanged(string value)
    {
        _config.LastBatchInputDirectory = value;
        ConfigService.Save(_config);
    }

    partial void OnResultsDirectoryChanged(string value)
    {
        _config.LastBatchResultsDirectory = value;
        ConfigService.Save(_config);
    }

    /// <summary>Issue #20 — shares DataViewModel's same config slot: one AAVSO observer code
    /// per user, not a separate one per window.</summary>
    partial void OnAavsoObserverCodeChanged(string value)
    {
        _config.AavsoObserverCode = value;
        ConfigService.Save(_config);
    }
    [ObservableProperty] private string _targetsText            = "";
    [ObservableProperty] private string _filterCode             = "V";
    [ObservableProperty] private string _aavsoObserverCode      = "";
    [ObservableProperty] private int    _maxCompStars           = 10;
    [ObservableProperty] private bool   _runAperture             = true;
    [ObservableProperty] private bool   _runPsfFit                = true;

    /// <summary>Issue #10 — unchecked by default (a long multi-target/both-methods batch could
    /// pop up a lot of windows; opting in avoids surprising a user who just wants the batch to
    /// run quietly). Popups stay open once shown (cascaded, not stacked exactly on top of each
    /// other) rather than auto-closing/replacing — see BatchView.axaml.cs's ShowPlotAsync.</summary>
    [ObservableProperty] private bool   _popUpResultsPlot        = false;
    [ObservableProperty] private bool   _isRunning               = false;
    [ObservableProperty] private string _log                     = "";
    [ObservableProperty] private string _status                  = "Not run yet.";

    /// <summary>Populated after "Import from file…" picks a CSV/XLSX — the column-picker
    /// ComboBox is only shown while this has entries.</summary>
    public ObservableCollection<string> ImportedColumns { get; } = [];

    [ObservableProperty] private string? _selectedImportColumn;
    private string? _importedFilePath;

    private CancellationTokenSource? _cts;

    /// <summary>Set by the view's code-behind to the platform folder picker (same pattern as
    /// DataViewModel.FolderPickerFunc).</summary>
    public Func<string, string?, Task<string?>>? FolderPickerFunc { get; set; }

    /// <summary>Set by the view's code-behind to the platform file picker, filtered to
    /// CSV/XLSX. Takes a starting directory, same pattern as FolderPickerFunc (issue #19).</summary>
    public Func<string?, Task<string?>>? FilePickerFunc { get; set; }

    /// <summary>Set by the view's code-behind to open a (cascaded) preview window for a
    /// just-produced result plot — only invoked by BatchRunService when PopUpResultsPlot is
    /// checked (issue #10).</summary>
    public Func<string, Task>? ShowPlotFunc { get; set; }

    [RelayCommand]
    private async Task BrowseInputDirectory()
    {
        if (FolderPickerFunc is null) return;
        var path = await FolderPickerFunc("Select folder of pre-calibrated, plate-solved FITS frames", InputDirectory);
        if (!string.IsNullOrEmpty(path)) InputDirectory = path;
    }

    [RelayCommand]
    private async Task BrowseResultsDirectory()
    {
        if (FolderPickerFunc is null) return;
        var path = await FolderPickerFunc("Select folder where each target's results subfolder will be created", ResultsDirectory);
        if (!string.IsNullOrEmpty(path)) ResultsDirectory = path;
    }

    [RelayCommand]
    private async Task ImportFromFile()
    {
        if (FilePickerFunc is null) return;
        var startDir = string.IsNullOrEmpty(_config.LastImportFilePath)
            ? null
            : System.IO.Path.GetDirectoryName(_config.LastImportFilePath);
        var path = await FilePickerFunc(startDir);
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            var headers = TargetListImportService.ReadHeaders(path);
            if (headers.Length == 0)
            {
                Status = "✗  That file has no header row to read.";
                return;
            }
            _importedFilePath = path;
            _config.LastImportFilePath = path;
            ConfigService.Save(_config);
            ImportedColumns.Clear();
            foreach (var h in headers) ImportedColumns.Add(h);
            SelectedImportColumn = headers[0];
            Status = $"Pick which column holds target names, then click \"Use column\".";
        }
        catch (Exception ex)
        {
            Status = $"✗  Could not read {System.IO.Path.GetFileName(path)}: {ex.Message}";
        }
    }

    [RelayCommand]
    private void UseImportedColumn()
    {
        if (_importedFilePath is null || SelectedImportColumn is null) return;
        var names = TargetListImportService.ReadColumn(_importedFilePath, SelectedImportColumn);
        TargetsText = string.Join(Environment.NewLine, names);
        ImportedColumns.Clear();
        Status = $"✓  Imported {names.Count} target name(s) from \"{SelectedImportColumn}\".";
    }

    private bool CanStart() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task Start()
    {
        var targets = TargetsText
            .Split('\n')
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .ToList();

        if (targets.Count == 0)
        {
            Status = "Enter at least one target name first.";
            return;
        }
        if (string.IsNullOrWhiteSpace(InputDirectory) || string.IsNullOrWhiteSpace(ResultsDirectory))
        {
            Status = "Set both the Input Directory and Results Directory first.";
            return;
        }
        if (!RunAperture && !RunPsfFit)
        {
            Status = "Check at least one of Aperture / PSF Fit.";
            return;
        }

        Log = "";
        IsRunning = true;
        StartElapsedTimer();
        Status = $"⟳  Running {targets.Count} target(s)…";
        _cts = new CancellationTokenSource();
        var progress = new Progress<string>(s => Log += s + "\n");

        try
        {
            await BatchRunService.RunAsync(
                InputDirectory, ResultsDirectory, targets,
                RunAperture, RunPsfFit,
                FilterCode, AavsoObserverCode, MaxCompStars,
                progress, _cts.Token,
                PopUpResultsPlot ? ShowPlotFunc : null);
            Status = $"✓  Batch finished — {targets.Count} target(s) processed.";
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled.";
        }
        catch (Exception ex)
        {
            Status = $"✗  Batch failed: {ex.Message}";
            SessionLogService.Write($"[Batch] Run failed: {ex}");
        }
        finally
        {
            IsRunning = false;
            StopElapsedTimer();
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    partial void OnIsRunningChanged(bool value) => StartCommand.NotifyCanExecuteChanged();
}
