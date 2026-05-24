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
        ContinueButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        StatusText.Text = "Opening picker… (a small window will appear on top — click the button in it)";

        try
        {
            var ok = await _transport.RequestPermissionAsync(_vendorId);
            // The dialog can be closed by the user (X button) while
            // RequestPermissionAsync is awaiting. Touching this.* properties
            // after the underlying Window has been closed throws
            // InvalidOperationException ("Cannot set Visibility..."), which
            // bubbles up to App.OnDispatcherUnhandledException and shows the
            // generic error popup. Guard every post-await UI touch.
            if (!ok)
            {
                TrySetStatus("No device chosen. You can try again or cancel.");
                TryReenableButtons();
                _inFlight = false;
                return;
            }

            TrySetStatus("Connected. Setting up…");
            await _keyboardManager.OnWebHidConsentGrantedAsync();
            TryCloseWithResult(true);
        }
        catch (System.Exception ex)
        {
            DebugLogger.Log($"WebHidConsentDialog.ContinueButton_Click: {ex.GetType().Name}: {ex.Message}");
            TrySetStatus("Something went wrong. " + ex.Message);
            TryReenableButtons();
            _inFlight = false;
        }
    }

    private void TrySetStatus(string text)
    {
        try { if (IsLoaded && StatusText is not null) StatusText.Text = text; }
        catch (System.Exception ex) { DebugLogger.Log($"WebHidConsentDialog.TrySetStatus: {ex.GetType().Name}: {ex.Message}"); }
    }

    private void TryReenableButtons()
    {
        try
        {
            if (IsLoaded)
            {
                if (ContinueButton is not null) ContinueButton.IsEnabled = true;
                if (CancelButton is not null) CancelButton.IsEnabled = true;
            }
        }
        catch (System.Exception ex) { DebugLogger.Log($"WebHidConsentDialog.TryReenableButtons: {ex.GetType().Name}: {ex.Message}"); }
    }

    private void TryCloseWithResult(bool result)
    {
        try { DialogResult = result; Close(); }
        catch (System.Exception ex) { DebugLogger.Log($"WebHidConsentDialog.TryCloseWithResult: {ex.GetType().Name}: {ex.Message}"); }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
