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
    // Issue #23 — set once the user has confirmed closing mid-processing, so the second
    // programmatic Close() isn't intercepted again.
    private bool _forceClose;

    public MainWindow()
    {
        InitializeComponent();
        Title = $"VariLab v{AppVersion.Version}";
        AttributionText.Text = $"© Art Trail 2026  ·  VariLab v{AppVersion.Version}  ·  Comp-star selection via the Stone method (Geoff Stone)";

        Opened += async (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
                await vm.RunStartupUpdateCheckAsync();
        };

        // Issue #23 — warn before closing while Comp Stars or Photometry is still running, so an
        // accidental window close doesn't silently abandon a long run. Closing can't be awaited,
        // so cancel it, ask, and re-issue the close if confirmed.
        Closing += async (_, e) =>
        {
            if (_forceClose) return;
            if (DataContext is not MainWindowViewModel vm) return;
            if (!vm.CompStars.IsRunning && !vm.Photometry.IsRunning) return;

            e.Cancel = true;
            bool ok = await ConfirmAsync(
                "Processing in progress",
                "VariLab is still processing (Comp Stars or Photometry is running).\n\n" +
                "Closing now will stop it. Close anyway?");
            if (ok)
            {
                _forceClose = true;
                Close();
            }
        };
    }

    /// <summary>Modal Yes/No confirmation, returns true if the user chose the affirmative button.
    /// Same visual style as ShowInfoAsync.</summary>
    internal async Task<bool> ConfirmAsync(string title, string message,
        string confirmText = "Close anyway", string cancelText = "Keep running")
    {
        var confirmBtn = new Button
        {
            Content = confirmText, MinWidth = 110,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
        };
        var cancelBtn = new Button
        {
            Content = cancelText, MinWidth = 110,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Classes = { "primary" },
        };

        var dialog = new Window
        {
            Title                 = title,
            Width                 = 380,
            SizeToContent         = SizeToContent.Height,
            CanResize             = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin  = new Avalonia.Thickness(28, 24, 28, 20),
                Spacing = 18,
                Children =
                {
                    new TextBlock
                    {
                        Text          = message,
                        TextWrapping  = Avalonia.Media.TextWrapping.Wrap,
                        TextAlignment = Avalonia.Media.TextAlignment.Center,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                        Spacing = 12,
                        Children = { cancelBtn, confirmBtn },
                    },
                },
            },
        };

        confirmBtn.Click += (_, _) => dialog.Close(true);
        cancelBtn.Click  += (_, _) => dialog.Close(false);
        return await dialog.ShowDialog<bool>(this);
    }

    internal async Task<string?> BrowseUpdateFolderAsync()
    {
        var defaultPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloadsPath = System.IO.Path.Combine(defaultPath, "Downloads");
        var startPath = System.IO.Directory.Exists(downloadsPath) ? downloadsPath : defaultPath;

        var folder = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title                  = "Choose download folder",
            AllowMultiple          = false,
            SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(startPath),
        });
        return folder.Count > 0 ? folder[0].Path.LocalPath : null;
    }

    internal async Task ShowInfoAsync(string title, string message)
    {
        var btn = new Button
        {
            Content                    = "OK",
            HorizontalAlignment        = Avalonia.Layout.HorizontalAlignment.Center,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            MinWidth                   = 70,
            Classes                    = { "primary" },
        };

        var dialog = new Window
        {
            Title                 = title,
            Width                 = 320,
            SizeToContent         = SizeToContent.Height,
            CanResize             = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin  = new Avalonia.Thickness(28, 24, 28, 20),
                Spacing = 18,
                Children =
                {
                    new TextBlock
                    {
                        Text                = message,
                        TextWrapping        = Avalonia.Media.TextWrapping.Wrap,
                        TextAlignment       = Avalonia.Media.TextAlignment.Center,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    },
                    btn,
                }
            },
        };

        btn.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
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

    private void OnBugReportClick(object? sender, RoutedEventArgs e)
    {
        Window? win = null;
        var bugVm = new BugReportViewModel();
        bugVm.CloseCallback = () => win?.Close();

        win = new Window
        {
            Title                 = "Submit Feedback",
            Width                 = 560,
            SizeToContent         = SizeToContent.Height,
            CanResize             = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content               = new BugReportView { DataContext = bugVm },
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

    private void OnBatchProcessClick(object? sender, RoutedEventArgs e)
    {
        var batchVm = new BatchViewModel();
        Window? win = null;
        bool batchForceClose = false;
        win = new Window
        {
            Title                 = "Batch Process",
            Width                 = 900,
            Height                = 950,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content               = new BatchView { DataContext = batchVm },
        };

        // Issue #23 — same close-while-running guard as the main window, for the batch window.
        win.Closing += async (_, ce) =>
        {
            if (batchForceClose) return;
            if (!batchVm.IsRunning) return;

            ce.Cancel = true;
            bool ok = await ConfirmAsync(
                "Batch in progress",
                "A batch run is still processing.\n\nClosing now will stop it. Close anyway?");
            if (ok)
            {
                batchForceClose = true;
                win?.Close();
            }
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
