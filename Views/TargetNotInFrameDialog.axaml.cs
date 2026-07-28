using System.Threading.Tasks;
using Avalonia.Controls;

namespace VariLab.Views;

public partial class TargetNotInFrameDialog : Window
{
    public TargetNotInFrameDialog()
    {
        InitializeComponent();
        OkButton.Click += (_, _) => Close();
    }

    /// <summary>
    /// Shows a blocking, OK-only alert that the resolved target position doesn't project onto
    /// this frame's plate solution. There is no "continue anyway" — unlike a failed catalog
    /// query, a target that isn't in the frame guarantees zero comp-star candidates downstream,
    /// so continuing would just waste the full Gaia/APASS/VSP/GSPC pipeline before failing.
    /// </summary>
    public static Task ShowAsync(
        Window owner, double targetRa, double targetDec, double frameRa, double frameDec,
        double sepDeg, double fovArcmin)
    {
        var dialog = new TargetNotInFrameDialog();
        dialog.MessageText.Text =
            $"Target resolved to RA={targetRa:F4}° Dec={targetDec:F4}°, but this frame's plate " +
            $"solution is centered near RA={frameRa:F4}° Dec={frameDec:F4}° — roughly {sepDeg:F1}° " +
            $"away (this frame's FOV is only {fovArcmin:F1}').";
        return dialog.ShowDialog(owner);
    }
}
