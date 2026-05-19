using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace WpfApp.Components;

public partial class InstallDialog : Window
{
    public enum Result
    {
        Cancelled,        // User dismissed (X button or window close).
        Install,          // First-launch: install to canonical.
        OpenInstalled,    // Already-installed: open the existing canonical.
        RunPortable,      // Skip install / run this version anyway.
    }

    private Result resultValue = Result.Cancelled;
    private DispatcherTimer? autoSelectTimer;
    private int autoSelectRemaining;
    private string autoSelectActionLabel = "Auto";

    private InstallDialog()
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
        Close();
    }

    private void SecondaryButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    /// <summary>
    /// Shown the first time we run from a non-canonical path with no
    /// existing install. Returns Install or RunPortable.
    /// </summary>
    public static Result ShowFirstLaunch(Version myVersion, string currentPath, string canonicalPath)
    {
        var dlg = new InstallDialog();
        dlg.HeaderTitle.Text = "Install DrunkDeer Control?";
        dlg.HeaderSubtitle.Text =
            "Install to your user profile to enable proper auto-updates and keep things tidy if you have multiple downloads. " +
            "Profiles and settings stay where they are.";

        dlg.Row1Label.Text = "Running from";
        dlg.Row1Value.Text = ShortenHomePath(currentPath);

        dlg.Row2Label.Text = "Install to";
        dlg.Row2Value.Text = ShortenHomePath(canonicalPath);

        dlg.PrimaryButton.Content = "Install";
        dlg.SecondaryButton.Content = "Just run once";

        dlg.PrimaryButton.Click += (_, _) => dlg.resultValue = Result.Install;
        dlg.SecondaryButton.Click += (_, _) => dlg.resultValue = Result.RunPortable;

        dlg.ShowDialog();
        return dlg.resultValue;
    }

    /// <summary>
    /// Shown when an older copy of the exe is launched and a newer
    /// canonical install already exists. Default action (autoselect after
    /// 3 seconds) is to redirect to the installed copy.
    /// </summary>
    public static Result ShowOpenInstalled(Version myVersion, Version installedVersion, string canonicalPath)
    {
        var dlg = new InstallDialog();
        dlg.HeaderTitle.Text = $"DrunkDeer Control v{installedVersion.ToString(3)} is already installed";
        dlg.HeaderSubtitle.Text = myVersion < installedVersion
            ? $"You opened an older copy (v{myVersion.ToString(3)}). Open the installed version instead?"
            : "Open the installed version instead?";

        dlg.Row1Label.Text = "Installed version";
        dlg.Row1Value.Text = $"v{installedVersion.ToString(3)}";

        dlg.Row2Label.Text = "This copy";
        dlg.Row2Value.Text = $"v{myVersion.ToString(3)}";

        dlg.PrimaryButton.Content = "Open installed";
        dlg.SecondaryButton.Content = "Run this version";

        dlg.PrimaryButton.Click += (_, _) => dlg.resultValue = Result.OpenInstalled;
        dlg.SecondaryButton.Click += (_, _) => dlg.resultValue = Result.RunPortable;

        // Auto-select the safe default (Open installed) after a grace period
        // so the user isn't blocked at a dialog if they double-clicked an old
        // shortcut and walked away. The countdown pauses while the mouse is
        // over the dialog, so it never yanks itself away mid-read.
        dlg.StartCountdown(seconds: 10, expireResult: Result.OpenInstalled,
            actionLabel: "Opening installed version");

        dlg.ShowDialog();
        return dlg.resultValue;
    }

    private void StartCountdown(int seconds, Result expireResult, string actionLabel)
    {
        autoSelectRemaining = seconds;
        autoSelectActionLabel = actionLabel;
        CountdownText.Text = $"{actionLabel} in {seconds}s…";
        autoSelectTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        autoSelectTimer.Tick += (_, _) =>
        {
            // Pause (don't decrement) while the mouse is over the dialog —
            // the user is reading it, not ignoring it. Resumes from where it
            // left off once the cursor moves away.
            if (IsMouseOver)
            {
                CountdownText.Text = $"{autoSelectActionLabel} paused — move mouse away to resume";
                return;
            }

            autoSelectRemaining--;
            if (autoSelectRemaining <= 0)
            {
                StopCountdown();
                resultValue = expireResult;
                Close();
            }
            else
            {
                CountdownText.Text = $"{autoSelectActionLabel} in {autoSelectRemaining}s…";
            }
        };
        autoSelectTimer.Start();
        // A deliberate click or keypress cancels the autoselect entirely —
        // once the user has acted, never auto-act over them.
        PreviewMouseDown += (_, _) => StopCountdown();
        PreviewKeyDown += (_, _) => StopCountdown();
    }

    private void StopCountdown()
    {
        autoSelectTimer?.Stop();
        autoSelectTimer = null;
        CountdownText.Text = "";
    }

    /// <summary>
    /// Replaces the user-profile prefix with ~ so the path fits in the
    /// dialog's narrow value column without truncation in the common case.
    /// </summary>
    private static string ShortenHomePath(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home) && path.StartsWith(home, StringComparison.OrdinalIgnoreCase))
            return "~" + path.Substring(home.Length);
        return path;
    }
}
