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
            // beta.23: tight picker filter so only the vendor data interface
            // appears in the Chromium picker. Same hardware enumerates the
            // boot keyboard interface (usage=6) as a separate picker entry —
            // a beta.22 user picked that one and got stuck in a consent loop
            // because WebHID strips writeable reports from protected
            // keyboard collections. See KeyboardManager.TryGen2WebHidDetection
            // for the beta.21 vs beta.22 topology comparison.
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
