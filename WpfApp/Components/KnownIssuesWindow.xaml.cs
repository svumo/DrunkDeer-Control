using System.Windows;
using System.Windows.Input;

namespace WpfApp.Components;

public partial class KnownIssuesWindow : Window
{
    public KnownIssuesWindow()
    {
        InitializeComponent();
    }

    protected override void OnSourceInitialized(System.EventArgs e)
    {
        base.OnSourceInitialized(e);
        AcrylicHelper.EnableAcrylic(this, 0xC0281A1A);
    }

    private void TitleBar_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = e.Uri.ToString(),
            UseShellExecute = true,
        });
        e.Handled = true;
    }
}
