using System.Diagnostics;
using System.Windows;
using System.Windows.Input;

namespace WpfApp.Components;

public partial class FirmwareTooOldDialog : Window
{
    public enum Result
    {
        Cancelled,         // User dismissed (X button or window close).
        GetFirmware,       // Opens the official firmware download page.
        ContinueAnyway,    // Acknowledged — modal won't re-fire for this fw.
    }

    private const string FirmwareDownloadUrl = "https://drunkdeer.keybord.net.cn/drunk/index.html";

    private Result resultValue = Result.Cancelled;

    private FirmwareTooOldDialog()
    {
        InitializeComponent();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        resultValue = Result.Cancelled;
        Close();
    }

    private void PrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        resultValue = Result.GetFirmware;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = FirmwareDownloadUrl,
                UseShellExecute = true,
            });
        }
        catch
        {
            // Browser launch errors are non-fatal — the user can still
            // dismiss; we just don't open the page for them.
        }
        Close();
    }

    private void SecondaryButton_Click(object sender, RoutedEventArgs e)
    {
        resultValue = Result.ContinueAnyway;
        Close();
    }

    public static Result Show(string modelLabel, string targetFwHex, Window? owner = null)
    {
        var dlg = new FirmwareTooOldDialog { Owner = owner };
        dlg.ModelLabel.Text = modelLabel;
        dlg.TargetFw.Text = $"{targetFwHex} or newer";
        dlg.ShowDialog();
        return dlg.resultValue;
    }
}
