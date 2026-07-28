using Avalonia.Controls;
using Avalonia.Interactivity;

namespace VariLab.Views;

public partial class UserGuideView : UserControl
{
    public UserGuideView()
    {
        InitializeComponent();
    }

    /// <summary>Scrolls the Table of Contents entry's target section into view. Each
    /// HyperlinkButton's Tag names the section's x:Name (e.g. "3" -> Section3).</summary>
    private void OnTocClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: string tag }) return;

        var name = tag == "Appendix" ? "SectionAppendix" : $"Section{tag}";
        if (this.FindControl<TextBlock>(name) is { } target)
            target.BringIntoView();
    }
}
