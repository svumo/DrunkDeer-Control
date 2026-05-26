using System.Windows;
using System.Windows.Input;
using Driver;

namespace WpfApp.WebHid;

// First-launch UX for gen-2 OEM keyboards that require WebHID consent.
// Explains what's about to happen, then drives WebHidTransport through
// the picker on user confirmation. On success, signals KeyboardManager
// to re-scan (so the keyboard goes from "not detected" to fully connected
// without needing an app restart).
public partial class WebHidConsentDialog : Window
{
    private readonly IGen2WebHidTransport _transport;
    private readonly KeyboardManager _keyboardManager;
    private readonly int _vendorId;
    private bool _inFlight;

    public WebHidConsentDialog(IGen2WebHidTransport transport, KeyboardManager keyboardManager, int vendorId)
    {
        InitializeComponent();
        _transport = transport;
        _keyboardManager = keyboardManager;
        _vendorId = vendorId;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private async void ContinueButton_Click(object sender, RoutedEventArgs e)
    {
        if (_inFlight) return;
        _inFlight = true;

        // Beta.14 + .15 both shipped with the consent dialog staying open
        // while the WebView2 picker host was made visible. Both betas'
        // user logs proved the picker host WAS becoming Visible — but
        // user screenshots proved it never actually drew above the consent
        // dialog regardless of which one had Topmost set. WPF modal
        // dialogs override Topmost on unowned peer windows; the only
        // reliable Z-order win is via Owner, which we can't set after
        // the WebView2 control's HWND has been attached.
        //
        // beta.16 fix: close the consent dialog immediately on Continue
        // and hand off entirely to the picker host. The host has its own
        // perfectly serviceable UX (header "Connect your keyboard",
        // status text, "Show device picker" button, success/error states)
        // — the consent dialog's job was just to introduce what's coming.
        //
        // The await on RequestPermissionAsync continues running after
        // Close() returns control; we just can't touch this.* UI elements
        // anymore (StatusText, IsEnabled etc.) so all post-await
        // observable state lives in the picker host's embedded HTML.
        DialogResult = true;
        Close();

        try
        {
            // beta.26: tight picker filter on the vendor data interface
            // (usagePage=1, usage=0). beta.25 proved that even with mi_01
            // untouched on the C# side, Chrome WebHID on this user's
            // machine returns a merged 5-collection device WITHOUT the
            // vendor interface anywhere in the list — picking it can
            // never work. Forcing the filter means:
            //   - if Chrome exposes mi_01 anywhere (as its own device OR
            //     inside a merged device's collections), the picker
            //     shows that entry and the user picks the right thing
            //     by construction;
            //   - if Chrome doesn't expose mi_01 at all (which we now
            //     suspect, post-beta.25), the picker is empty and the
            //     user knows immediately — and we know the path forward
            //     isn't "fix our code" but "figure out why Chrome WebHID
            //     stopped enumerating mi_01 on this hardware" (likely a
            //     WebView2 Runtime update between 17:43 beta.21 and
            //     19:00 beta.22 on 2026-05-25).
            const int kVendorUsagePage = 0x0001; // Generic Desktop
            const int kVendorUsage     = 0x0000; // Undefined (vendor data interface)
            var ok = await _transport.RequestPermissionAsync(_vendorId, kVendorUsagePage, kVendorUsage);
            if (ok)
            {
                await _keyboardManager.OnWebHidConsentGrantedAsync();
                DebugLogger.Log("WebHidConsentDialog: picker resolved ok, keyboard rescan triggered");
            }
            else
            {
                DebugLogger.Log("WebHidConsentDialog: picker resolved cancelled/failed — user can plug-replug to retry");
            }
        }
        catch (System.Exception ex)
        {
            DebugLogger.Log($"WebHidConsentDialog.ContinueButton_Click (post-close picker flow): {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
