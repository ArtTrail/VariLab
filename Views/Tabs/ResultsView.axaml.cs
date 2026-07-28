using Avalonia.Controls;
using Avalonia.Media.Imaging;
using System;
using System.Threading.Tasks;
using VariLab.ViewModels;

namespace VariLab.Views.Tabs;

public partial class ResultsView : UserControl
{
    public ResultsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ResultsViewModel vm)
                vm.ViewImageFunc = ViewImageAsync;
        };
    }

    private Task ViewImageAsync(string path)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;

        Bitmap bitmap;
        try { bitmap = new Bitmap(path); }
        catch (Exception ex)
        {
            Services.SessionLogService.Write($"[Results] Could not open PNG '{path}': {ex}");
            return Task.CompletedTask;
        }

        var image = new Image { Source = bitmap, Stretch = Avalonia.Media.Stretch.Uniform };
        var win = new Window
        {
            Title                 = System.IO.Path.GetFileName(path),
            Width                 = Math.Min(bitmap.PixelSize.Width + 40, 1200),
            Height                = Math.Min(bitmap.PixelSize.Height + 60, 900),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content               = new ScrollViewer { Content = image },
        };

        if (owner is not null) win.Show(owner);
        else win.Show();

        return Task.CompletedTask;
    }
}
