using Avalonia.Controls;
using System.Collections.Generic;
using System.Threading.Tasks;
using VariLab.Services;
using VariLab.ViewModels;
using VariLab.Views;

namespace VariLab.Views.Tabs;

public partial class PhotometryView : UserControl
{
    public PhotometryView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is PhotometryViewModel vm)
                vm.ExcludeTransitFunc = ExcludeTransitAsync;
        };
    }

    private Task<bool[]?> ExcludeTransitAsync(IReadOnlyList<PhotometryService.FramePoint> points, string yLabel)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        return owner is not null
            ? TransitExclusionWindow.ShowAsync(owner, points, yLabel)
            : Task.FromResult<bool[]?>(null);
    }
}
