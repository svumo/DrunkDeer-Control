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
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace WpfApp
{
    public partial class MainWindow : Window
    {
        public static bool ShouldStartMinimized { get; set; } = false;
        private readonly Dictionary<ProfileItem, KeyHandler> directHandlers = [];
        private readonly ProfileManager ProfileManager;
        private readonly WinEventHook WinEventHook;
        private readonly KeyboardManager KeyboardManager;
        private ProcessSelector? processSelectorWindow;
        private ProfileItem? selectedProfile;
        private bool suppressToggleEvents;
        private bool isRecordingDirectKeybind = false;

        private readonly IGen2WebHidTransport? _webHidTransport;
        private bool _webHidConsentInFlight;

        // beta.23: once the consent dialog has been shown for a given VID in
        // this session, don't auto-fire it again. If the user picked the
        // wrong interface (one with no writable reports — see WebHidBridgeHtml
        // post-pick validator) the bridge forgets the permission, but
        // re-firing the dialog on every subsequent probe just locks the
        // user in a perpetual prompt loop. Once-per-session means: pick
        // succeeds → app stays connected; pick fails → user can replug
        // their keyboard (which fires a fresh device-add notification) or
        // restart the app to retry.
        private readonly System.Collections.Generic.HashSet<int> _webHidConsentShownVids = new();

        public MainWindow(ProfileManager profileManager, WinEventHook winEventHook, TrayIcon icon, KeyboardManager keyboardManager, Settings settings, IGen2WebHidTransport? webHidTransport = null)
        {
            this.settings = settings;
            WinEventHook = winEventHook;
            ProfileManager = profileManager;
            KeyboardManager = keyboardManager;
            _webHidTransport = webHidTransport;
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

            // WindowStyle=None + AllowsTransparency=True drops the taskbar
            // button's icon — push it explicitly to the native HWND so it
            // renders alongside the thumbnail preview. Do it now AND once
            // the window is visible (the taskbar button doesn't exist yet
            // at OnSourceInitialized — the second call refreshes it).
            ApplyTaskbarIcon();
            ContentRendered += (_, _) => ApplyTaskbarIcon();

            // For borderless transparent windows, Maximize covers the whole
            // monitor (including the taskbar) unless we constrain it via
            // WM_GETMINMAXINFO to the monitor's work area.
            var source = System.Windows.Interop.HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            source?.AddHook(WindowProcHook);

            // First thing: clean up any .bak left behind by a previous in-app
            // update. The old running process couldn't delete itself, so we
            // do it now that we're a fresh process.
            AutoUpdater.CleanupBakIfPresent();

            DiscoverProfiles();

            StartupShortcutHelper.SelfHealStartupRegistration();
            StartOnWindowsStartupToggle.IsChecked = StartupShortcutHelper.StartupFileExists();
            StartOnWindowsStartupToggle.Click += OnCheckChanged;

            // Stamp the version footer immediately and kick off the update check.
            // The check runs on the thread pool with a 5s timeout and never throws;
            // ApplyUpdateInfo handles success/failure uniformly.
            VersionFooter.Text = $"Version {UpdateChecker.CurrentVersion.ToString(3)}";
            SidebarVersionLabel.Text = $"v{UpdateChecker.CurrentVersion.ToString(3)}";
            TriggerUpdateCheck();

            WinEventHook.WinEventHookHandler += OnWinEventHook;

            KeyboardManager.ConnectedKeyboardChanged += OnConnectedKeyboardChanged;
            OnConnectedKeyboardChanged(KeyboardManager.KeyboardWithSpecs);

            // Gen-2 OEM keyboards (VID 0x19F5) route through an embedded
            // WebView2 + WebHID — the Windows HID class driver silently
            // filters them so no standard API works. KeyboardManager raises
            // Gen2WebHidConsentNeeded when it spots such a device with no
            // previously-saved permission; we show the consent dialog,
            // which drives the picker and re-scans on success.
            KeyboardManager.Gen2WebHidConsentNeeded += OnGen2WebHidConsentNeeded;

            // Kick off a follow-up scan once WebView2 is ready. The initial
            // scan from KeyboardManager's constructor likely ran before the
            // transport had finished initializing, so previously-permitted
            // gen-2 OEM devices wouldn't have reconnected.
            _ = Task.Run(async () =>
            {
                try { await KeyboardManager.RefreshAsync(); }
                catch (Exception ex) { DebugLogger.Log($"MainWindow: post-init keyboard refresh failed — {ex.GetType().Name}: {ex.Message}"); }
            });
        }

        private void OnGen2WebHidConsentNeeded(int vid, int pid)
        {
            // Marshal to UI thread and de-dupe: KeyboardManager may emit
            // this event multiple times during rapid re-scans (initial
            // ctor scan + RefreshAsync + DeviceListChanged). Show the
            // dialog at most once per session.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_webHidConsentInFlight) return;
                if (_webHidTransport is null) return;
                if (_webHidConsentShownVids.Contains(vid))
                {
                    DebugLogger.Log($"OnGen2WebHidConsentNeeded: skipping — consent already shown this session for VID=0x{vid:x4}. User can replug the keyboard or restart the app to retry.");
                    return;
                }
                _webHidConsentInFlight = true;
                _webHidConsentShownVids.Add(vid);
                try
                {
                    var dlg = new WebHid.WebHidConsentDialog(_webHidTransport, KeyboardManager, vid)
                    {
                        Owner = this.IsVisible ? this : null
                    };
                    dlg.ShowDialog();
                }
                finally
                {
                    _webHidConsentInFlight = false;
                }
            }));
        }

        // Kept alive for the window's lifetime — WM_SETICON does not copy the
        // HICON, so disposing these would leave the taskbar pointing at a
        // freed handle and the icon would silently vanish.
        private System.Drawing.Icon? _taskbarIconSmall;
        private System.Drawing.Icon? _taskbarIconBig;

        private void ApplyTaskbarIcon()
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero)
                {
                    DebugLogger.Log("ApplyTaskbarIcon: HWND is zero");
                    return;
                }

                var iconUri = new Uri("pack://application:,,,/Resources/drunkdeer-control-logo.ico", UriKind.Absolute);
                using var stream = System.Windows.Application.GetResourceStream(iconUri)?.Stream;
                if (stream is null)
                {
                    DebugLogger.Log("ApplyTaskbarIcon: pack stream null");
                    return;
                }

                _taskbarIconBig?.Dispose();
                _taskbarIconSmall?.Dispose();
                _taskbarIconBig = new System.Drawing.Icon(stream, 32, 32);
                stream.Position = 0;
                _taskbarIconSmall = new System.Drawing.Icon(stream, 16, 16);

                // Also refresh WPF's managed Icon — this pokes WPF's
                // internal taskbar-icon plumbing and forces a re-publish.
                stream.Position = 0;
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.StreamSource = stream;
                bmp.EndInit();
                bmp.Freeze();
                this.Icon = bmp;

                const int WM_SETICON = 0x0080;
                const int ICON_SMALL = 0;
                const int ICON_BIG = 1;
                const int GCLP_HICON = -14;
                const int GCLP_HICONSM = -34;

                SendMessage(hwnd, WM_SETICON, (IntPtr)ICON_SMALL, _taskbarIconSmall.Handle);
                SendMessage(hwnd, WM_SETICON, (IntPtr)ICON_BIG, _taskbarIconBig.Handle);

                // Also set the class icons. The taskbar prefers WM_GETICON
                // results, but falls back to the class icon (GCLP_HICON) if
                // WM_GETICON returns NULL — and some shells query the class
                // icon directly.
                if (IntPtr.Size == 8)
                {
                    SetClassLongPtr64(hwnd, GCLP_HICON, _taskbarIconBig.Handle);
                    SetClassLongPtr64(hwnd, GCLP_HICONSM, _taskbarIconSmall.Handle);
                }
                else
                {
                    SetClassLongPtr32(hwnd, GCLP_HICON, _taskbarIconBig.Handle);
                    SetClassLongPtr32(hwnd, GCLP_HICONSM, _taskbarIconSmall.Handle);
                }

                DebugLogger.Log($"ApplyTaskbarIcon: hwnd=0x{hwnd.ToInt64():X} small=0x{_taskbarIconSmall.Handle.ToInt64():X} big=0x{_taskbarIconBig.Handle.ToInt64():X}");
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"ApplyTaskbarIcon failed: {ex.Message}");
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        // ===== WM_GETMINMAXINFO: clip maximize to the monitor's work area =====

        private const int WM_GETMINMAXINFO = 0x0024;
        private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct POINT { public int x; public int y; }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct RECT { public int left, top, right, bottom; }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        private IntPtr WindowProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // A second launch (any copy, any filename, even while we're
            // hidden in the tray) broadcasts this registered message asking
            // the running instance to surface itself.
            if (Program.ShowExistingMessage != 0 && msg == Program.ShowExistingMessage)
            {
                Restore();
                handled = true;
                return IntPtr.Zero;
            }

            // Update-takeover: a newer copy is launching and wants us to
            // dispose USB handles cleanly before exiting. Kill() leaves the
            // keyboard's composite device in a half-released state where the
            // next instance's HidSharp enumeration can't see it until the
            // user replugs (verified on gen-1 A75 Pro mid-typing 2026-05-26).
            //
            // Disposal order: KeyboardManager FIRST (releases gen-1 HidStream
            // + clears gen-2 vendor channels), then the WebHID transport
            // (tears down WebView2 so its WebHID claims on VID 0x19F5 are
            // released). Both wrapped — disposal failures must not block exit
            // or the new instance will Kill us anyway.
            if (Program.ShutdownExistingMessage != 0 && msg == Program.ShutdownExistingMessage)
            {
                DebugLogger.Log("MainWindow: received shutdown broadcast from new instance — disposing HID + exiting");
                try { KeyboardManager?.Dispose(); }
                catch (Exception ex) { DebugLogger.Log($"  KeyboardManager.Dispose failed: {ex.GetType().Name}: {ex.Message}"); }
                // IGen2WebHidTransport is the abstraction; the concrete
                // WebView2-backed impl implements IDisposable. Cast through
                // the runtime interface to keep MainWindow decoupled from
                // the concrete WebHidTransport type.
                try { (_webHidTransport as IDisposable)?.Dispose(); }
                catch (Exception ex) { DebugLogger.Log($"  WebHidTransport.Dispose failed: {ex.GetType().Name}: {ex.Message}"); }
                _forceClose = true;
                // Defer the actual Shutdown so we return from WindowProc first
                // (the dispatcher unwinds its current message before processing
                // the next), avoiding re-entrancy into native modal pumps.
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { System.Windows.Application.Current?.Shutdown(); }
                    catch (Exception ex) { DebugLogger.Log($"  Application.Shutdown failed: {ex.Message}"); }
                }));
                handled = true;
                return IntPtr.Zero;
            }

            if (msg == WM_GETMINMAXINFO)
            {
                var mmi = System.Runtime.InteropServices.Marshal.PtrToStructure<MINMAXINFO>(lParam);
                IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                if (monitor != IntPtr.Zero)
                {
                    var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
                    if (GetMonitorInfo(monitor, ref info))
                    {
                        var work = info.rcWork;
                        var screen = info.rcMonitor;
                        mmi.ptMaxPosition.x = work.left - screen.left;
                        mmi.ptMaxPosition.y = work.top - screen.top;
                        mmi.ptMaxSize.x = work.right - work.left;
                        mmi.ptMaxSize.y = work.bottom - work.top;
                        mmi.ptMaxTrackSize.x = mmi.ptMaxSize.x;
                        mmi.ptMaxTrackSize.y = mmi.ptMaxSize.y;
                        System.Runtime.InteropServices.Marshal.StructureToPtr(mmi, lParam, true);
                    }
                }
                handled = true;
            }
            return IntPtr.Zero;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetClassLongPtr")]
        private static extern IntPtr SetClassLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetClassLong")]
        private static extern IntPtr SetClassLongPtr32(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

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

        private void KnownIssuesButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Components.KnownIssuesWindow { Owner = this };
            dlg.ShowDialog();
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

        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
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
                ProfileFooter.Visibility = Visibility.Visible;
                UpdateDetailPanel();

                // Clicking a sidebar entry activates it — push the profile to
                // the keyboard and rehydrate the editor view in one move.
                // No-op when the entry is already active (covers programmatic
                // selection sync from ProfileChanged below, hotkey/tray paths,
                // and double-clicks on the already-selected row).
                if (ProfileManager.CurrentIndex < 0
                    || ProfileManager.CurrentIndex >= ProfileManager.Profiles.Count
                    || ProfileManager.Profiles[ProfileManager.CurrentIndex] != item)
                {
                    ProfileManager.SwitchTo(item);
                }
            }
            else
            {
                selectedProfile = null;
                ProfileFooter.Visibility = Visibility.Collapsed;
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
            var sub = selectedProfile.Profile.Showname;
            var triggerCount = selectedProfile.ProcessTriggers.Length;
            var triggerSuffix = triggerCount == 0 ? "no process triggers" : $"{triggerCount} process trigger{(triggerCount == 1 ? "" : "s")}";
            DetailProfileSubtitle.Text = string.IsNullOrEmpty(sub) ? triggerSuffix : $"{sub} · {triggerSuffix}";

            suppressToggleEvents = true;
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
            // KeyboardManager raises this from HidSharp's DeviceMonitorEventThread
            // (a background thread), but every assignment below touches a WPF
            // DependencyProperty that demands the UI thread. Marshal explicitly
            // so a USB unplug doesn't crash the app.
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnConnectedKeyboardChanged(keyboardWithSpecs)));
                return;
            }

            if (keyboardWithSpecs is { } kws)
            {
                KeyboardStatusText.Text = $"{kws.Keyboard.GetFriendlyName()}  v{kws.Specs.FirmwareVersion}";

                // Encode FirmwareCapabilities tier in the dot colour + badge:
                //   Verified → green dot, "verified" badge
                //   Beta     → yellow dot, "beta" badge
                //   Unknown  → red dot, "unrecognized" badge
                // No ToolTip — this window has AllowsTransparency=True which
                // breaks WPF tooltips, so the tier label is shown inline as the
                // badge text instead.
                var caps = kws.Specs.GetCapabilities();
                var (dotBrushKey, badgeText) = caps.Tier switch
                {
                    Driver.SupportTier.Verified => ("DdSuccess", "verified"),
                    Driver.SupportTier.Beta     => ("DdWarn",    "beta"),
                    _                           => ("DdDanger",  "unrecognized"),
                };
                ConnectionDot.Fill = (SolidColorBrush)FindResource(dotBrushKey);
                TierBadge.Text = badgeText;
                TierBadge.Visibility = Visibility.Visible;
            }
            else
            {
                KeyboardStatusText.Text = "No keyboard";
                ConnectionDot.Fill = (SolidColorBrush)FindResource("DdNeutral");
                TierBadge.Visibility = Visibility.Collapsed;

                // Drop the cached gen-2 firmware slot map — next connect
                // re-reads it. If a different keyboard model is plugged in,
                // a stale map would route writes to the wrong slots again.
                ProfileManager?.InvalidateGen2SlotMap();
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

                // Mirror activation into sidebar selection so hotkey / tray /
                // startup-restore paths keep the visible row in sync with the
                // active profile. OnProfileSelectionChanged's "already active"
                // guard makes the resulting SelectionChanged a no-op.
                if (!ReferenceEquals(ProfileListBox.SelectedItem, item))
                    ProfileListBox.SelectedItem = item;

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

        // Top-level tab views — instantiated once, toggled via Visibility so
        // keyboard-view state (canvas selection, keystroke listener, firmware
        // banner) survives tab switches.
        private Components.KeyboardView.KeyboardPerformanceView? _keyboardView;
        private Components.Lighting.LightingView? _lightingView;

        private void OnTopTabKeyboardClicked(object sender, System.Windows.RoutedEventArgs e)
        {
            DebugLogger.Log("OnTopTabKeyboardClicked");
            ShowTopTab(TopTab.Keyboard);
        }

        private void OnTopTabLightingClicked(object sender, System.Windows.RoutedEventArgs e)
        {
            DebugLogger.Log("OnTopTabLightingClicked");
            ShowTopTab(TopTab.Lighting);
        }

        private void ShowTopTab(TopTab tab)
        {
            DebugLogger.Log($"ShowTopTab({tab}) kbdView={(_keyboardView is null ? "null" : "ok")} lightingView={(_lightingView is null ? "null" : "ok")}");
            if (_keyboardView is null || _lightingView is null) return;
            bool lighting = tab == TopTab.Lighting;
            _keyboardView.Visibility = lighting ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
            _lightingView.Visibility = lighting ? System.Windows.Visibility.Visible   : System.Windows.Visibility.Collapsed;
            settings.ActiveTopTab = tab;
            // Visual: active tab gets DdAccent fill, inactive gets transparent
            // with DdBorderThin outline. Mirrors how the SegmentedTabStyle
            // RadioButton template handled this internally.
            var active = (System.Windows.Media.Brush)FindResource("DdAccent");
            var inactiveBg = System.Windows.Media.Brushes.Transparent;
            var inactiveBorder = (System.Windows.Media.Brush)FindResource("DdBorderThin");
            var activeFg = (System.Windows.Media.Brush)FindResource("DdFg1");
            var inactiveFg = (System.Windows.Media.Brush)FindResource("DdFg2");
            TopTabKeyboardBtn.Background  = lighting ? inactiveBg     : active;
            TopTabKeyboardBtn.BorderBrush = lighting ? inactiveBorder : active;
            TopTabKeyboardBtn.Foreground  = lighting ? inactiveFg     : activeFg;
            TopTabLightingBtn.Background  = lighting ? active         : inactiveBg;
            TopTabLightingBtn.BorderBrush = lighting ? active         : inactiveBorder;
            TopTabLightingBtn.Foreground  = lighting ? activeFg       : inactiveFg;
        }

        private static string FormatKeybind(int vk, int mods)
        {
            var parts = new System.Text.StringBuilder();
            if ((mods & KeyHandler.MOD_CONTROL) != 0) parts.Append("Ctrl+");
            if ((mods & KeyHandler.MOD_ALT) != 0) parts.Append("Alt+");
            if ((mods & KeyHandler.MOD_SHIFT) != 0) parts.Append("Shift+");
            var key = KeyInterop.KeyFromVirtualKey(vk);
            parts.Append(FormatKey(key));
            return parts.ToString();
        }

        // Render a WPF Key as the glyph/name a user actually recognizes
        // (matches what's printed on the keycap on a US ANSI A75 Pro). The
        // raw enum names — "Oem3", "Oem8", "Back", "D1" — are developer
        // strings and confused users who saw e.g. "Oem8" rendered in the
        // monospace chip after binding tilde. Ranged keys (letters, digits,
        // F-row) collapse to one character via arithmetic; everything else
        // comes from the dictionary, with `key.ToString()` as a last-resort
        // fallback so an unmapped key is no worse than today.
        private static string FormatKey(Key key) =>
            key switch
            {
                >= Key.A and <= Key.Z       => ((char)('A' + (key - Key.A))).ToString(),
                >= Key.D0 and <= Key.D9     => ((char)('0' + (key - Key.D0))).ToString(),
                >= Key.F1 and <= Key.F24    => $"F{(int)(key - Key.F1) + 1}",
                >= Key.NumPad0 and <= Key.NumPad9 => $"Num {(int)(key - Key.NumPad0)}",
                _ => _keyLabels.TryGetValue(key, out var lbl) ? lbl : key.ToString(),
            };

        private static readonly Dictionary<Key, string> _keyLabels = new()
        {
            // Editing / whitespace
            [Key.Back]        = "Backspace",
            [Key.Tab]         = "Tab",
            [Key.Return]      = "Enter",
            [Key.Space]       = "Space",
            [Key.Escape]      = "Esc",
            [Key.Capital]     = "Caps Lock",
            // Navigation
            [Key.Insert]      = "Insert",
            [Key.Delete]      = "Delete",
            [Key.Home]        = "Home",
            [Key.End]         = "End",
            [Key.PageUp]      = "PgUp",
            [Key.PageDown]    = "PgDn",
            [Key.Up]          = "↑",
            [Key.Down]        = "↓",
            [Key.Left]        = "←",
            [Key.Right]       = "→",
            // System-ish
            [Key.PrintScreen] = "PrtSc",
            [Key.Scroll]      = "ScrLk",
            [Key.Pause]       = "Pause",
            [Key.Apps]        = "Menu",
            // Numpad symbols
            [Key.Multiply]    = "Num *",
            [Key.Add]         = "Num +",
            [Key.Subtract]    = "Num −",
            [Key.Divide]      = "Num /",
            [Key.Decimal]     = "Num .",
            // OEM punctuation (US ANSI mapping for the A75 Pro keycaps)
            [Key.OemTilde]       = "`",
            // VK_OEM_8 is layout-dependent (Win32 docs: "varies by keyboard").
            // On UK/IT layouts it's the grave/section key; on US ANSI it
            // doesn't normally fire. Map to `` ` `` as the most common case —
            // better than the developer enum name "Oem8" the user saw.
            [Key.Oem8]           = "`",
            [Key.OemMinus]       = "-",
            [Key.OemPlus]        = "=",
            [Key.OemOpenBrackets] = "[",
            [Key.Oem6]           = "]",
            [Key.Oem5]           = "\\",
            [Key.OemSemicolon]   = ";",
            [Key.OemQuotes]      = "'",
            [Key.OemComma]       = ",",
            [Key.OemPeriod]      = ".",
            [Key.OemQuestion]    = "/",
        };

        // Shown every time the user opens a hotkey-recording chip until
        // they tick "Don't show this again". Tip nudges them toward
        // modifier combos because plain letter/punctuation bindings eat
        // the key globally while the app is running.
        private void ShowHotkeyHint()
        {
            if (settings.HotkeyHintDismissed) return;
            ShowInfoOverlay(
                title: "Choosing a hotkey",
                body: "Binding a plain key (like [, ], or R) takes that key system-wide while the app is running — you won't be able to type it. Pair it with Alt, Ctrl, or Shift (e.g. Alt+[, Ctrl+Shift+P) so the key still works for typing.",
                iconKind: MaterialDesignThemes.Wpf.PackIconKind.LightbulbOnOutline,
                accentColor: (System.Windows.Media.SolidColorBrush)FindResource("DdAccent"),
                showDontShowAgain: true,
                onDismiss: dontShowAgain =>
                {
                    if (dontShowAgain) settings.HotkeyHintDismissed = true;
                });
        }

        private void OnWindowPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (!isRecordingDirectKeybind) return;
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

            if (isRecordingDirectKeybind && selectedProfile is { } profile)
            {
                var conflict = FindHotkeyConflict(vk, mods, excludeProfile: profile);
                if (conflict is not null)
                {
                    isRecordingDirectKeybind = false;
                    UpdateDirectKeybindLabel();
                    ShowHotkeyConflictWarning(conflict);
                    return;
                }
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

        private void OnActivateProfileClicked(object sender, RoutedEventArgs e)
        {
            var lbSel = ProfileListBox.SelectedItem is ProfileItem lbsi ? lbsi.Name : "null";
            DebugLogger.Log($"OnActivateProfileClicked: selectedProfile='{selectedProfile?.Name}' ListBox.SelectedItem='{lbSel}'");
            if (selectedProfile is { } item)
                ProfileManager.SwitchTo(item);
        }

        private void DeleteProfile(ProfileItem item)
        {
            ShowConfirmOverlay(
                title: "Delete profile?",
                body: $"This will permanently delete “{item.Name}” and its keyboard settings. This can't be undone.",
                confirmText: "Delete",
                iconKind: MaterialDesignThemes.Wpf.PackIconKind.AlertOctagonOutline,
                accentColor: (System.Windows.Media.SolidColorBrush)FindResource("DdDanger"),
                onConfirm: () =>
                {
                    ProfileManager.RemoveProfileItems(item);
                    if (item == selectedProfile) selectedProfile = null;
                    if (ProfileManager.Profiles.Count > 0)
                        ProfileListBox.SelectedIndex = 0;
                    else
                        ProfileFooter.Visibility = Visibility.Collapsed;
                });
        }

        private void OnDeleteProfileClicked(object sender, RoutedEventArgs e)
        {
            if (selectedProfile is { } item) DeleteProfile(item);
        }

        private void OnContextMenuDeleteClicked(object sender, RoutedEventArgs e)
        {
            if (ProfileListBox.SelectedItem is ProfileItem item) DeleteProfile(item);
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

        // Opens the per-profile details overlay (process triggers, notes,
        // direct keybind, default flag) for the clicked sidebar entry. Selects
        // the row first so the overlay's bindings target the correct profile —
        // because sidebar selection now also activates, clicking this in a
        // non-active row switches to it as a side-effect. That's consistent
        // with the rest of the sidebar model.
        private void OnSidebarOpenDetailsClicked(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button btn) return;
            if (btn.Tag is not ProfileItem item) return;
            if (!ReferenceEquals(ProfileListBox.SelectedItem, item))
                ProfileListBox.SelectedItem = item;
            ProfileDetailsTitle.Text = $"{item.Name} — details";
            ProfileDetailsOverlay.Visibility = Visibility.Visible;
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
                ProfileFooter.Visibility = Visibility.Visible;
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


        // ===== Auto-activate on edit =====
        // Called from KeyboardPerformanceView whenever the user mutates the
        // canvas. Historically this auto-promoted the sidebar-selected profile
        // to "active" — but in the new profile-aware architecture the view
        // ALWAYS edits the active profile (it rehydrates from CurrentIndex,
        // and writes back to CurrentIndex). Auto-MarkActive would move
        // CurrentIndex mid-edit and dump the view's current state (which
        // mirrors the previously-active profile) into the newly-active one,
        // silently clobbering it. The user explicitly activates a profile
        // when they want to switch; the auto-mechanism is gone.
        private void OnKeyboardEdit(object? sender, EventArgs e)
        {
            // intentionally empty — see comment above.
        }


        // ===== Profile details overlay (process triggers + notes) =====

        private void OnOpenProfileDetailsClicked(object sender, RoutedEventArgs e)
        {
            if (selectedProfile is null) return;
            ProfileDetailsTitle.Text = $"{selectedProfile.Name} — details";
            ProfileDetailsOverlay.Visibility = Visibility.Visible;
        }

        private void ProfileDetailsClose_Click(object sender, RoutedEventArgs e)
        {
            ProfileDetailsOverlay.Visibility = Visibility.Collapsed;
        }

        private void ProfileDetailsOverlay_Dismiss(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource == ProfileDetailsOverlay)
                ProfileDetailsOverlay.Visibility = Visibility.Collapsed;
        }


        // ===== Presets / Export / Drag-and-drop =====

        private sealed record PresetEntry(string Key, string Name, string Description, string Icon);

        // Names line up with the embedded JSON files under Resources\Presets\.
        // Keep ordering deliberate: FPS first (most-used), Default last.
        private static readonly PresetEntry[] Presets =
        {
            new("FPS", "FPS", "WASD + arrows + space shallow with Rapid Trigger for spam-friendly response.", "Crosshairs"),
            new("Valorant", "Valorant", "WASD ultra-shallow, left shift tuned for counter-strafe.", "Pistol"),
            new("Typing", "Typing", "Deep actuation across the board, no Rapid Trigger — minimises accidental presses.", "Keyboard"),
            new("Default", "Default", "Stock 2.0 mm actuation, Rapid Trigger off. A clean slate.", "Restore"),
        };

        private void OnPresetsButtonClicked(object sender, RoutedEventArgs e)
        {
            PresetsList.ItemsSource = Presets;
            PresetsOverlay.Visibility = Visibility.Visible;
        }

        private void PresetsCancel_Click(object sender, RoutedEventArgs e)
        {
            PresetsOverlay.Visibility = Visibility.Collapsed;
        }

        private void PresetsOverlay_Dismiss(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource == PresetsOverlay)
                PresetsOverlay.Visibility = Visibility.Collapsed;
        }

        private void OnPresetItemClicked(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button btn || btn.Tag is not string key) return;
            PresetsOverlay.Visibility = Visibility.Collapsed;

            // Load the embedded JSON via WPF resource URI — works in both
            // single-file publish and dev runs.
            var uri = new Uri($"pack://application:,,,/Resources/Presets/{key}.json", UriKind.Absolute);
            try
            {
                var info = System.Windows.Application.GetResourceStream(uri);
                if (info is null) return;
                using var reader = new System.IO.StreamReader(info.Stream);
                var json = reader.ReadToEnd();
                var imported = ProfileManager.ImportProfileFromJson(json, key);
                if (imported is not null)
                {
                    ProfileListBox.SelectedItem = imported;
                    ProfileListBox.ScrollIntoView(imported);
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"OnPresetItemClicked: EXCEPTION {ex}");
            }
        }

        private void OnExportProfileClicked(object sender, RoutedEventArgs e)
        {
            if (selectedProfile is null) return;
            var dialog = new SaveFileDialog
            {
                DefaultExt = ".json",
                Filter = "JSON files (.json)|*.json",
                FileName = selectedProfile.Name + ".json",
            };
            if (dialog.ShowDialog() == true)
                ProfileManager.ExportProfile(selectedProfile, dialog.FileName);
        }

        // Drag-and-drop a .json onto the window imports it. Lets users drop
        // straight from the official driver's profile folder without having
        // to click Import → navigate every time.
        private void OnWindowPreviewDragOver(object sender, System.Windows.DragEventArgs e)
        {
            e.Effects = HasJsonFiles(e.Data) ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None;
            e.Handled = true;
        }

        private void OnWindowDrop(object sender, System.Windows.DragEventArgs e)
        {
            if (!HasJsonFiles(e.Data)) return;
            var files = (string[])e.Data.GetData(System.Windows.DataFormats.FileDrop);
            ProfileItem? last = null;
            foreach (var f in files)
            {
                if (string.Equals(System.IO.Path.GetExtension(f), ".json", StringComparison.OrdinalIgnoreCase))
                    last = ProfileManager.ImportProfile(f);
            }
            if (last is not null)
            {
                ProfileListBox.SelectedItem = last;
                ProfileListBox.ScrollIntoView(last);
            }
            e.Handled = true;
        }

        private static bool HasJsonFiles(System.Windows.IDataObject data)
        {
            if (!data.GetDataPresent(System.Windows.DataFormats.FileDrop)) return false;
            var files = data.GetData(System.Windows.DataFormats.FileDrop) as string[];
            return files is not null && files.Any(f => string.Equals(System.IO.Path.GetExtension(f), ".json", StringComparison.OrdinalIgnoreCase));
        }


        // ===== Direct Switch Keybind =====

        // Returns a human-readable description of whichever profile already
        // owns (vk, modifiers), or null if free. Used to block recording a
        // duplicate binding — RegisterHotKey would silently lose the race
        // anyway, so we surface the conflict before saving it to the
        // profile.
        private string? FindHotkeyConflict(int vk, int modifiers, ProfileItem? excludeProfile)
        {
            if (vk == 0) return null;
            foreach (var p in ProfileManager.Profiles)
            {
                if (ReferenceEquals(p, excludeProfile)) continue;
                if (p.DirectSwitchKey == vk && p.DirectSwitchModifiers == modifiers)
                    return $"profile \"{p.Name}\"";
            }
            return null;
        }

        // Used by the hotkey-callback path. When the user presses a key
        // mid-recording that's already someone's hotkey, the OS fires
        // WM_HOTKEY first and PreviewKeyDown may never see the key, so the
        // callback itself has to cancel the recording and surface the
        // conflict — otherwise the user gets silent failure.
        private void CancelRecordingWithConflictWarning(string owner)
        {
            if (isRecordingDirectKeybind)
            {
                isRecordingDirectKeybind = false;
                UpdateDirectKeybindLabel();
            }
            ShowHotkeyConflictWarning(owner);
        }

        private void ShowHotkeyConflictWarning(string owner)
        {
            ShowInfoOverlay(
                title: "Hotkey already in use",
                body: $"That combo is already bound to {owner}. Pick a different key, or pair it with a modifier (Alt, Ctrl, Shift) so each profile gets its own combo.",
                iconKind: MaterialDesignThemes.Wpf.PackIconKind.AlertOutline,
                accentColor: (System.Windows.Media.SolidColorBrush)FindResource("DdWarn"));
        }

        // Held between Show*Overlay and the dismiss handlers. Info dialogs
        // use _onDismiss (carries the don't-show-again bool); confirm
        // dialogs use _onConfirm (fires only when the primary button is
        // clicked, never on Cancel/backdrop).
        private Action<bool>? _infoOverlayOnDismiss;
        private Action? _infoOverlayOnConfirm;

        // Themed info popup — Got-it / single primary button. Replaces
        // Windows MessageBox so the dialog reads as part of the app.
        private void ShowInfoOverlay(string title, string body,
            MaterialDesignThemes.Wpf.PackIconKind iconKind,
            System.Windows.Media.SolidColorBrush accentColor,
            bool showDontShowAgain = false,
            Action<bool>? onDismiss = null)
        {
            InfoOverlayTitle.Text = title;
            InfoOverlayBody.Text = body;
            InfoOverlayIcon.Kind = iconKind;
            InfoOverlayIcon.Foreground = accentColor;
            InfoOverlayDontShowAgain.IsChecked = false;
            InfoOverlayDontShowAgain.Visibility = showDontShowAgain ? Visibility.Visible : Visibility.Collapsed;
            InfoOverlayCancelBtn.Visibility = Visibility.Collapsed;
            InfoOverlayPrimaryBtn.Content = "Got it";
            _infoOverlayOnDismiss = onDismiss;
            _infoOverlayOnConfirm = null;
            InfoOverlay.Visibility = Visibility.Visible;
        }

        // Themed confirm popup — Cancel + destructive/primary action.
        // onConfirm only fires when the primary is clicked; Cancel and
        // backdrop click both dismiss without invoking it. Caller-supplied
        // accentColor tints the icon + primary border so destructive
        // actions read as such.
        private void ShowConfirmOverlay(string title, string body,
            string confirmText,
            MaterialDesignThemes.Wpf.PackIconKind iconKind,
            System.Windows.Media.SolidColorBrush accentColor,
            Action onConfirm)
        {
            InfoOverlayTitle.Text = title;
            InfoOverlayBody.Text = body;
            InfoOverlayIcon.Kind = iconKind;
            InfoOverlayIcon.Foreground = accentColor;
            InfoOverlayDontShowAgain.IsChecked = false;
            InfoOverlayDontShowAgain.Visibility = Visibility.Collapsed;
            InfoOverlayCancelBtn.Visibility = Visibility.Visible;
            InfoOverlayPrimaryBtn.Content = confirmText;
            _infoOverlayOnConfirm = onConfirm;
            _infoOverlayOnDismiss = null;
            InfoOverlay.Visibility = Visibility.Visible;
        }

        // Primary button = Got-it (info) OR Confirm/Delete (confirm). For
        // info dialogs, fires the dismiss callback with don't-show-again
        // state. For confirm dialogs, fires onConfirm only.
        private void InfoOverlay_Primary(object sender, RoutedEventArgs e)
        {
            bool dontShowAgain = InfoOverlayDontShowAgain.IsChecked == true;
            var onConfirm = _infoOverlayOnConfirm;
            var onDismiss = _infoOverlayOnDismiss;
            CloseInfoOverlay();
            onConfirm?.Invoke();
            onDismiss?.Invoke(dontShowAgain);
        }

        // Cancel button (only visible on confirm dialogs) — never invokes
        // onConfirm. Backdrop click routes here too if we're showing a
        // confirm dialog, so the user can dismiss with a click outside.
        private void InfoOverlay_Cancel(object sender, RoutedEventArgs e) => CloseInfoOverlay();

        private void InfoOverlay_Backdrop(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource != InfoOverlay) return;
            // Confirm dialogs treat backdrop as cancel; info dialogs treat
            // it as primary (since pressing Got-it is the only sane action).
            if (_infoOverlayOnConfirm is not null) CloseInfoOverlay();
            else InfoOverlay_Primary(sender, e);
        }

        private void CloseInfoOverlay()
        {
            InfoOverlay.Visibility = Visibility.Collapsed;
            _infoOverlayOnConfirm = null;
            _infoOverlayOnDismiss = null;
        }

        private void RegisterDirectHandler(ProfileItem item)
        {
            if (item.DirectSwitchKey == 0) return;
            var windowHandle = new WindowInteropHelper(this).Handle;
            var source = HwndSource.FromHwnd(windowHandle);
            var captured = item; // avoid closure capture issue
            var h = new KeyHandler(item.DirectSwitchKey, windowHandle, source,
                item.DirectSwitchModifiers | KeyHandler.MOD_NOREPEAT)
            {
                Callback = () => Dispatcher.BeginInvoke(() =>
                {
                    // RegisterHotKey wins WM_HOTKEY before WPF sees WM_KEYDOWN,
                    // so when the user is recording a NEW binding and presses
                    // a key that's already this (or another) profile's hotkey,
                    // PreviewKeyDown may not fire at all. The conflict has to
                    // be reported from inside the callback, otherwise the chip
                    // just silently reverts and the user gets zero feedback.
                    if (isRecordingDirectKeybind)
                    {
                        CancelRecordingWithConflictWarning($"profile \"{captured.Name}\"");
                        return;
                    }
                    ProfileManager.SwitchTo(captured);
                }),
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

        // Suspend/Resume all RegisterHotKey hooks (quick-switch + every
        // profile's direct-switch). RegisterHotKey is an OS-level intercept
        // that fires before WPF input — without these the user can't bind
        // any key that's also a registered hotkey (e.g. `]` if it was set
        // as a direct-switch elsewhere). Suspend is invoked from
        // KeyboardPerformanceView.RemapCaptureStarted; Resume from
        // RemapCaptureEnded. Counter-balanced so nested capture sequences
        // (shouldn't happen, but defensive) don't leak suspensions.
        private int _hotkeySuspendDepth;
        private void SuspendGlobalHotkeys()
        {
            if (_hotkeySuspendDepth++ > 0) return;
            DebugLogger.Log($"SuspendGlobalHotkeys: unregistering {directHandlers.Count} direct handlers");
            foreach (var h in directHandlers.Values) h.Unregister();
        }
        private void ResumeGlobalHotkeys()
        {
            if (--_hotkeySuspendDepth > 0) return;
            if (_hotkeySuspendDepth < 0) _hotkeySuspendDepth = 0;
            DebugLogger.Log($"ResumeGlobalHotkeys: re-registering {directHandlers.Count} direct handlers");
            foreach (var h in directHandlers.Values) h.Register();
        }

        private void OnDirectKeybindBadgeClicked(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (selectedProfile is null) return;
            ShowHotkeyHint();
            isRecordingDirectKeybind = true;
            DirectKeybindLabel.Text = "Press keys…";
        }

        private void UpdateDirectKeybindLabel()
        {
            if (DirectKeybindLabel is null || selectedProfile is null) return;
            DirectKeybindLabel.Text = selectedProfile.DirectSwitchKey == 0
                ? "Click to set"
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
            // Force the taskbar icon. The XAML Icon="..." attribute is
            // unreliable with AllowsTransparency=True + WindowStyle=None on
            // some Windows builds. Earlier attempts pulled Frames[0] which
            // pins to the smallest size in the .ico (16x16) — Windows then
            // can't scale up cleanly for the 32×32 taskbar slot and falls
            // back to the EXE's icon. BitmapFrame.Create(uri) hands WPF the
            // multi-resolution .ico intact so the shell picks the right
            // frame at each DPI.
            try
            {
                var iconUri = new Uri("pack://application:,,,/Resources/drunkdeer-control-logo.ico", UriKind.Absolute);
                Icon = System.Windows.Media.Imaging.BitmapFrame.Create(
                    iconUri,
                    System.Windows.Media.Imaging.BitmapCreateOptions.None,
                    System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            }
            catch (Exception ex) { DebugLogger.Log($"Window_Loaded: icon load failed — {ex.Message}"); }

            // Mount the keyboard performance view as the main content of the
            // detail pane. Built here (not in XAML) so we can hand it the
            // KeyboardManager — the parameterless ctor would skip the
            // hardware wiring that drives Sync, firmware banner, etc.
            if (PerfViewHost.Children.Count == 0)
            {
                var view = new Components.KeyboardView.KeyboardPerformanceView(KeyboardManager);
                // Any user edit in the keyboard view (per-key slider, mode
                // toggle, remap, preset, reset) auto-activates the currently
                // selected profile — editing implies "this is my working
                // profile" so the green dot / top pill / Activate-state
                // should follow without an extra click.
                view.EditOccurred += OnKeyboardEdit;
                // Bind the view to the profile system so per-profile data
                // (per-key AP/DS/US, mode toggles, remaps, LW pairs) flows
                // both ways: edits write back into the active ProfileItem,
                // and profile switches rehydrate the view's UI state.
                view.Attach(ProfileManager);
                // Global RegisterHotKey hooks intercept keystrokes before WPF
                // sees them. While the user is capturing a remap target key,
                // suspend the lot so keys like `]` (if bound elsewhere as a
                // direct-switch hotkey) actually reach the drawer.
                view.RemapCaptureStarted += (_, _) => SuspendGlobalHotkeys();
                view.RemapCaptureEnded += (_, _) => ResumeGlobalHotkeys();
                PerfViewHost.Children.Add(view);
                _keyboardView = view;

                // RGB Lighting view — second top-level tab. Toggled via
                // Visibility (not torn down) so keyboard-view state survives.
                try
                {
                    DebugLogger.Log("Window_Loaded: about to construct LightingView");
                    var lighting = new Components.Lighting.LightingView(KeyboardManager, settings);
                    DebugLogger.Log("Window_Loaded: LightingView constructed, about to Attach");
                    lighting.Attach(ProfileManager);
                    DebugLogger.Log("Window_Loaded: LightingView Attached, adding to PerfViewHost");
                    lighting.Visibility = System.Windows.Visibility.Collapsed;
                    PerfViewHost.Children.Add(lighting);
                    _lightingView = lighting;
                    DebugLogger.Log("Window_Loaded: LightingView wired OK");
                }
                catch (Exception ex)
                {
                    DebugLogger.Log($"Window_Loaded: LightingView wiring FAILED — {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                }

                // Restore last-active tab from settings. ShowTopTab updates
                // both visibility and the active-button visual styling.
                ShowTopTab(settings.ActiveTopTab);
            }

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
            bool maximized = WindowState == WindowState.Maximized;
            OuterBorder.CornerRadius = maximized ? new CornerRadius(0) : new CornerRadius(12);
            SidebarBorder.CornerRadius = maximized ? new CornerRadius(0) : new CornerRadius(0, 0, 0, 12);
            MaximizeIcon.Kind = maximized
                ? MaterialDesignThemes.Wpf.PackIconKind.WindowRestore
                : MaterialDesignThemes.Wpf.PackIconKind.WindowMaximize;
            System.Windows.Automation.AutomationProperties.SetName(MaximizeButton, maximized ? "Restore" : "Maximize");
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
                ProfileFooter.Visibility = Visibility.Collapsed;
            ProfileManager.ApplyCurrentProfile();
        }

        private void OnCheckChanged(object? sender, EventArgs e)
        {
            StartupShortcutHelper.OnCheckChanged(StartOnWindowsStartupToggle.IsChecked ?? false);
        }

    }
}
