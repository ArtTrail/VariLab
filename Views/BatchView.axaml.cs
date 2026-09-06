using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using VariLab.ViewModels;

namespace VariLab.Views;

public partial class BatchView : UserControl
{
    public BatchView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is BatchViewModel vm)
            {
                vm.FolderPickerFunc = PickFolderAsync;
                vm.FilePickerFunc   = PickFileAsync;
                vm.ShowPlotFunc     = ShowPlotAsync;

                // Log is a plain string rebuilt via "+=", not an appendable log control, so the
                // TextBox doesn't keep itself pinned to the bottom the way a native console does.
                // Nudging the caret to the end after every Log change scrolls the (long,
                // fast-growing during PSF Fit) text into view without the user having to scroll
                // by hand.
                vm.PropertyChanged += OnBatchViewModelPropertyChanged;
            }
        };
    }

    private int _plotWindowCascadeStep;
    private const int CascadeOffsetPx = 32;
    private const int CascadeWrapAfter = 10;

    /// <summary>Opens a preview window for a just-produced result plot (issue #10) — same
    /// window pattern as ResultsView's own ViewImageAsync, but cascaded (each new one offset
    /// diagonally from the last, wrapping back to the start after CascadeWrapAfter windows so
    /// it can't run off-screen) rather than stacked exactly on top of each other, and left open
    /// rather than auto-closed/replaced, since a long/both-methods batch is expected to
    /// genuinely produce several of these at once.</summary>
    private Task ShowPlotAsync(string path)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;

        Bitmap bitmap;
        try { bitmap = new Bitmap(path); }
        catch (Exception ex)
        {
            Services.SessionLogService.Write($"[Batch] Could not open plot '{path}': {ex}");
            return Task.CompletedTask;
        }

        var image = new Image { Source = bitmap, Stretch = Avalonia.Media.Stretch.Uniform };
        var win = new Window
        {
            Title   = System.IO.Path.GetFileName(path),
            Width   = Math.Min(bitmap.PixelSize.Width + 40, 1200),
            Height  = Math.Min(bitmap.PixelSize.Height + 60, 900),
            Content = new ScrollViewer { Content = image },
        };

        int step = _plotWindowCascadeStep % CascadeWrapAfter;
        _plotWindowCascadeStep++;

        if (owner is not null)
        {
            win.WindowStartupLocation = WindowStartupLocation.Manual;
            win.Position = new PixelPoint(
                owner.Position.X + step * CascadeOffsetPx,
                owner.Position.Y + step * CascadeOffsetPx);
            win.Show(owner);
        }
        else
        {
            win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            win.Show();
        }

        return Task.CompletedTask;
    }

    private void OnBatchViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(BatchViewModel.Log)) return;
        // Read the length from the ViewModel's own Log property, not LogTextBox.Text — both
        // this handler and the XAML's {Binding Log} are subscribed to the same PropertyChanged
        // event, and subscriber order isn't guaranteed. If the binding hadn't updated
        // LogTextBox.Text yet when this ran, CaretIndex was set from the *previous* (shorter)
        // text, so the view always lagged one update behind — reported (issue #11) as the log
        // not really auto-scrolling despite this code already being here. A ViewModel property
        // is always current by the time its own PropertyChanged fires, so this has no such race.
        if (sender is BatchViewModel vm)
            LogTextBox.CaretIndex = vm.Log.Length;
    }

    /// <summary>Same pattern as DataView's PickFolderAsync — startPath is this field's own
    /// current value, not a value shared across every folder picker in the window.</summary>
    private async Task<string?> PickFolderAsync(string title, string? startPath)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return null;

        IStorageFolder? startFolder = null;
        if (!string.IsNullOrWhiteSpace(startPath))
            startFolder = await topLevel.StorageProvider.TryGetFolderFromPathAsync(startPath);

        var results = await topLevel.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = title, AllowMultiple = false, SuggestedStartLocation = startFolder });
        return results.Count > 0 ? results[0].Path.LocalPath : null;
    }

    /// <summary>startDir is the last-imported file's own directory, same pattern as
    /// PickFolderAsync's startPath (issue #19).</summary>
    private async Task<string?> PickFileAsync(string? startDir)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return null;

        IStorageFolder? startFolder = null;
        if (!string.IsNullOrWhiteSpace(startDir))
            startFolder = await topLevel.StorageProvider.TryGetFolderFromPathAsync(startDir);

        var results = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title          = "Import Target List",
            AllowMultiple  = false,
            SuggestedStartLocation = startFolder,
            FileTypeFilter = new List<FilePickerFileType>
            {
                new("CSV / Excel files") { Patterns = ["*.csv", "*.xlsx"] },
                new("All files")         { Patterns = ["*"] },
            }
        });
        return results.Count > 0 ? results[0].Path.LocalPath : null;
    }
}
