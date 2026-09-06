using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Threading.Tasks;
using VariLab.Services;

namespace VariLab.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    public DataViewModel       Data        { get; }
    public CompStarsViewModel  CompStars   { get; }
    public PhotometryViewModel Photometry  { get; }
    public ResultsViewModel    Results     { get; }

    public string Version => AppVersion.Version;

    private readonly AppConfig _cfg = ConfigService.Load();

    public MainWindowViewModel()
    {
        Data       = new DataViewModel();
        CompStars  = new CompStarsViewModel(Data);
        Photometry = new PhotometryViewModel(Data, CompStars);
        Results    = new ResultsViewModel(Data, CompStars, Photometry);

        // No button click needed: once a photometry pass finishes, immediately run the
        // Results tab's period search (and its auto-export) on the new light curve.
        Photometry.Completed += () => Results.RunCommand.Execute(null);

        // A new dataset — or just a new target within the same dataset (e.g. switching between
        // stars in the same cluster field without changing the Target Directory) — invalidates
        // every downstream tab's results. Clear them so stale comps/photometry/period-search
        // output is never left over from whatever was open before.
        void ClearDownstreamTabs()
        {
            CompStars.Clear();
            Photometry.Clear();
            Results.Clear();
        }
        Data.InputDirectoryChanged += ClearDownstreamTabs;
        Data.TargetNameChanged     += ClearDownstreamTabs;
    }

    // ── Update checker (issue #17) ──────────────────────────────────────────
    // Plain zip/dmg download only — VariLab has no Inno-managed Windows installer (yet), unlike
    // TransitLab/StarFix, so there is no self-update path to offer here.

    public Func<Task<string?>>? BrowseUpdateFolderFunc { get; set; }
    public Func<string, string, Task>? ShowInfoFunc { get; set; }
    /// <summary>Closes the main window through its normal Closing path — used as a fallback if the
    /// installer's own close-and-reinstall doesn't complete promptly during self-update.</summary>
    public Action? RequestAppExitAction { get; set; }

    [ObservableProperty] private bool   _isUpdateAvailable   = false;
    [ObservableProperty] private bool   _isUpdateDownloading = false;
    [ObservableProperty] private bool   _isUpdateDone        = false;
    [ObservableProperty] private bool   _canSelfUpdate       = false;
    [ObservableProperty] private string _updateVersionText   = "";
    [ObservableProperty] private string _updateStatusText    = "";
    [ObservableProperty] private double _updateProgress      = 0;

    private UpdateInfo? _pendingUpdate;
    private UpdateInfo? _pendingInstallerUpdate;

    public async Task RunStartupUpdateCheckAsync()
    {
        var info = await UpdateService.CheckAsync(Version);
        if (info is null || info.Version == _cfg.SkippedUpdateVersion) return;

        _pendingUpdate    = info;
        UpdateVersionText = $"VariLab v{info.Version} is available";
        IsUpdateDone      = false;
        IsUpdateAvailable = true;

        await CheckSelfUpdateAsync();
    }

    // Only offered when this instance is an Inno-managed install (installer\VariLab.iss) AND the
    // latest release actually has a Setup .exe asset — both independently checked, since a
    // portable/manual copy (the zip) can't be safely reinstalled over silently.
    private async Task CheckSelfUpdateAsync()
    {
        _pendingInstallerUpdate = null;
        CanSelfUpdate = false;
        if (!UpdateService.IsSelfUpdateCapable()) return;

        var installerInfo = await UpdateService.CheckInstallerAsync(Version);
        if (installerInfo is null) return;

        _pendingInstallerUpdate = installerInfo;
        CanSelfUpdate = true;
    }

    [RelayCommand]
    private async Task SelfUpdateNow()
    {
        if (_pendingInstallerUpdate is null) return;

        var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "VariLab");
        System.IO.Directory.CreateDirectory(tempDir);
        var destPath = System.IO.Path.Combine(tempDir, _pendingInstallerUpdate.AssetName);

        IsUpdateAvailable   = false;
        IsUpdateDownloading = true;
        UpdateStatusText    = "Downloading update…";

        try
        {
            var progress = new Progress<(long done, long total)>(t =>
            {
                if (t.total > 0)
                {
                    UpdateProgress   = (double)t.done / t.total * 100;
                    UpdateStatusText = $"Downloading update… {t.done / 1_048_576.0:F1} MB / {t.total / 1_048_576.0:F1} MB";
                }
            });
            await UpdateService.DownloadFileAsync(_pendingInstallerUpdate.DownloadUrl, destPath, progress, default);

            UpdateStatusText = "Installing update — VariLab will close and reopen automatically…";
            UpdateService.LaunchSilentInstall(destPath);

            // The installer's Restart Manager-based CloseApplications should close this window on
            // its own once it detects the file lock on VariLab.exe. This is a fallback in case
            // that doesn't happen promptly, so the update never gets stuck with two processes.
            await Task.Delay(TimeSpan.FromSeconds(5));
            RequestAppExitAction?.Invoke();
        }
        catch (Exception ex)
        {
            UpdateStatusText    = $"Update failed: {ex.Message}";
            IsUpdateDownloading = false;
            IsUpdateDone        = true;
        }
    }

    [RelayCommand]
    private void SkipUpdate()
    {
        if (_pendingUpdate is not null)
        {
            _cfg.SkippedUpdateVersion = _pendingUpdate.Version;
            ConfigService.Save(_cfg);
        }
        IsUpdateAvailable = false;
    }

    [RelayCommand]
    private async Task DownloadUpdate()
    {
        if (_pendingUpdate is null || BrowseUpdateFolderFunc is null) return;

        var folder = await BrowseUpdateFolderFunc();
        if (folder is null) return;

        var destPath = System.IO.Path.Combine(folder, _pendingUpdate.AssetName);
        IsUpdateAvailable   = false;
        IsUpdateDownloading = true;
        UpdateStatusText    = "Downloading…";

        try
        {
            var progress = new Progress<(long done, long total)>(t =>
            {
                if (t.total > 0)
                {
                    UpdateProgress   = (double)t.done / t.total * 100;
                    UpdateStatusText = $"Downloading… {t.done / 1_048_576.0:F1} MB / {t.total / 1_048_576.0:F1} MB";
                }
            });
            await UpdateService.DownloadFileAsync(_pendingUpdate.DownloadUrl, destPath, progress, default);
            UpdateStatusText    = $"Downloaded to {destPath} — extract to update.";
            IsUpdateDownloading = false;
            IsUpdateDone        = true;
        }
        catch (Exception ex)
        {
            UpdateStatusText    = $"Download failed: {ex.Message}";
            IsUpdateDownloading = false;
            IsUpdateDone        = true;
        }
    }

    [RelayCommand]
    private void DismissUpdateDone() => IsUpdateDone = false;

    [RelayCommand]
    private async Task CheckForUpdates()
    {
        var info = await UpdateService.CheckAsync(Version);
        if (info is null)
        {
            if (ShowInfoFunc is not null)
                await ShowInfoFunc("Check for Updates", "VariLab is up to date.");
            return;
        }

        _pendingUpdate    = info;
        UpdateVersionText = $"VariLab v{info.Version} is available";
        IsUpdateDone      = false;
        IsUpdateAvailable = true;

        await CheckSelfUpdateAsync();
    }
}
