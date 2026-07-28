using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using VariLab.Services;
using VariLab.ViewModels;

namespace VariLab;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            SessionLogService.Initialize($"v{AppVersion.Version}");

            var mainWindow = new MainWindow();
            var vm         = new MainWindowViewModel();
            mainWindow.DataContext = vm;
            vm.CompStars.ConfirmOnSourceFailure = (source, message) =>
                Views.ConfirmSourceFailureDialog.ShowAsync(mainWindow, source, message);
            vm.CompStars.NotifyPlateSolveRequired = (withWcs, total) =>
                Views.PlateSolveRequiredDialog.ShowAsync(mainWindow, withWcs, total);
            vm.CompStars.NotifyTargetNotInFrame = (targetRa, targetDec, frameRa, frameDec, sepDeg, fovArcmin) =>
                Views.TargetNotInFrameDialog.ShowAsync(mainWindow, targetRa, targetDec, frameRa, frameDec, sepDeg, fovArcmin);

            desktop.MainWindow = mainWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }
}