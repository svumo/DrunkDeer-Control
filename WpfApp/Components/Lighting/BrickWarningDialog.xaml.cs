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
        dlg.TitleBarText.Text = "RGB FIRST SYNC";
        dlg.HeaderTitle.Text = "First-time RGB sync";
        dlg.HeaderSubtitle.Text =
            "About to send the first RGB packet to this keyboard. Preset mode has been "
            + "validated on real A75 Pro hardware — this is a routine confirmation, asked once.";

        dlg.Row1Label.Text = "What is being sent";
        dlg.Row1Body.Text =
            "A single 0xAE/0x01 mode-select packet — preset, brightness, speed. No per-key colour data.";

        dlg.Row2Label.Text = "Safety";
        dlg.Row2Body.Text =
            "Brightness is hard-clamped to 0–9 at every layer (UI, ViewModel, packet builder); "
            + "the firmware's brick boundary sits above that. Wire format byte-matches the "
            + "official Antler driver.";

        dlg.Row3Label.Text = "If something goes wrong (unlikely)";
        dlg.Row3Body.Text =
            "Soft-brick recovery is documented in docs/rgb-protocol.md. Outline: hold the "
            + "Lights-off packet on a tight retry while plugging the keyboard back in — the "
            + "Lights-off button in this view does exactly that.";

        dlg.ConfirmButton.Content = "Continue";
        return dlg.ShowDialogAndReturn();
    }

    // #6: remap / Last Win / Release Dual-Trigger pair writes on a model
    // where they're untested (A75 Ultra / Master). Asked once per keyboard.
    public static bool ShowUntestedRemapPairWarning(System.Windows.Window? owner, string modelLabel)
    {
        var dlg = new BrickWarningDialog
        {
            Owner = owner,
        };
        dlg.TitleBarText.Text = "UNTESTED ON THIS KEYBOARD";
        dlg.HeaderTitle.Text = "Send remaps and key pairs?";
        dlg.HeaderSubtitle.Text =
            $"Remaps, Last Win pairs and Release Dual-Trigger pairs haven't been tested on {modelLabel}. "
            + "One user's W, A, S and D stopped working after a sync until they unplugged the keyboard.";

        dlg.Row1Label.Text = "If you continue";
        dlg.Row1Body.Text =
            "This sync and later ones also send your key map and pair table to this keyboard.";

        dlg.Row2Label.Text = "If you don't";
        dlg.Row2Body.Text =
            "Actuation, Rapid Trigger and mode switches still sync. The key map and pairs already on "
            + "the keyboard stay as they are.";

        dlg.Row3Label.Text = "If keys stop working";
        dlg.Row3Body.Text =
            "Unplug the keyboard and plug it back in. Then reset the key map from the official driver, "
            + "and please report it on GitHub with your firmware version.";

        dlg.ConfirmButton.Content = "Send them";
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

    public static bool ShowUnverifiedModeWarning(System.Windows.Window? owner)
    {
        var dlg = new BrickWarningDialog
        {
            Owner = owner,
        };
        dlg.TitleBarText.Text = "UNVERIFIED EFFECT — FIRST USE";
        dlg.HeaderTitle.Text = "About to send an effect we haven't hardware-tested";
        dlg.HeaderSubtitle.Text =
            "The full 19-effect catalog comes from the official driver's JS bundle so it should be "
            + "safe, but only a handful (Off / Marquee / Wave Spectrum / Breath / Ripple) have been "
            + "confirmed on real A75 Pro hardware. The rest are first-of-their-kind tests.";

        dlg.Row1Label.Text = "What is being sent";
        dlg.Row1Body.Text =
            "A single 0xAE/0x01 mode-select packet with a mode byte from the gen-1 effect catalog. "
            + "Same packet shape and wire path as the verified effects — only the mode byte differs.";

        dlg.Row2Label.Text = "Provenance";
        dlg.Row2Body.Text =
            "Catalog extracted from the official Antler driver's ddeerA75ProColour class "
            + "(tools/captures/antler-extracted/index.CJWCGjvj.js). 21 entries; wire codes via "
            + "max(0, idx-2) per JS source.";

        dlg.Row3Label.Text = "Brick risk + recovery";
        dlg.Row3Body.Text =
            "Same soft-brick boot-loop as any RGB write (docs/rgb-protocol.md). Recovery is to "
            + "spam the Lights-off packet while the keyboard is in the loop. Lights-off button in "
            + "this view does exactly that — keep it accessible.";

        dlg.FooterHint.Text = "Asks once across all unverified effects. Acknowledged status persists in Settings.";
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
