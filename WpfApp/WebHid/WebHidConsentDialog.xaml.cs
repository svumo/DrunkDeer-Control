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
        StatusText.Text = "Opening picker…";

        try
        {
            var ok = await _transport.RequestPermissionAsync(_vendorId);
            if (!ok)
            {
                StatusText.Text = "No device chosen. You can try again or cancel.";
                _inFlight = false;
                ContinueButton.IsEnabled = true;
                CancelButton.IsEnabled = true;
                return;
            }

            StatusText.Text = "Connected. Setting up…";
            await _keyboardManager.OnWebHidConsentGrantedAsync();
            DialogResult = true;
            Close();
        }
        catch (System.Exception ex)
        {
            DebugLogger.Log($"WebHidConsentDialog.ContinueButton_Click: {ex.GetType().Name}: {ex.Message}");
            StatusText.Text = "Something went wrong. " + ex.Message;
            _inFlight = false;
            ContinueButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
