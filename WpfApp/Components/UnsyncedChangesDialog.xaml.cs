using System.Windows;
using System.Windows.Input;

namespace WpfApp.Components;

public partial class UnsyncedChangesDialog : Window
{
    public enum Result
    {
        Stay,            // User dismissed / clicked Stay / Esc / X.
        PushAndSwitch,   // Push changes to keyboard, then switch profile.
        DiscardAndSwitch // Revert to last-pushed snapshot, then switch.
    }

    private Result resultValue = Result.Stay;

    private UnsyncedChangesDialog()
    {
        InitializeComponent();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        resultValue = Result.Stay;
        Close();
    }

    private void StayButton_Click(object sender, RoutedEventArgs e)
    {
        resultValue = Result.Stay;
        Close();
    }

    private void DiscardButton_Click(object sender, RoutedEventArgs e)
    {
        resultValue = Result.DiscardAndSwitch;
        Close();
    }

    private void PushButton_Click(object sender, RoutedEventArgs e)
    {
        resultValue = Result.PushAndSwitch;
        Close();
    }

    public static Result Show(Window? owner, string fromProfileName, string toProfileName)
    {
        var dlg = new UnsyncedChangesDialog();
        if (owner is not null) dlg.Owner = owner;
        dlg.HeaderSubtitle.Text =
            $"\"{fromProfileName}\" has changes that haven't been pushed to the keyboard yet. " +
            $"Push them before switching to \"{toProfileName}\", or discard them and start fresh?";
        dlg.ShowDialog();
        return dlg.resultValue;
    }
}
