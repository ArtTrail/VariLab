using System.Threading.Tasks;
using Avalonia.Controls;

namespace VariLab.Views;

public partial class ConfirmSourceFailureDialog : Window
{
    public ConfirmSourceFailureDialog()
    {
        InitializeComponent();
        ContinueButton.Click += (_, _) => Close(true);
        CancelButton.Click   += (_, _) => Close(false);
    }

    /// <summary>
    /// Shows a modal warning that <paramref name="source"/> failed during comp-star
    /// selection, with the raw error/status message, and asks whether to continue with
    /// whatever sources did respond or cancel the search outright. Returns true to continue.
    /// </summary>
    public static Task<bool> ShowAsync(Window owner, string source, string message)
    {
        var dialog = new ConfirmSourceFailureDialog();
        dialog.HeadingText.Text = $"{source} did not respond";
        dialog.MessageText.Text = message;
        return dialog.ShowDialog<bool>(owner);
    }
}
