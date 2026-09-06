using Avalonia.Controls;
using Avalonia.Platform.Storage;
using System.Threading.Tasks;
using VariLab.ViewModels;

namespace VariLab.Views.Tabs;

public partial class DataView : UserControl
{
    public DataView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is DataViewModel vm)
                vm.FolderPickerFunc = PickFolderAsync;
        };
    }

    /// <summary>startPath is this specific field's own current/last-used directory, not a
    /// single value shared across every folder picker in the app — without it, the OS's own
    /// "wherever a folder dialog was last closed" memory is shared globally, so Target
    /// Directory's and Output Directory's Browse buttons indistinguishably clobber each
    /// other's starting location.</summary>
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
}
