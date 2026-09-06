using Avalonia.Controls;
using System.Threading.Tasks;
using VariLab.ViewModels;

namespace VariLab.Views;

public partial class PsfEngineSetupWindow : Window
{
    public PsfEngineSetupWindow()
    {
        InitializeComponent();
        var closeButton = this.FindControl<Button>("CloseButton");
        if (closeButton is not null) closeButton.Click += (_, _) => Close();
    }

    /// <summary>Shows the setup popup modally; returns once closed. The caller (Photometry
    /// tab) re-checks PSF-engine readiness afterward rather than reading a return value here,
    /// since the user may check status, install, re-check, and close in any order.</summary>
    public static Task ShowAsync(Window owner)
    {
        var vm = new PsfEngineSetupViewModel();
        var win = new PsfEngineSetupWindow { DataContext = vm };
        _ = vm.InitializeAsync();
        return win.ShowDialog(owner);
    }
}
