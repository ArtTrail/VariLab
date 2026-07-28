using Avalonia.Controls;

namespace VariLab.Views;

public partial class AboutView : UserControl
{
    public AboutView()
    {
        InitializeComponent();
        VersionText.Text = $"Version {AppVersion.Version}";
    }
}
