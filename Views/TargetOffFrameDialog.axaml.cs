using System.Threading.Tasks;
using Avalonia.Controls;

namespace VariLab.Views;

public partial class TargetOffFrameDialog : Window
{
    public TargetOffFrameDialog()
    {
        InitializeComponent();
        OkButton.Click += (_, _) => Close();
    }

    /// <summary>
    /// Shows a blocking, OK-only alert that the target's resolved position, while close enough
    /// to this frame's pointing to pass the coarser center-separation check, still projects to a
    /// pixel outside the frame's real bounds — distinct from <see cref="TargetNotInFrameDialog"/>,
    /// which catches a wildly wrong pointing (e.g. a stale FITS OBJECT header). No "continue
    /// anyway" — a target with no real pixel data at its position guarantees a 0-usable-frame
    /// run downstream, so continuing would just waste the full Gaia/APASS/VSP/GSPC pipeline
    /// before failing.
    /// </summary>
    public static Task ShowAsync(
        Window owner, double targetRa, double targetDec, int pixelX, int pixelY, int naxis1, int naxis2)
    {
        var dialog = new TargetOffFrameDialog();
        dialog.MessageText.Text =
            $"Target resolved to RA={targetRa:F4}° Dec={targetDec:F4}°, projecting to pixel " +
            $"({pixelX},{pixelY}) — outside this {naxis1}×{naxis2} frame's real bounds.";
        return dialog.ShowDialog(owner);
    }
}
