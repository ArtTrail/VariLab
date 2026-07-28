using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using VariLab.ViewModels;
using VariLab.Views;

namespace VariLab;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = $"VariLab v{AppVersion.Version}";
        AttributionText.Text = $"© Art Trail 2026  ·  VariLab v{AppVersion.Version}  ·  Comp-star selection via the Stone method (Geoff Stone)";
    }

    private void OnUserGuideClick(object? sender, RoutedEventArgs e)
    {
        var win = new Window
        {
            Title                 = "User Guide",
            Width                 = 900,
            Height                = 700,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content               = new UserGuideView(),
        };
        win.Show(this);
    }

    private void OnRevisionHistoryClick(object? sender, RoutedEventArgs e)
    {
        var win = new Window
        {
            Title                 = "Revision History",
            Width                 = 800,
            Height                = 640,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content               = new RevisionHistoryView(),
        };
        win.Show(this);
    }

    private void OnAboutClick(object? sender, RoutedEventArgs e)
    {
        var win = new Window
        {
            Title                 = "About VariLab",
            Width                 = 520,
            SizeToContent         = SizeToContent.Height,
            CanResize             = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content               = new AboutView(),
        };
        win.Show(this);
    }

    private void OnDiagnosticsClick(object? sender, RoutedEventArgs e)
    {
        Window? win = null;
        var diagVm = new DiagnosticsViewModel();
        diagVm.SaveFileFunc    = SaveDiagnosticsLogAsync;
        diagVm.OpenLogFileFunc = OpenPreviousLogAsync;
        diagVm.CloseCallback   = () => win?.Close();

        win = new Window
        {
            Title                 = "Diagnostics — Session Log",
            Width                 = 900,
            Height                = 600,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content               = new DiagnosticsView { DataContext = diagVm },
        };
        win.Opened += (_, _) => diagVm.Connect();
        win.Closed  += (_, _) => diagVm.Disconnect();
        win.Show(this);
    }

    private async Task<string?> OpenPreviousLogAsync()
    {
        var logsDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VariLab", "logs");

        var results = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title          = "Open Previous Session Log",
            AllowMultiple  = false,
            SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(logsDir),
            FileTypeFilter = new List<FilePickerFileType>
            {
                new("Log files") { Patterns = ["*.log"] },
                new("All files") { Patterns = ["*"]     },
            }
        });
        return results.Count > 0 ? results[0].Path.LocalPath : null;
    }

    private async Task<string?> SaveDiagnosticsLogAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title             = "Save Session Log",
            SuggestedFileName = $"VariLab_diagnostics_log_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
            FileTypeChoices   = new List<FilePickerFileType>
            {
                new("Text files") { Patterns = ["*.txt"] },
                new("All files")  { Patterns = ["*"]     },
            }
        });
        return file?.Path.LocalPath;
    }
}
