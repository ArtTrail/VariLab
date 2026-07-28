using System.Threading.Tasks;
using Avalonia.Controls;

namespace VariLab.Views;

public partial class PlateSolveRequiredDialog : Window
{
    public PlateSolveRequiredDialog()
    {
        InitializeComponent();
        OkButton.Click += (_, _) => Close();
    }

    /// <summary>
    /// Shows a blocking, OK-only alert that most of the frames in this directory have no WCS
    /// solution. There is no "continue anyway" — unlike a failed catalog query, a dataset that
    /// isn't plate-solved has nothing for the pipeline to work with.
    /// </summary>
    public static Task ShowAsync(Window owner, int withWcs, int total)
    {
        var dialog = new PlateSolveRequiredDialog();
        dialog.MessageText.Text =
            $"Only {withWcs} of {total} frames in this directory have a WCS solution. " +
            "Plate-solve the rest before running VariLab on this dataset.";
        return dialog.ShowDialog(owner);
    }
}
