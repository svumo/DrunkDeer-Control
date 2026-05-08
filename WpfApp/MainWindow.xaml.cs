using Driver;
using System.Collections.Specialized;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WpfApp.Components;
using WpfApp.Extensions;
using WpfApp.Hooks;
using WpfApp.Profile;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace WpfApp
{
    public partial class MainWindow : Window
    {
        public static bool ShouldStartMinimized { get; set; } = false;
        private readonly Dictionary<int, KeyHandler> handlers = [];
        private readonly Dictionary<ProfileItem, KeyHandler> directHandlers = [];
        private readonly ProfileManager ProfileManager;
        private readonly WinEventHook WinEventHook;
        private readonly KeyboardManager KeyboardManager;
        private ProcessSelector? processSelectorWindow;
        private ProfileItem? selectedProfile;
        private bool suppressToggleEvents;
        private bool isRecordingDirectKeybind = false;

        public MainWindow(ProfileManager profileManager, WinEventHook winEventHook, TrayIcon icon, KeyboardManager keyboardManager, Settings settings)
        {
            this.settings = settings;
            WinEventHook = winEventHook;
            ProfileManager = profileManager;
            KeyboardManager = keyboardManager;
            InitializeComponent();

            // Build/instance fingerprint — confirms which binary is actually running.
            // If you don't see this line in debug.log on launch, you are running a stale build.
            try
            {
                var proc = System.Diagnostics.Process.GetCurrentProcess();
                var exePath = proc.MainModule?.FileName ?? "<unknown>";
                var buildTime = System.IO.File.Exists(exePath)
                    ? System.IO.File.GetLastWriteTime(exePath).ToString("yyyy-MM-dd HH:mm:ss")
                    : "<n/a>";
                DebugLogger.Log($"=== MainWindow ctor PID={proc.Id} EXE={exePath} BUILT={buildTime} ===");
            }
            catch (Exception ex) { DebugLogger.Log($"Build stamp failed: {ex.Message}"); }

            icon.DoubleClick = () => Restore();
            icon.AppShouldClose = () => { _forceClose = true; Close(); };
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // First thing: clean up any .bak left behind by a previous in-app
            // update. The old running process couldn't delete itself, so we
            // do it now that we're a fresh process.
            AutoUpdater.CleanupBakIfPresent();

            DiscoverProfiles();
            RegisterKeyHandler();
            UpdateQuickSwitchLabel();

            StartupShortcutHelper.SelfHealStartupRegistration();
            StartOnWindowsStartupToggle.IsChecked = StartupShortcutHelper.StartupFileExists();
            StartOnWindowsStartupToggle.Click += OnCheckChanged;

            // Initialize the anonymous-usage-stats toggle from persisted settings.
            // Default is true; UsageStatsToggle_Changed writes any user flip back.
            UsageStatsToggle.IsChecked = settings.UsageStatsEnabled;

            // Stamp the version footer immediately and kick off the update check.
            // The check runs on the thread pool with a 5s timeout and never throws;
            // ApplyUpdateInfo handles success/failure uniformly.
            VersionFooter.Text = $"Version {UpdateChecker.CurrentVersion.ToString(3)}";
            TriggerUpdateCheck();

            WinEventHook.WinEventHookHandler += OnWinEventHook;

            KeyboardManager.ConnectedKeyboardChanged += OnConnectedKeyboardChanged;
            OnConnectedKeyboardChanged(KeyboardManager.KeyboardWithSpecs);

            // Fire-and-forget anonymous usage heartbeat. No-ops if the user has
            // turned the toggle off or we already pinged within 24h. Never
            // throws — see UsageReporter.cs.
            _ = UsageReporter.ReportIfDueAsync(settings, KeyboardManager.KeyboardWithSpecs?.Specs);
        }

        // ===== Update banner state machine =====
        //
        // The banner cycles through states driven by user clicks and the
        // AutoUpdater task. Visibility / button labels / subtitle / progress
        // bar / icon are all set in one place (SetUpdateState) so the UI
        // can never drift out of sync with the underlying state.

        private enum UpdateUiState
        {
            Hidden,         // No update available (or check hasn't run yet) — banner collapsed.
            Available,      // Newer release exists. [Install] / —
            Downloading,    // Streaming the new exe. [Cancel] / progress bar visible.
            Ready,          // Downloaded; counting down to restart. [Restart now] / [Cancel]
            Installing,     // Swap in progress. (no buttons; app is exiting)
            Failed,         // Download or swap blew up. [Retry] / [Open in browser]
            WriteProtected, // Exe lives somewhere we can't write — fall back to browser only. [Open in browser] / —
        }

        private UpdateUiState updateState = UpdateUiState.Hidden;
        private UpdateInfo? pendingUpdateInfo;
        private string? pendingDownloadUrl;
        private CancellationTokenSource? downloadCts;
        private DispatcherTimer? readyCountdownTimer;
        private int readyCountdownRemaining;
        private string? lastFailureReason;

        // Throttle for re-checks fired by opening Options. Keeps GitHub
        // happy if the user opens/closes Options repeatedly.
        private DateTime lastUpdateCheck = DateTime.MinValue;
        private static readonly TimeSpan UpdateCheckMinInterval = TimeSpan.FromMinutes(5);

        private void TriggerUpdateCheck()
        {
            lastUpdateCheck = DateTime.Now;
            _ = Task.Run(async () =>
            {
                var info = await UpdateChecker.CheckAsync().ConfigureAwait(false);
                _ = Dispatcher.BeginInvoke(() => ApplyUpdateInfo(info));
            });
        }

        private void ApplyUpdateInfo(UpdateInfo? info)
        {
            var current = UpdateChecker.CurrentVersion.ToString(3);
            if (info is null)
            {
                VersionFooter.Text = $"Version {current}";
                return;
            }

            if (info.IsNewer)
            {
                pendingUpdateInfo = info;
                pendingDownloadUrl = info.DownloadUrl;
                VersionFooter.Text = $"Version {current} · {info.LatestTag} available";

                if (AutoUpdater.CanWriteToInstallDir(out var reason))
                {
                    SetUpdateState(UpdateUiState.Available);
                }
                else
                {
                    DebugLogger.Log($"ApplyUpdateInfo: install dir not writable ({reason}) — falling back to browser-only");
                    SetUpdateState(UpdateUiState.WriteProtected);
                }
            }
            else
            {
                VersionFooter.Text = $"Version {current} · Up to date";
            }
        }

        // Toggles the title-bar gear into a bell + orange notification dot
        // when there's something to act on inside Options. Called from
        // SetUpdateState whenever the banner-visible states change.
        private void SetOptionsNotification(bool visible)
        {
            if (OptionsNotificationDot is null || OptionsButtonIcon is null) return;
            if (visible)
            {
                OptionsButtonIcon.Kind = MaterialDesignThemes.Wpf.PackIconKind.BellRing;
                OptionsButtonIcon.Foreground = (SolidColorBrush)FindResource("DdFg1");
                OptionsNotificationDot.Visibility = Visibility.Visible;
                OptionsButton.ToolTip = "Update available — open Settings";
            }
            else
            {
                OptionsButtonIcon.Kind = MaterialDesignThemes.Wpf.PackIconKind.Tune;
                OptionsButtonIcon.Foreground = (SolidColorBrush)FindResource("DdFg3");
                OptionsNotificationDot.Visibility = Visibility.Collapsed;
                OptionsButton.ToolTip = "Settings & Keybinds";
            }
        }

        private void SetUpdateState(UpdateUiState newState)
        {
            updateState = newState;
            var current = UpdateChecker.CurrentVersion.ToString(3);
            var info = pendingUpdateInfo;

            // Title-bar notification badge follows the banner: visible whenever
            // there's something the user might want to act on inside Options
            // (available / failed / write-protected). Hidden during in-progress
            // states (downloading / ready / installing) since the user can already
            // see them in the open overlay, and hidden when there's nothing.
            SetOptionsNotification(newState is UpdateUiState.Available
                                     or UpdateUiState.Failed
                                     or UpdateUiState.WriteProtected);

            switch (newState)
            {
                case UpdateUiState.Hidden:
                    UpdateBanner.Visibility = Visibility.Collapsed;
                    UpdateProgressBar.Visibility = Visibility.Collapsed;
                    break;

                case UpdateUiState.Available:
                    UpdateBanner.Visibility = Visibility.Visible;
                    UpdateBannerIcon.Kind = MaterialDesignThemes.Wpf.PackIconKind.Download;
                    UpdateBannerIcon.Foreground = (SolidColorBrush)FindResource("DdAccent");
                    UpdateBannerTitle.Text = "Update available";
                    UpdateBannerSubtitle.Text = info is null ? "" : $"v{current}  →  {info.LatestTag}";
                    UpdateProgressBar.Visibility = Visibility.Collapsed;
                    SetButton(UpdatePrimaryButton, "Install", visible: true, primary: true);
                    SetButton(UpdateSecondaryButton, "", visible: false);
                    break;

                case UpdateUiState.Downloading:
                    UpdateBanner.Visibility = Visibility.Visible;
                    UpdateBannerIcon.Kind = MaterialDesignThemes.Wpf.PackIconKind.Download;
                    UpdateBannerIcon.Foreground = (SolidColorBrush)FindResource("DdAccent");
                    UpdateBannerTitle.Text = "Downloading update";
                    UpdateBannerSubtitle.Text = "Starting…";
                    UpdateProgressBar.Visibility = Visibility.Visible;
                    UpdateProgressBar.Value = 0;
                    SetButton(UpdatePrimaryButton, "", visible: false);
                    SetButton(UpdateSecondaryButton, "Cancel", visible: true);
                    break;

                case UpdateUiState.Ready:
                    UpdateBanner.Visibility = Visibility.Visible;
                    UpdateBannerIcon.Kind = MaterialDesignThemes.Wpf.PackIconKind.CheckCircle;
                    UpdateBannerIcon.Foreground = (SolidColorBrush)FindResource("DdSuccess");
                    UpdateBannerTitle.Text = "Update ready";
                    // Subtitle is updated tick-by-tick by the countdown timer.
                    UpdateProgressBar.Visibility = Visibility.Collapsed;
                    SetButton(UpdatePrimaryButton, "Restart now", visible: true, primary: true);
                    SetButton(UpdateSecondaryButton, "Cancel", visible: true);
                    break;

                case UpdateUiState.Installing:
                    UpdateBanner.Visibility = Visibility.Visible;
                    UpdateBannerIcon.Kind = MaterialDesignThemes.Wpf.PackIconKind.Restart;
                    UpdateBannerIcon.Foreground = (SolidColorBrush)FindResource("DdAccent");
                    UpdateBannerTitle.Text = "Restarting…";
                    UpdateBannerSubtitle.Text = "Applying update";
                    UpdateProgressBar.Visibility = Visibility.Collapsed;
                    SetButton(UpdatePrimaryButton, "", visible: false);
                    SetButton(UpdateSecondaryButton, "", visible: false);
                    break;

                case UpdateUiState.Failed:
                    UpdateBanner.Visibility = Visibility.Visible;
                    UpdateBannerIcon.Kind = MaterialDesignThemes.Wpf.PackIconKind.AlertCircle;
                    UpdateBannerIcon.Foreground = (SolidColorBrush)FindResource("DdDanger");
                    UpdateBannerTitle.Text = "Update failed";
                    UpdateBannerSubtitle.Text = lastFailureReason ?? "Unknown error";
                    UpdateProgressBar.Visibility = Visibility.Collapsed;
                    SetButton(UpdatePrimaryButton, "Retry", visible: true, primary: true);
                    SetButton(UpdateSecondaryButton, "Open in browser", visible: true);
                    break;

                case UpdateUiState.WriteProtected:
                    UpdateBanner.Visibility = Visibility.Visible;
                    UpdateBannerIcon.Kind = MaterialDesignThemes.Wpf.PackIconKind.LockOutline;
                    UpdateBannerIcon.Foreground = (SolidColorBrush)FindResource("DdFg3");
                    UpdateBannerTitle.Text = "Update available";
                    UpdateBannerSubtitle.Text = info is null
                        ? "Install location is read-only"
                        : $"v{current} → {info.LatestTag} · open in browser to install";
                    UpdateProgressBar.Visibility = Visibility.Collapsed;
                    SetButton(UpdatePrimaryButton, "Open in browser", visible: true, primary: true);
                    SetButton(UpdateSecondaryButton, "", visible: false);
                    break;
            }
        }

        private static void SetButton(System.Windows.Controls.Button btn, string content, bool visible, bool primary = false)
        {
            btn.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (visible) btn.Content = content;
        }

        private void OnUpdatePrimaryClicked(object sender, RoutedEventArgs e)
        {
            switch (updateState)
            {
                case UpdateUiState.Available:
                case UpdateUiState.Failed:
                    StartDownload();
                    break;
                case UpdateUiState.Ready:
                    StartInstall();
                    break;
                case UpdateUiState.WriteProtected:
                    OpenInBrowser();
                    break;
            }
        }

        private void OnUpdateSecondaryClicked(object sender, RoutedEventArgs e)
        {
            switch (updateState)
            {
                case UpdateUiState.Downloading:
                    downloadCts?.Cancel();
                    break;
                case UpdateUiState.Ready:
                    StopReadyCountdown();
                    SetUpdateState(UpdateUiState.Available);
                    break;
                case UpdateUiState.Failed:
                    OpenInBrowser();
                    break;
            }
        }

        private void StartDownload()
        {
            if (pendingUpdateInfo is null) return;

            // Replace any previous CTS — if this is a Retry, the old one is
            // already cancelled or completed.
            downloadCts?.Dispose();
            downloadCts = new CancellationTokenSource();
            var ct = downloadCts.Token;
            var info = pendingUpdateInfo;

            SetUpdateState(UpdateUiState.Downloading);

            var progress = new Progress<DownloadProgress>(p =>
            {
                if (updateState != UpdateUiState.Downloading) return;
                if (p.Total > 0)
                {
                    var pct = (double)p.Downloaded / p.Total * 100;
                    UpdateProgressBar.Value = pct;
                    UpdateBannerSubtitle.Text =
                        $"{FormatMb(p.Downloaded)} / {FormatMb(p.Total)} · {pct:F0}%";
                }
                else
                {
                    UpdateProgressBar.Value = 0;
                    UpdateBannerSubtitle.Text = $"{FormatMb(p.Downloaded)} downloaded";
                }
            });

            _ = Task.Run(async () =>
            {
                try
                {
                    await AutoUpdater.DownloadAsync(info.DownloadUrl, info.AssetSize, progress, ct).ConfigureAwait(false);
                    _ = Dispatcher.BeginInvoke(() => OnDownloadComplete());
                }
                catch (OperationCanceledException)
                {
                    DebugLogger.Log("StartDownload: cancelled by user");
                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        if (updateState == UpdateUiState.Downloading)
                            SetUpdateState(UpdateUiState.Available);
                    });
                }
                catch (Exception ex)
                {
                    _ = Dispatcher.BeginInvoke(() => FailUpdate($"{ex.GetType().Name}: {ex.Message}"));
                }
            });
        }

        private void OnDownloadComplete()
        {
            SetUpdateState(UpdateUiState.Ready);
            StartReadyCountdown();
        }

        private void StartReadyCountdown()
        {
            const int totalSeconds = 5;
            readyCountdownRemaining = totalSeconds;
            UpdateBannerSubtitle.Text = $"Restarting in {readyCountdownRemaining}s…";

            readyCountdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            readyCountdownTimer.Tick += (_, _) =>
            {
                readyCountdownRemaining--;
                if (readyCountdownRemaining <= 0)
                {
                    StopReadyCountdown();
                    StartInstall();
                }
                else if (updateState == UpdateUiState.Ready)
                {
                    UpdateBannerSubtitle.Text = $"Restarting in {readyCountdownRemaining}s…";
                }
            };
            readyCountdownTimer.Start();
        }

        private void StopReadyCountdown()
        {
            readyCountdownTimer?.Stop();
            readyCountdownTimer = null;
        }

        private void StartInstall()
        {
            StopReadyCountdown();
            SetUpdateState(UpdateUiState.Installing);

            // Run the swap + Process.Start on a background thread so the UI
            // thread stays responsive while the file ops happen — even though
            // they're typically fast (rename + move within the same volume),
            // Windows Defender scans the freshly-moved 169 MB exe before
            // letting Process.Start return, which can take several seconds.
            // The UI otherwise freezes on "Restarting…" with no feedback.
            //
            // Application.Current.Shutdown() must run on the dispatcher thread
            // (it calls VerifyAccess internally), so we marshal it explicitly.
            _ = Task.Run(() =>
            {
                try
                {
                    AutoUpdater.ApplyAndRestart(AutoUpdater.StagedPath);
                }
                catch (Exception ex)
                {
                    _ = Dispatcher.BeginInvoke(() => FailUpdate($"Install failed — {ex.Message}"));
                    return;
                }
                // Swap succeeded and the new process was spawned. Drop ours.
                _ = Dispatcher.BeginInvoke(() => System.Windows.Application.Current.Shutdown());
            });
        }

        private void FailUpdate(string reason)
        {
            lastFailureReason = reason;
            AutoUpdater.CleanupStagedIfPresent();
            SetUpdateState(UpdateUiState.Failed);
        }

        private void OpenInBrowser()
        {
            var url = pendingDownloadUrl ?? UpdateChecker.DirectDownloadUrl;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
                OptionsOverlay.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"OpenInBrowser: failed to launch '{url}' — {ex.Message}");
            }
        }

        private static string FormatMb(long bytes) => $"{bytes / (1024.0 * 1024.0):F1} MB";

        // ===== Custom Title Bar =====

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                WindowState = WindowState == WindowState.Maximized
                    ? WindowState.Normal
                    : WindowState.Maximized;
            }
            else
            {
                DragMove();
            }
        }

        private void OptionsButton_Click(object sender, RoutedEventArgs e)
        {
            var opening = OptionsOverlay.Visibility != Visibility.Visible;
            OptionsOverlay.Visibility = opening ? Visibility.Visible : Visibility.Collapsed;

            // Re-check for updates whenever the user opens Options. Debounced
            // (UpdateCheckMinInterval) so we never hammer GitHub if they
            // open and close repeatedly. Only fires when the banner is in a
            // state that's safe to clobber — leave Downloading/Ready/Installing
            // alone so we don't tear down an in-progress flow.
            if (opening
                && DateTime.Now - lastUpdateCheck > UpdateCheckMinInterval
                && updateState is UpdateUiState.Hidden
                                or UpdateUiState.Available
                                or UpdateUiState.WriteProtected
                                or UpdateUiState.Failed)
            {
                TriggerUpdateCheck();
            }
        }

        private void OptionsOverlay_Dismiss(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            OptionsOverlay.Visibility = Visibility.Collapsed;
        }

        private void OnStarRepoClicked(object sender, RoutedEventArgs e)
        {
            OptionsOverlay.Visibility = Visibility.Collapsed;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://github.com/svumo/DrunkDeer-Control",
                UseShellExecute = true
            });
        }

        private void OnOpenGitHubClicked(object sender, RoutedEventArgs e)
        {
            OptionsOverlay.Visibility = Visibility.Collapsed;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://github.com/svumo/DrunkDeer-Control/issues",
                UseShellExecute = true
            });
        }

        private void OnImportHelpClicked(object sender, MouseButtonEventArgs e)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://github.com/svumo/DrunkDeer-Control/blob/main/Importing-Profiles.md",
                UseShellExecute = true
            });
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            HideToTray();
        }

        private void HideToTray()
        {
            Hide();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // Intercept Alt+F4 and any other OS close — hide to tray instead
            if (!_forceClose)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }
            base.OnClosing(e);
        }

        private bool _forceClose = false;

        // ===== Profile Selection =====

        private void OnProfileSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var addedName = e.AddedItems.Count > 0 && e.AddedItems[0] is ProfileItem ai ? ai.Name : "<none>";
            var removedName = e.RemovedItems.Count > 0 && e.RemovedItems[0] is ProfileItem ri ? ri.Name : "<none>";
            var sel = ProfileListBox.SelectedItem is ProfileItem si ? si.Name : "null";
            var prevField = selectedProfile?.Name ?? "null";
            DebugLogger.Log($"OnProfileSelectionChanged: Added=[{addedName}] Removed=[{removedName}] ListBox.SelectedItem='{sel}' selectedProfile(before)='{prevField}'");

            // Cancel any in-progress keybind recording when the user switches profiles
            if (isRecordingDirectKeybind)
            {
                isRecordingDirectKeybind = false;
                UpdateDirectKeybindLabel();
            }

            // Trust ListBox.SelectedItem, NOT e.AddedItems[0]. During a desync where
            // multiple containers have IsSelected=true, e.AddedItems can report an
            // item that isn't actually the Selector's current SelectedItem — using
            // it would lock selectedProfile to a different profile than the visible
            // highlight, which is exactly the rapid-click bug we keep hitting.
            if (ProfileListBox.SelectedItem is ProfileItem item)
            {
                selectedProfile = item;
                EmptyState.Visibility = Visibility.Collapsed;
                DetailScrollViewer.Visibility = Visibility.Visible;
                UpdateDetailPanel();
                // Clicking the sidebar shows the profile detail — it does NOT activate it.
                // Activation happens via direct keybind, quick-switch, or the Activate button.
            }
            else
            {
                selectedProfile = null;
                EmptyState.Visibility = Visibility.Visible;
                DetailScrollViewer.Visibility = Visibility.Collapsed;
            }
        }

        private void UpdateActivateButton()
        {
            if (ActivateButton is null) return;
            bool isActive = selectedProfile?.IsActiveProfile == true;
            ActivateButton.Content = isActive ? "Profile Activated" : "Activate";
            ActivateButton.Foreground = (SolidColorBrush)FindResource("DdFg1");
            ActivateButton.IsEnabled = !isActive;
        }

        private void UpdateDetailPanel()
        {
            if (selectedProfile is null) return;

            DetailProfileName.Text = selectedProfile.Name;
            DetailProfileSubtitle.Text = selectedProfile.Profile.Showname;

            var keys = selectedProfile.Profile.Keys_Array;
            if (keys.Length > 0)
            {
                var minAp = keys.Min(k => k.Action_Point);
                var maxAp = keys.Max(k => k.Action_Point);
                ActuationRangeText.Text = minAp == maxAp
                    ? $"{minAp:F1}mm"
                    : $"{minAp:F1}mm — {maxAp:F1}mm";
            }
            else
            {
                ActuationRangeText.Text = "No key data";
            }

            RtpStatusText.Text = selectedProfile.Profile.RTP is not null ? "Enabled" : "Disabled";

            RemapStatusText.Text = selectedProfile.RemapProfile is { } remap
                ? remap.Showname
                : "None";

            suppressToggleEvents = true;
            QuickSwitchToggle.IsChecked = selectedProfile.SelectedForQuickSwitch;
            DefaultProfileToggle.IsChecked = selectedProfile.IsDefault;
            suppressToggleEvents = false;

            ProfileNoteTextBox.Text = selectedProfile.Note;
            UpdateActivateButton();
            UpdateDirectKeybindLabel();

            // Force refresh of triggers ItemsControl
            ProcessTriggersPanel.ItemsSource = null;
            ProcessTriggersPanel.ItemsSource = selectedProfile.ProcessTriggers;
        }

        // ===== Connection Status =====

        private void OnConnectedKeyboardChanged(KeyboardWithSpecs? keyboardWithSpecs)
        {
            if (keyboardWithSpecs is { } kws)
            {
                KeyboardStatusText.Text = $"{kws.Keyboard.GetFriendlyName()}  v{kws.Specs.FirmwareVersion}";
                ConnectionDot.Fill = (SolidColorBrush)FindResource("DdSuccess");

            }
            else
            {
                KeyboardStatusText.Text = "No keyboard";
                ConnectionDot.Fill = (SolidColorBrush)FindResource("DdNeutral");

            }
        }

        // ===== Profile Lifecycle =====

        private void ProfileChanged(int index, ProfileItem item)
        {
            DebugLogger.Log($"ProfileChanged(sync): index={index} item='{item.Name}' — queuing UI update");
            // Defer ALL UI updates to a clean dispatcher frame so none of them run
            // while WPF is still mid-way through processing the WM_HOTKEY message.
            // Modifying bound properties on ListBox items inside the hook corrupts
            // WPF's input state and causes the sidebar to freeze on the next click.
            Dispatcher.BeginInvoke(() =>
            {
                DebugLogger.Log($"ProfileChanged(deferred): applying for '{item.Name}', ListBox.SelectedItem='{(ProfileListBox.SelectedItem is ProfileItem lbsi ? lbsi.Name : "null")}'");
                foreach (var p in ProfileManager.Profiles)
                    p.IsActiveProfile = false;
                item.IsActiveProfile = true;
                ActiveProfileText.Text = "Active: " + item.Name;
                ActiveProfileDot.Fill = (SolidColorBrush)FindResource("DdSuccess");
                UpdateActivateButton();
            });
        }

        private void ProfilesChanged(ProfileItem[] changed)
        {
            // ItemsSource is bound to the ObservableCollection once at startup; we
            // rely on INotifyCollectionChanged here. Resetting ItemsSource (=null;
            // =collection;) corrupts Selector's internal selection-tracking state
            // and leaves multiple ListBoxItem containers with IsSelected=True,
            // which silently drops subsequent clicks.
            foreach (var p in changed)
                RegisterDirectHandler(p);
            if (selectedProfile is null && ProfileManager.Profiles.Count > 0)
            {
                ProfileListBox.SelectedIndex = 0;
            }
        }

        private void DiscoverProfiles()
        {
            ProfileManager.CurrentProfileChanged += ProfileChanged;
            ProfileManager.ProfileCollectionChanged += ProfilesChanged;
            ProfileListBox.ItemsSource = ProfileManager.Profiles;
            ProfileManager.DiscoverProfiles();

            // Register any saved direct-access keybinds
            foreach (var p in ProfileManager.Profiles)
                RegisterDirectHandler(p);

            // Fallback selection runs via BeginInvoke so it is queued AFTER any deferred
            // SelectedItem = item posted by ProfileChanged above. That way if a last-used
            // profile was restored, the deferred sync wins and the fallback sees a non-null
            // SelectedItem and skips. If no last-used profile, the fallback selects index 0.
            Dispatcher.BeginInvoke(() =>
            {
                if (ProfileManager.Profiles.Count > 0 && ProfileListBox.SelectedItem is null)
                    ProfileListBox.SelectedIndex = 0;
            });
        }

        private readonly Settings settings;
        private bool isRecordingKeybind = false;

        private void RegisterKeyHandler()
        {
            var windowHandle = new WindowInteropHelper(this).Handle;
            var source = HwndSource.FromHwnd(windowHandle);

            RegisterQuickSwitchHandler(windowHandle, source);
        }

        private void RegisterQuickSwitchHandler(nint windowHandle, HwndSource source)
        {
            // Unregister existing quick-switch handler if present
            if (handlers.TryGetValue(QuickSwitchHandlerKey(), out var old))
            {
                old.Unregister();
                handlers.Remove(QuickSwitchHandlerKey());
            }

            var handler = new KeyHandler(settings.QuickSwitchKey, windowHandle, source,
                settings.QuickSwitchModifiers | KeyHandler.MOD_NOREPEAT)
            {
                Callback = () => Dispatcher.BeginInvoke(ProfileManager.QuickSwitchProfile),
            };
            handlers[handler.GetHashCode()] = handler;
            handler.Register();
            UpdateQuickSwitchLabel();
        }

        private int QuickSwitchHandlerKey()
        {
            // Matches GetHashCode() in KeyHandler: key ^ modifiers ^ hWnd
            var windowHandle = new WindowInteropHelper(this).Handle;
            return settings.QuickSwitchKey ^ (settings.QuickSwitchModifiers | KeyHandler.MOD_NOREPEAT) ^ windowHandle.ToInt32();
        }

        private void UpdateQuickSwitchLabel()
        {
            if (QuickSwitchKeybindLabel is null) return;
            QuickSwitchKeybindLabel.Text = FormatKeybind(settings.QuickSwitchKey, settings.QuickSwitchModifiers);
        }

        private static string FormatKeybind(int vk, int mods)
        {
            var parts = new System.Text.StringBuilder();
            if ((mods & KeyHandler.MOD_CONTROL) != 0) parts.Append("Ctrl+");
            if ((mods & KeyHandler.MOD_ALT) != 0) parts.Append("Alt+");
            if ((mods & KeyHandler.MOD_SHIFT) != 0) parts.Append("Shift+");
            var key = KeyInterop.KeyFromVirtualKey(vk);
            parts.Append(key.ToString());
            return parts.ToString();
        }

        private void StartKeybindRecording()
        {
            isRecordingKeybind = true;
            QuickSwitchKeybindLabel.Text = "Press keys…";
        }

        private void OnKeybindBadgeClicked(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (!isRecordingKeybind) StartKeybindRecording();
        }

        private void OnWindowPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (!isRecordingKeybind && !isRecordingDirectKeybind) return;
            e.Handled = true;

            // Resolve the actual key (Alt combos come through as Key.System)
            var actualKey = e.Key == Key.System ? e.SystemKey : e.Key;

            // Ignore pure modifier presses
            if (actualKey is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
                return;

            // Escape cancels recording; if a keybind was already set it clears it
            if (actualKey == Key.Escape && isRecordingDirectKeybind)
            {
                isRecordingDirectKeybind = false;
                if (selectedProfile is { } item)
                {
                    UnregisterDirectHandler(item);
                    item.DirectSwitchKey = 0;
                    item.DirectSwitchModifiers = 0;
                }
                UpdateDirectKeybindLabel();
                return;
            }

            int mods = 0;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) mods |= KeyHandler.MOD_CONTROL;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) mods |= KeyHandler.MOD_ALT;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) mods |= KeyHandler.MOD_SHIFT;
            int vk = KeyHandler.ToKeycode(actualKey);

            if (isRecordingKeybind)
            {
                isRecordingKeybind = false;
                var windowHandle = new WindowInteropHelper(this).Handle;
                var source = HwndSource.FromHwnd(windowHandle);
                foreach (var h in handlers.Values.ToList()) { h.Unregister(); }
                handlers.Clear();
                settings.QuickSwitchKey = vk;
                settings.QuickSwitchModifiers = mods;
                RegisterQuickSwitchHandler(windowHandle, source);
            }
            else if (isRecordingDirectKeybind && selectedProfile is { } profile)
            {
                isRecordingDirectKeybind = false;
                profile.DirectSwitchKey = vk;
                profile.DirectSwitchModifiers = mods;
                RegisterDirectHandler(profile);
                UpdateDirectKeybindLabel();
            }
        }

        private void OnWinEventHook(object? sender, WinEventHookEventArgs e)
        {
            // WinEventProc is invoked on a background thread (WineventOutofcontext).
            // Resolve the path here while we are still inside WinEventProc's using(Process)
            // block, then marshal all UI / ProfileManager work to the UI thread.
            var path = e.Process.GetPathFromProcessId();
            Dispatcher.BeginInvoke(() =>
            {
                // If no profiles have process triggers configured, do nothing.
                // Without this guard the default-profile fallback fires on every focus
                // change and undoes any switch the user just made with a direct keybind.
                if (!ProfileManager.Profiles.Any(p => p.ProcessTriggers.Length > 0)) return;

                var profileToSwitchTo = ProfileManager.Profiles.FirstOrDefault(p => p.ProcessTriggers.Any(pt => pt.Equals(path, StringComparison.OrdinalIgnoreCase)));
                if (profileToSwitchTo is { } profile)
                {
                    ProfileManager.SwitchTo(profile);
                }
                else if (ProfileManager.Profiles.FirstOrDefault(p => p.IsDefault) is { } defaultProfile)
                {
                    ProfileManager.SwitchTo(defaultProfile);
                }
            });
        }

        protected override void OnClosed(EventArgs e)
        {
            foreach (var handler in handlers.Values)
                handler.Unregister();
            foreach (var handler in directHandlers.Values)
                handler.Unregister();
            processSelectorWindow?.Close();
            base.OnClosed(e);
        }

        // ===== Event Handlers =====

        protected void OnImportButtonClicked(object sender, EventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                DefaultExt = ".json",
                Filter = "JSON files (.json)|*.json",
                Multiselect = true,
            };

            if (dialog.ShowDialog() == true)
            {
                foreach (var path in dialog.FileNames)
                {
                    ProfileManager.ImportProfile(path);
                }
            }
        }

        protected void OnImportRemapButtonClicked(object sender, EventArgs e)
        {
            if (selectedProfile is null) return;

            var dialog = new OpenFileDialog
            {
                DefaultExt = ".json",
                Filter = "JSON files (.json)|*.json",
            };

            if (dialog.ShowDialog() == true)
            {
                ProfileManager.ImportAndLinkRemaps(selectedProfile, dialog.FileName);
                UpdateDetailPanel();
            }
        }

        private void OnActivateProfileClicked(object sender, RoutedEventArgs e)
        {
            var lbSel = ProfileListBox.SelectedItem is ProfileItem lbsi ? lbsi.Name : "null";
            DebugLogger.Log($"OnActivateProfileClicked: selectedProfile='{selectedProfile?.Name}' ListBox.SelectedItem='{lbSel}'");
            if (selectedProfile is { } item)
                ProfileManager.SwitchTo(item);
        }

        private void DeleteProfile(ProfileItem item)
        {
            var result = System.Windows.MessageBox.Show(
                $"Delete '{item.Name}'?",
                "Delete Profile",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes) return;

            ProfileManager.RemoveProfileItems(item);
            if (item == selectedProfile) selectedProfile = null;
            if (ProfileManager.Profiles.Count > 0)
                ProfileListBox.SelectedIndex = 0;
            else
            {
                EmptyState.Visibility = Visibility.Visible;
                DetailScrollViewer.Visibility = Visibility.Collapsed;
            }
        }

        private void OnDeleteProfileClicked(object sender, RoutedEventArgs e)
        {
            if (selectedProfile is { } item) DeleteProfile(item);
        }

        private void OnContextMenuDeleteClicked(object sender, RoutedEventArgs e)
        {
            if (ProfileListBox.SelectedItem is ProfileItem item) DeleteProfile(item);
        }

        private void OnQuickSwitchChanged(object sender, RoutedEventArgs e)
        {
            if (suppressToggleEvents || selectedProfile is null) return;
            selectedProfile.SelectedForQuickSwitch = QuickSwitchToggle.IsChecked == true;
        }

        private void OnDefaultProfileChanged(object sender, RoutedEventArgs e)
        {
            if (suppressToggleEvents || selectedProfile is null) return;
            selectedProfile.IsDefault = DefaultProfileToggle.IsChecked == true;
        }

        private ProfileItem? renamingProfile;

        private void ShowRenameOverlay(ProfileItem item)
        {
            renamingProfile = item;
            RenameTextBox.Text = item.Name;
            RenameOverlay.Visibility = Visibility.Visible;
            RenameTextBox.Focus();
            RenameTextBox.SelectAll();
        }

        private void CommitRename()
        {
            var newName = RenameTextBox.Text?.Trim();
            if (renamingProfile is { } item && !string.IsNullOrEmpty(newName) && newName != item.Name)
            {
                item.Name = newName;
                if (selectedProfile == item)
                    DetailProfileName.Text = newName;
                if (item.IsActiveProfile)
                    ActiveProfileText.Text = "Active: " + newName;
            }
            RenameOverlay.Visibility = Visibility.Collapsed;
            renamingProfile = null;
        }

        private void RenameConfirm_Click(object sender, RoutedEventArgs e) => CommitRename();

        private void RenameCancel_Click(object sender, RoutedEventArgs e)
        {
            RenameOverlay.Visibility = Visibility.Collapsed;
            renamingProfile = null;
        }

        private void RenameOverlay_Dismiss(object sender, MouseButtonEventArgs e)
        {
            // Click on the semi-transparent backdrop outside the rename dialog dismisses it
            if (e.OriginalSource == RenameOverlay)
            {
                RenameOverlay.Visibility = Visibility.Collapsed;
                renamingProfile = null;
            }
        }

        private void RenameTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Enter) CommitRename();
            else if (e.Key == Key.Escape) RenameCancel_Click(sender, e);
        }

        private void OnSidebarRenameClicked(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is ProfileItem item)
                ShowRenameOverlay(item);
        }

        private void OnListBoxPreviewRightClick(object sender, MouseButtonEventArgs e)
        {
            var container = ItemsControl.ContainerFromElement(ProfileListBox, e.OriginalSource as DependencyObject) as ListBoxItem;
            if (container?.DataContext is ProfileItem item)
                ProfileListBox.SelectedItem = item;
        }

        private void OnListBoxPreviewLeftClick(object sender, MouseButtonEventArgs e)
        {
            // Don't intercept clicks on interactive children (rename pencil etc.).
            if (e.OriginalSource is DependencyObject src
                && FindAncestor<System.Windows.Controls.Primitives.ButtonBase>(src) is not null)
                return;

            var container = ItemsControl.ContainerFromElement(ProfileListBox, e.OriginalSource as DependencyObject) as ListBoxItem;
            var clicked = container?.DataContext as ProfileItem;
            if (clicked is null) return;

            var clickedSel = container?.IsSelected.ToString() ?? "?";
            var lbSelName = ProfileListBox.SelectedItem is ProfileItem lbsi ? lbsi.Name : "null";
            var fieldName = selectedProfile?.Name ?? "null";
            DebugLogger.Log($"OnListBoxPreviewLeftClick: clicked='{clicked.Name}'(IsSelected={clickedSel}) ListBox.SelectedItem='{lbSelName}' selectedProfile='{fieldName}'");

            // CRITICAL: Do NOT touch ListBoxItem.IsSelected directly. WPF's Selector
            // crashes (System.ArgumentException: duplicate key in SelectedItemsStorage)
            // when its internal SelectedItems dictionary is already corrupted, and
            // direct IsSelected manipulation hits SetSelectedHelper which is the
            // exact path that throws. Use only the SelectedItem property — it's
            // safer and idempotent if already set.
            if (!ReferenceEquals(ProfileListBox.SelectedItem, clicked))
                ProfileListBox.SelectedItem = clicked;

            // Always sync the field. SelectionChanged is unreliable when the
            // Selector is in a confused state (it sometimes reports the wrong
            // SelectedItem, or doesn't fire at all when it thinks nothing changed).
            if (!ReferenceEquals(selectedProfile, clicked))
            {
                selectedProfile = clicked;
                EmptyState.Visibility = Visibility.Collapsed;
                DetailScrollViewer.Visibility = Visibility.Visible;
                UpdateDetailPanel();
            }
            // Don't set e.Handled — let WPF's normal mouse handling continue so
            // focus, keyboard nav, context menus etc. keep working.
        }

        private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
        {
            while (d is not null and not T)
                d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                    ? System.Windows.Media.VisualTreeHelper.GetParent(d)
                    : LogicalTreeHelper.GetParent(d);
            return d as T;
        }

        private void OnContextMenuRenameClicked(object sender, RoutedEventArgs e)
        {
            if (ProfileListBox.SelectedItem is ProfileItem item)
                ShowRenameOverlay(item);
        }


        // ===== Direct Switch Keybind =====

        private void RegisterDirectHandler(ProfileItem item)
        {
            if (item.DirectSwitchKey == 0) return;
            var windowHandle = new WindowInteropHelper(this).Handle;
            var source = HwndSource.FromHwnd(windowHandle);
            var captured = item; // avoid closure capture issue
            var h = new KeyHandler(item.DirectSwitchKey, windowHandle, source,
                item.DirectSwitchModifiers | KeyHandler.MOD_NOREPEAT)
            {
                Callback = () => Dispatcher.BeginInvoke(() => ProfileManager.SwitchTo(captured)),
            };
            UnregisterDirectHandler(item);
            directHandlers[item] = h;
            h.Register();
        }

        private void UnregisterDirectHandler(ProfileItem item)
        {
            if (directHandlers.TryGetValue(item, out var old))
            {
                old.Unregister();
                directHandlers.Remove(item);
            }
        }

        private void OnDirectKeybindBadgeClicked(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (selectedProfile is null) return;
            isRecordingDirectKeybind = true;
            isRecordingKeybind = false;
            DirectKeybindLabel.Text = "Press keys…";
        }

        private void UpdateDirectKeybindLabel()
        {
            if (DirectKeybindLabel is null || selectedProfile is null) return;
            DirectKeybindLabel.Text = selectedProfile.DirectSwitchKey == 0
                ? "None"
                : FormatKeybind(selectedProfile.DirectSwitchKey, selectedProfile.DirectSwitchModifiers);
            DirectKeybindLabel.Foreground = selectedProfile.DirectSwitchKey == 0
                ? (SolidColorBrush)FindResource("DdFg3")
                : (SolidColorBrush)FindResource("DdFg1");
        }

        private void OnAddProcessTriggerClicked(object sender, RoutedEventArgs e)
        {
            if (selectedProfile is { } item)
            {
                processSelectorWindow?.Close();
                processSelectorWindow = new ProcessSelector(item);
                processSelectorWindow.Owner = this;
                processSelectorWindow.SetStoredProcesses(item.ProcessTriggers);
                processSelectorWindow.StoredProcesses.CollectionChanged += HandleStoredProcessesCollectionChanged;
                processSelectorWindow.ShowDialog();
                UpdateDetailPanel();
            }
        }

        private void OnRemoveTriggerClicked(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button button && button.Tag is string triggerPath && selectedProfile is { } item)
            {
                item.ProcessTriggers = item.ProcessTriggers.Where(t => t != triggerPath).ToArray();
                UpdateDetailPanel();
            }
        }

        private void HandleStoredProcessesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (processSelectorWindow is { } window)
            {
                window.ProfileItem.ProcessTriggers = window.StoredProcesses.Select(pr => pr.ProcessPath).ToArray();
            }
        }

        // ===== Window Lifecycle =====

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (ShouldStartMinimized)
            {
                WindowState = WindowState.Minimized;
            }
        }

        public void Restore()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
            Topmost = true;
            Topmost = false;
            Focus();
        }

        private void Window_StateChanged(object sender, EventArgs e)
        {
            if (WindowState is WindowState.Minimized)
            {
                ShowInTaskbar = false;
            }
            else
            {
                ShowInTaskbar = true;
                // Adjust corner radius for maximized state
                bool maximized = WindowState == WindowState.Maximized;
                OuterBorder.CornerRadius = maximized ? new CornerRadius(0) : new CornerRadius(12);
                SidebarBorder.CornerRadius = maximized ? new CornerRadius(0) : new CornerRadius(0, 0, 0, 12);
            }
        }

        private void OnCloseDetailClicked(object sender, RoutedEventArgs e)
        {
            selectedProfile = null;
            ProfileListBox.SelectedItem = null;
            EmptyState.Visibility = Visibility.Visible;
            DetailScrollViewer.Visibility = Visibility.Collapsed;
        }

        private FrameworkElement? _helpTarget;
        private System.Windows.Threading.DispatcherTimer? _helpCloseTimer;

        private void HelpIcon_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (sender is not FrameworkElement el || el.Tag is not string tag) return;

            HelpPopupText.Inlines.Clear();
            var parts = tag.Split('|');
            HelpPopupText.Inlines.Add(new System.Windows.Documents.Run(parts[0]));
            if (parts.Length == 2)
            {
                HelpPopupText.Inlines.Add(new System.Windows.Documents.LineBreak());
                var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run("View guide →"))
                {
                    NavigateUri = new Uri(parts[1]),
                    Foreground = (SolidColorBrush)FindResource("DdAccent")
                };
                link.RequestNavigate += (_, ev) =>
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = ev.Uri.ToString(), UseShellExecute = true });
                    ev.Handled = true;
                };
                HelpPopupText.Inlines.Add(link);
            }

            _helpTarget = el;
            HelpPopup.PlacementTarget = el;
            HelpPopup.IsOpen = true;

            _helpCloseTimer?.Stop();
            _helpCloseTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _helpCloseTimer.Tick += (_, _) =>
            {
                bool overTrigger = _helpTarget?.IsMouseOver == true;
                bool overPopup = HelpPopup.Child is FrameworkElement child && child.IsMouseOver;
                if (!overTrigger && !overPopup)
                {
                    _helpCloseTimer.Stop();
                    HelpPopup.IsOpen = false;
                }
            };
            _helpCloseTimer.Start();
        }

        private void OnProfileNoteChanged(object sender, RoutedEventArgs e)
        {
            if (selectedProfile is null) return;
            selectedProfile.Note = ProfileNoteTextBox.Text ?? string.Empty;
        }

        private void HelpIcon_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e) { }
        private void HelpPopup_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e) { }
        private void HelpPopup_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e) { }

        private void OnRefreshProfilesClicked(object sender, RoutedEventArgs e)
        {
            // Avoid the ItemsSource null/reset anti-pattern — it corrupts Selector
            // selection tracking and silently breaks future clicks. Just clear and
            // restore the SelectedIndex through the proper path.
            ProfileListBox.SelectedItem = null;
            selectedProfile = null;
            if (ProfileManager.Profiles.Count > 0)
                ProfileListBox.SelectedIndex = 0;
            else
            {
                EmptyState.Visibility = Visibility.Visible;
                DetailScrollViewer.Visibility = Visibility.Collapsed;
            }
            ProfileManager.ApplyCurrentProfile();
        }

        private void OnCheckChanged(object? sender, EventArgs e)
        {
            StartupShortcutHelper.OnCheckChanged(StartOnWindowsStartupToggle.IsChecked ?? false);
        }

        private void UsageStatsToggle_Changed(object sender, RoutedEventArgs e)
        {
            // Persist the new toggle state immediately. Settings.SaveOnDirty
            // is on by default after FromFile(), so SetField via the property
            // setter writes through; calling Save() here is belt-and-suspenders
            // for the first-launch case (no settings.json yet).
            var enabled = UsageStatsToggle.IsChecked ?? true;
            settings.UsageStatsEnabled = enabled;
            settings.Save();
        }
    }
}
