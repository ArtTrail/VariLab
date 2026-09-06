using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VariLab.Services;

namespace VariLab.ViewModels;

/// <summary>
/// PSF Engine Setup popup — one-time (per machine) setup for the PSF-fit photometry mode:
/// finds a Python 3.11+ interpreter, creates an isolated venv at
/// %AppData%\VariLab\pyenv, and installs the bundled engine's dependencies
/// (astropy, photutils, numpy, scipy) into it. Mirrors the shape of TransitLab's own
/// EXOTIC setup flow, trimmed down since there's no separate package to install/uninstall
/// or stable/pre-release channel to choose — just "is this venv ready or not."
/// </summary>
public partial class PsfEngineSetupViewModel : ViewModelBase
{
    [ObservableProperty] private bool   _isBusy;
    [ObservableProperty] private bool   _isReady;
    [ObservableProperty] private string _statusHeadline = "Checking PSF-fit engine status…";
    [ObservableProperty] private string _logText = "";

    private CancellationTokenSource? _cts;

    public async Task InitializeAsync()
    {
        await CheckStatusAsync();
    }

    [RelayCommand]
    private async Task CheckStatus() => await CheckStatusAsync();

    private async Task CheckStatusAsync()
    {
        IsBusy = true;
        try
        {
            var runtime = await PsfEngineRuntimeService.ResolveAsync();
            IsReady = runtime is not null;
            StatusHeadline = IsReady
                ? "✓  PSF-fit engine is ready."
                : "PSF-fit engine is not set up yet on this machine.";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task Install()
    {
        if (IsBusy) return;
        IsBusy = true;
        _cts   = new CancellationTokenSource();
        var log = new Progress<string>(Log);

        try
        {
            Log("Looking for a Python 3.11+ interpreter…");
            var python = await PsfEngineInstallService.FindPythonAsync(_cts.Token);
            if (python is null)
            {
                Log("");
                Log("✗  No compatible Python found (need 3.11 or newer).");
                Log($"   Install it from {PsfEngineInstallService.PyDownloadUrl}, then click Install again.");
                StatusHeadline = "Python 3.11+ required — see log below.";
                return;
            }
            Log($"Found Python {python.Version}: {python.ExePath}");

            if (!await PsfEngineInstallService.ValidatePythonAsync(python.ExePath, _cts.Token))
            {
                Log("✗  That Python installation looks broken (can't import stdlib). Try reinstalling Python.");
                StatusHeadline = "Python installation looks broken — see log below.";
                return;
            }

            await PsfEngineInstallService.CreateVenvAsync(python.ExePath, PsfEngineRuntimeService.VenvDir, log, _cts.Token);
            await PsfEngineInstallService.InstallDepsAsync(
                PsfEngineRuntimeService.VenvPythonExe, PsfEngineRuntimeService.RequirementsPath, log, _cts.Token);

            Log("");
            var runtime = await PsfEngineRuntimeService.ResolveAsync(_cts.Token);
            IsReady = runtime is not null;
            StatusHeadline = IsReady
                ? "✓  PSF-fit engine set up successfully."
                : "✗  Setup finished but the engine still isn't importable — see log above.";
            Log(StatusHeadline);
        }
        catch (OperationCanceledException)
        {
            Log("Cancelled.");
            StatusHeadline = "Setup cancelled.";
        }
        catch (Exception ex)
        {
            Log($"✗  {ex.Message}");
            StatusHeadline = "Setup failed — see log below.";
            SessionLogService.Write($"[PsfEngineSetup] {ex}");
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private readonly StringBuilder _logBuffer = new();
    private void Log(string message)
    {
        _logBuffer.AppendLine(message);
        LogText = _logBuffer.ToString();
    }
}
