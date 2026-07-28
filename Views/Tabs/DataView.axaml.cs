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

    private async Task<string?> PickFolderAsync(string title)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return null;
        var results = await topLevel.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return results.Count > 0 ? results[0].Path.LocalPath : null;
    }
}
