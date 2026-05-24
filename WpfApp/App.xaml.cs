using Driver;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using WpfApp.Components;
using WpfApp.Hooks;
using WpfApp.Profile;
using Application = System.Windows.Application;

namespace WpfApp
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private static ServiceProvider? serviceProvider;
        public static ServiceProvider ServiceProvider => serviceProvider ?? throw new NullReferenceException();

        public App()
        {
            ServiceCollection serviceCollection = new();
            serviceCollection.ConfigureServices();

            serviceProvider = serviceCollection.BuildServiceProvider();

            // Catch every unhandled exception we can reach so the user sees a
            // styled error dialog (with the Discord report link) instead of the
            // app silently dying. Each handler covers a different surface:
            //   - DispatcherUnhandledException: WPF UI thread exceptions (most
            //     of what we hit). We mark these Handled so the app survives.
            //   - AppDomain.UnhandledException: terminal exceptions on any
            //     thread; we can't actually stop the process from dying here,
            //     but we can log and show the dialog before it does.
            //   - TaskScheduler.UnobservedTaskException: background Task faults
            //     no one awaited. Mark Observed so they don't promote to fatal.
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Decide whether this exe is the canonical install before
            // showing any UI. May redirect to a different exe and shut
            // us down — in which case we never reach MainWindow.
            if (InstallationManager.HandleLaunch() == InstallationManager.LaunchDecision.ExitAfterRedirect)
            {
                Shutdown();
                return;
            }

            var cliArgs = Environment.GetCommandLineArgs();

            // --experimental-precision <legacy|oldhighprec>
            // forces every connected keyboard's wire-format dispatch to the
            // given dialect, regardless of what the firmware reports. Used
            // to probe whether a firmware actually accepts a different
            // wire format than it advertises.
            //
            // `newhighprec` was accepted in pre-2.4 builds but is rejected
            // here — the 0xFD 2-byte format used by A75 Ultra/Master is
            // unverified on hardware. See branch parked/newhighprec-untested
            // for the implementation that ships once an Ultra owner can
            // confirm it works.
            int expIdx = Array.IndexOf(cliArgs, "--experimental-precision");
            if (expIdx >= 0 && expIdx + 1 < cliArgs.Length)
            {
                var raw = cliArgs[expIdx + 1].ToLowerInvariant();
                WirePrecision? forced = raw switch
                {
                    "legacy"      => WirePrecision.Legacy,
                    "oldhighprec" => WirePrecision.OldHighPrec,
                    _             => null,
                };
                if (forced.HasValue)
                {
                    FirmwareCapabilities.OverridePrecision = forced;
                    DebugLogger.Log($"App.OnStartup: --experimental-precision {raw} (forcing {forced})");
                }
                else if (raw == "newhighprec")
                {
                    DebugLogger.Log("App.OnStartup: --experimental-precision newhighprec is parked in v2.4 (untested on Ultra/Master hardware) — see branch parked/newhighprec-untested");
                }
                else
                {
                    DebugLogger.Log($"App.OnStartup: --experimental-precision arg '{raw}' not recognised (expected legacy|oldhighprec)");
                }
            }

            // --verbose-log enables packet-level hex dumps in debug.log
            // (every `-> [b6 04 ...]` send and `<- [...]` echo from
            // HidDeviceExtensions + the per-chunk depth lines from
            // HidStreamListener). Default is OFF — a single profile sync
            // produces ~100+ hex lines, drowning out event-level signals
            // like "PushCurrentProfile" / "WritePacket batch complete".
            // Enable when you genuinely need to inspect wire bytes.
            if (cliArgs.Contains("--verbose-log"))
            {
                DebugLogger.Verbose = true;
                DebugLogger.Log("App.OnStartup: --verbose-log enabled (packet hex dumps will appear)");
            }

            // v2.4.1-beta.4 is a diagnostic-only build for the gen-2 firmware
            // identity-timeout investigation. Force verbose logging ON by
            // default so the user doesn't have to remember the CLI flag — the
            // whole point of this build is to capture every byte going over
            // the wire. Revert when shipping a non-diagnostic build.
            if (!DebugLogger.Verbose)
            {
                DebugLogger.Verbose = true;
                DebugLogger.Log("App.OnStartup: forcing Verbose=true (beta.4 is a diagnostic build for gen-2 identity-timeout investigation)");
            }

            // --firmware-too-old-demo [fwHex] launches the FirmwareTooOldDialog
            // standalone with mock data so the rendering / button behaviour
            // can be smoke-tested on any Windows machine without a connected
            // keyboard. Default firmware: 0x0009 (A75 Pro factory floor, the
            // case the modal was built for). Optional second arg overrides
            // (e.g. `--firmware-too-old-demo 0x0012` mimics a G75 JP on
            // pre-2.3.4 firmware).
            //
            // Useful for verifying the upgrade-prompt flow remotely — the
            // FULL production path (FirmwareCapabilities.Resolve → IsTooOld
            // gate → Settings ack lookup → dialog) only fires for real on
            // hardware that returns the matching spec response, but the
            // visuals + browser-launch are 100% the production path.
            if (cliArgs.Contains("--firmware-too-old-demo"))
            {
                ushort fw = 0x0009;
                var idx = Array.IndexOf(cliArgs, "--firmware-too-old-demo");
                if (idx >= 0 && idx + 1 < cliArgs.Length)
                {
                    // Skip if the next arg is another flag (e.g. --no-install-redirect)
                    // so positional fwHex stays optional. ushort.TryParse writes 0 to
                    // its out param on failure, which would silently clobber the
                    // 0x0009 default.
                    var raw = cliArgs[idx + 1];
                    if (!raw.StartsWith("--"))
                    {
                        bool ok = raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                            ? ushort.TryParse(raw.AsSpan(2), System.Globalization.NumberStyles.HexNumber,
                                System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                            : ushort.TryParse(raw, out parsed);
                        if (ok) fw = parsed;
                    }
                }
                // Mock the A75 Pro (TypeCode 750) so Resolve picks the
                // matching LatestKnownFirmware entry; the dialog's labels
                // come from the resolved capabilities + model name.
                var caps = FirmwareCapabilities.Resolve(750, fw);
                var targetFwHex = caps.RecommendedFloor is ushort floor ? $"0x{floor:X4}" : "0x????";
                var fwHex = $"0x{fw:X4}";
                var modelLabel = $"A75 Pro {fwHex} (demo)";
                DebugLogger.Log($"App.OnStartup: --firmware-too-old-demo invoked (fw={fwHex}, target={targetFwHex}, isTooOld={caps.IsTooOld})");
                var result = Components.FirmwareTooOldDialog.Show(modelLabel, targetFwHex, owner: null);
                DebugLogger.Log($"  → dialog result: {result}");
                Shutdown();
                return;
            }

            // --keyboard-debug opens the keyboard performance view in a bare
            // host Window with no sidebar/profile shell — useful for layout
            // verification or HID testing without the rest of the app. The
            // same view is embedded inside MainWindow for normal launches.
            // Combine with --no-install-redirect when running a dev build
            // alongside a 1.5+ canonical install.
            if (cliArgs.Contains("--keyboard-debug"))
            {
                DebugLogger.Log("App.OnStartup: --keyboard-debug set, hosting KeyboardPerformanceView in standalone window");
                var keyboardManager = ServiceProvider.GetRequiredService<KeyboardManager>();
                var view = new Components.KeyboardView.KeyboardPerformanceView(keyboardManager);
                var host = new Window
                {
                    Title = "Keyboard Debug",
                    Width = 1360,
                    Height = 940,
                    MinWidth = 1300,
                    MinHeight = 900,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    Content = view,
                };
                host.SetResourceReference(Window.BackgroundProperty, "DdBgBase");
                host.Show();
                return;
            }

            var mainWindow = ServiceProvider.GetRequiredService<MainWindow>();
            mainWindow.Show();
        }

        public static void Application_Exit()
        {
            var icon = ServiceProvider.GetRequiredService<TrayIcon>();
            icon?.Dispose();
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            DebugLogger.Log($"DispatcherUnhandledException: {e.Exception}");
            ShowErrorWindow("UI thread exception", e.Exception);
            e.Handled = true; // keep the app alive
        }

        private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var ex = e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "Unknown error");
            DebugLogger.Log($"AppDomain UnhandledException (terminating={e.IsTerminating}): {ex}");
            // We may already be on a background thread that's about to die — try
            // to marshal to the UI thread, but don't block forever if we can't.
            try
            {
                Dispatcher?.Invoke(() => ShowErrorWindow("Background thread exception", ex), DispatcherPriority.Send);
            }
            catch { }
        }

        private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            DebugLogger.Log($"UnobservedTaskException: {e.Exception}");
            try
            {
                Dispatcher?.BeginInvoke(() => ShowErrorWindow("Unobserved task exception", e.Exception));
            }
            catch { }
            e.SetObserved();
        }

        // Re-entrancy guard: if showing the error dialog itself throws (e.g. the
        // dialog's own resources fail to load), don't loop forever.
        private bool _showingError;

        private void ShowErrorWindow(string title, Exception ex)
        {
            if (_showingError)
            {
                DebugLogger.Log("ShowErrorWindow: already showing, suppressing nested error");
                return;
            }
            _showingError = true;
            try
            {
                var win = new ErrorWindow(title, ex);
                if (Current?.MainWindow is { IsVisible: true } owner)
                {
                    win.Owner = owner;
                    win.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                }
                win.ShowDialog();
            }
            catch (Exception ex2)
            {
                DebugLogger.Log($"ShowErrorWindow itself threw: {ex2}");
            }
            finally
            {
                _showingError = false;
            }
        }
    }

    public static class ServiceCollectionExtensions
    {
        public static void ConfigureServices(this IServiceCollection services)
        {
            var settings = Settings.FromFile() ?? new Settings() { SaveOnDirty = true };
            services.AddSingleton(settings);
            services.AddSingleton<WinEventHook>();
            services.AddSingleton<MainWindow>();
            services.AddSingleton<ProfileManager>();
            services.AddSingleton<TrayIcon>();
            services.AddSingleton<KeyboardManager>();
        }
    }
}
