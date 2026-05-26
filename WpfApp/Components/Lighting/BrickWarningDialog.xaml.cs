using System.Windows;
using System.Windows.Input;

namespace WpfApp.Components.Lighting;

// Design-system-matched confirmation dialog for RGB writes. Two static
// factory methods drive the two surfaces:
//
//   ShowFirstSyncWarning      → preset-mode first-sync (replaces the older
//                               WinForms MessageBox path in LightingView)
//   ShowCustomModeWarning     → per-key custom-mode first-sync (the
//                               unverified-on-hardware path)
//
// Both return true if the user accepted the risk, false otherwise. Mirrors
// the chrome of InstallDialog and ErrorWindow — glass card, draggable
// title bar, IsDefault primary action, IsCancel secondary, Esc to abort.
public partial class BrickWarningDialog : System.Windows.Window
{
    private bool result;

    public BrickWarningDialog()
    {
        InitializeComponent();
    }

    public static bool ShowFirstSyncWarning(System.Windows.Window? owner)
    {
        var dlg = new BrickWarningDialog
        {
            Owner = owner,
        };
        dlg.TitleBarText.Text = "RGB BRICK SAFETY";
        dlg.HeaderTitle.Text = "First-time RGB sync";
        dlg.HeaderSubtitle.Text =
            "About to send the first RGB packet to this keyboard. The values you've chosen sit "
            + "inside the safe range, but treat this as a one-way door.";

        dlg.Row1Label.Text = "What is being sent";
        dlg.Row1Body.Text =
            "A single 0xAE/0x01 mode-select packet — preset, brightness, speed. No per-key colour data.";

        dlg.Row2Label.Text = "Brick risk";
        dlg.Row2Body.Text =
            "Sending an invalid RGB packet can soft-brick A75 Pro hardware: the keyboard enters "
            + "a boot loop that's only recoverable by spamming a known-good packet during the loop.";

        dlg.Row3Label.Text = "Recovery procedure";
        dlg.Row3Body.Text =
            "docs/rgb-protocol.md → \"Soft-brick recovery\". Outline: hold the Lights-off "
            + "packet on a tight retry while plugging the keyboard back in.";

        dlg.ConfirmButton.Content = "Continue";
        return dlg.ShowDialogAndReturn();
    }

    public static bool ShowCustomModeWarning(System.Windows.Window? owner)
    {
        var dlg = new BrickWarningDialog
        {
            Owner = owner,
        };
        dlg.TitleBarText.Text = "CUSTOM LIGHTING — FIRST SYNC";
        dlg.HeaderTitle.Text = "Per-key colours are unverified on hardware";
        dlg.HeaderSubtitle.Text =
            "About to send a multi-packet per-key colour stream. The wire format matches what the "
            + "official driver emits, but the A75 Pro per-key path has not been confirmed on real "
            + "hardware. The first send is the first hardware test.";

        dlg.Row1Label.Text = "What is being sent";
        dlg.Row1Body.Text =
            "Six or seven 0xAE/0x01/mode=0x13 packets carrying [0x80+layoutIndex, R, G, B] entries "
            + "for every visible key on the connected model. Brightness clamps to 0–9; layout "
            + "indices clamp to 0–127; RGB bytes are unchecked but byte-typed at the API boundary.";

        dlg.Row2Label.Text = "Provenance";
        dlg.Row2Body.Text =
            "Wire format byte-traced against the official Antler driver bundle "
            + "(sendTurboLedModeData, JS offset 6069254). Per-key layout indices match the model's "
            + "LayoutKey.KeyIndex 1:1 — confirmed against getA75Pro() at JS offset 283371.";

        dlg.Row3Label.Text = "Brick risk + recovery";
        dlg.Row3Body.Text =
            "Same soft-brick boot-loop as preset writes (docs/rgb-protocol.md). Recovery is to "
            + "spam a known-good Lights-off packet while the keyboard is in the loop. Lights-off "
            + "button in this view does exactly that — keep it accessible.";

        dlg.FooterHint.Text = "Asks once. Acknowledged status persists in Settings.";
        dlg.ConfirmButton.Content = "I accept the risk";
        return dlg.ShowDialogAndReturn();
    }

    private bool ShowDialogAndReturn()
    {
        ShowDialog();
        return result;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        result = false;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        result = false;
        Close();
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        result = true;
        Close();
    }
}
