using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;

namespace WpfApp;

public partial class Program
{
    public static readonly string APP_DIR = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "DrunkDeer Control"));

    private const string MutexName = "Global\\DrunkDeerControl_SingleInstance";

    /// <summary>
    /// The process-wide single-instance lock, exposed so the install
    /// redirect can drop it before launching the canonical exe (see
    /// <see cref="ReleaseSingleInstanceLock"/>).
    /// </summary>
    private static Mutex? _singleInstanceMutex;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllocConsole();

    [STAThread]
    public static void Main(string[] args)
    {
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        _singleInstanceMutex = mutex;
        if (!createdNew)
        {
            // Another instance is already running — handle update or duplicate launch
            BringExistingInstanceToFront();
            if (!_shouldContinueAfterKillingOld)
                return;

            // Update scenario: old process was killed, re-acquire the mutex and continue
            try { mutex.WaitOne(5000); } catch (AbandonedMutexException) { /* expected */ }
        }

        if (args.Contains("--console"))
        {
            AllocConsole();
            try { Console.SetWindowSize(220, 32); }
            catch (IOException) { /* Redirected console — ignore */ }
        }
        if (args.Contains("--start-minimized"))
        {
            MainWindow.ShouldStartMinimized = true;
        }

        // Defensive clamp probe — runs Packets.BuildLedModePacket with out-of-
        // range inputs (brightness=15, mode=99) and verifies the clamp+whitelist
        // produce the safe wire bytes. No HID I/O, no UI. Exits after the
        // probe so it can't accidentally launch the app. Must run before the
        // install-redirect check below so the probe still works on a canonical
        // install. Do not ship with anything that auto-invokes this.
        if (args.Contains("--rgb-clamp-probe"))
        {
            AllocConsole();
            try { Console.SetWindowSize(120, 24); } catch (IOException) { }
            var probe = new Driver.LightingProfile { Mode = 99, Brightness = 15, Speed = 200 };
            var bytes = Driver.Packets.BuildLedModePacket(probe);
            Console.WriteLine($"BuildLedModePacket(Mode=99, Bright=15, Speed=200) =>");
            Console.WriteLine($"  [0]=0x{bytes[0]:X2} (expected AE)");
            Console.WriteLine($"  [1]=0x{bytes[1]:X2} (expected 01)");
            Console.WriteLine($"  [2]=0x{bytes[2]:X2} (expected 00)");
            Console.WriteLine($"  [3]=0x{bytes[3]:X2} (expected 00)");
            Console.WriteLine($"  [4]=0x{bytes[4]:X2} (expected 00 — Off, mode 99 rejected by whitelist)");
            Console.WriteLine($"  [5]=0x{bytes[5]:X2} (expected 09 — speed clamped from 200)");
            Console.WriteLine($"  [6]=0x{bytes[6]:X2} (expected 09 — brightness clamped from 15)");
            Console.WriteLine($"  [7]=0x{bytes[7]:X2} (expected 00)");
            Console.WriteLine($"PASS = {bytes[0]==0xAE && bytes[1]==0x01 && bytes[4]==0 && bytes[5]==9 && bytes[6]==9}");
            Console.WriteLine("Press Enter to exit.");
            Console.ReadLine();
            return;
        }

        // Pre-WPF install-redirect check: silently exits for the common
        // duplicate-download cases (same version as canonical, or newer →
        // auto-upgrade) BEFORE PreviousInstallCleaner runs, so a numbered
        // "DrunkDeer-Control (12).exe" doesn't leave a stray
        // "%LocalAppData%\DrunkDeer-Control (12)\" data dir on every double-
        // click. Dialog cases (older copy, first launch) are still handled
        // in App.OnStartup where WPF + MaterialDesign resources are loaded.
        if (InstallationManager.HandleEarlyLaunch() == InstallationManager.LaunchDecision.ExitAfterRedirect)
            return;

        // Migrate user data + startup registration from prior installs that used a
        // different exe name. Must run before App() because Settings.FromFile() and
        // ProfileManager read from APP_DIR during DI container construction.
        PreviousInstallCleaner.RunOnce();

        App app = new();
        app.InitializeComponent();
        app.Run();
        App.Application_Exit();
    }

    /// <summary>
    /// Drops the single-instance lock so a redirect target (the canonical
    /// install launched by <see cref="InstallationManager"/>) can acquire
    /// it with createdNew=true and start normally.
    ///
    /// Without this, the just-launched canonical races this still-shutting-
    /// down process for the named mutex, loses (createdNew=false), and then
    /// fails to take over because the takeover path matches the old process
    /// by exact filename — which breaks when the running copy is a browser
    /// duplicate like "DrunkDeer-Control (5).exe". Net effect: the dialog
    /// vanishes and nothing opens. Releasing here removes the race entirely.
    ///
    /// Called on the WPF dispatcher thread, which is the same STA thread
    /// that acquired the mutex in Main — so ReleaseMutex() is valid.
    /// </summary>
    internal static void ReleaseSingleInstanceLock()
    {
        var m = _singleInstanceMutex;
        if (m is null) return;
        _singleInstanceMutex = null;
        try { m.ReleaseMutex(); } catch { /* not owned — fine, just close it */ }
        try { m.Dispose(); } catch { }
    }

    /// <summary>
    /// Matches our process under any name a copy may run as: "DrunkDeer
    /// Control", "DrunkDeer-Control", "DrunkDeerControl", optionally with a
    /// browser-duplicate " (N)" suffix. Discovery must NOT depend on an exact
    /// filename — otherwise a second launch of a differently-named copy
    /// fails to find the running instance and silently does nothing.
    /// </summary>
    private static readonly Regex OurProcessNamePattern =
        new(@"^drunkdeer[ -]?control( \(\d+\))?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Process-independent "please show yourself" signal. RegisterWindowMessage
    /// returns the same id in every process for the same string, so a second
    /// launch and the running instance agree on it with zero shared state.
    /// Broadcast via HWND_BROADCAST, it reaches the running instance even
    /// when its window is hidden in the tray — no PID / filename / window-title
    /// matching required.
    /// </summary>
    internal static readonly int ShowExistingMessage =
        unchecked((int)RegisterWindowMessageW("DrunkDeerControl_ShowExisting_v1"));

    /// <summary>
    /// Process-independent "please dispose HID and exit" signal. Used by the
    /// update-takeover path to let the running instance release its USB
    /// handles cleanly before we exit, rather than killing it mid-flight.
    ///
    /// Why this matters: <see cref="Process.Kill"/> on the old process leaves
    /// the OS USB stack in a half-released state for the keyboard's composite
    /// device. The next instance's HidSharp enumeration then can't see the
    /// keyboard at all (not even its kbd interface — verified on a gen-1 A75
    /// Pro mid-typing 2026-05-26) until the user physically replugs. The
    /// graceful path disposes:
    ///   - KeyboardManager → closes the gen-1 HidStream + clears any gen-2
    ///     vendor channels (HidDeviceExtensions.ClearAllGen2Channels)
    ///   - IGen2WebHidTransport → tears down the WebView2 host so the WebHID
    ///     picker releases its claims on VID 0x19F5 devices
    /// Both must happen before the process exits; the broadcast handler in
    /// MainWindow.WindowProcHook does that ordering.
    /// </summary>
    internal static readonly int ShutdownExistingMessage =
        unchecked((int)RegisterWindowMessageW("DrunkDeerControl_ShutdownRequest_v1"));

    private static readonly nint HWND_BROADCAST = 0xFFFF;

    private static void BringExistingInstanceToFront()
    {
        var currentPath = Environment.ProcessPath;
        var selfPid = System.Diagnostics.Process.GetCurrentProcess().Id;
        var existing = FindOtherInstance(selfPid);

        if (existing is not null && currentPath is not null)
        {
            string? runningPath = null;
            try { runningPath = existing.MainModule?.FileName; } catch { }

            if (runningPath is not null &&
                !string.Equals(currentPath, runningPath, StringComparison.OrdinalIgnoreCase))
            {
                // Different exe → update scenario. Broadcast a graceful shutdown
                // request so the running instance disposes its HID resources
                // (gen-1 HidStream + gen-2 WebView2 channels) before exiting.
                // Kill() leaves the keyboard's USB composite device in a state
                // where HidSharp can't enumerate it again until a physical
                // replug — verified mid-typing on a gen-1 A75 Pro 2026-05-26.
                //
                // CloseMainWindow alone is not enough: MainWindow.OnClosing
                // intercepts WM_CLOSE to hide-to-tray. The broadcast handler
                // sets _forceClose=true AND disposes HID first.
                //
                // Older builds (pre this commit) don't subscribe to the
                // shutdown message; WaitForExit will time out and we'll fall
                // through to Kill — same behaviour as before for those.
                RequestExistingInstanceToShutdown();
                if (!existing.WaitForExit(5000))
                {
                    try { existing.CloseMainWindow(); } catch { }
                    if (!existing.WaitForExit(1500))
                        try { existing.Kill(); } catch { }
                }
                _shouldContinueAfterKillingOld = true;
                return;
            }
        }

        // Same exe, or we couldn't introspect the running one: just ask it
        // to surface its window. This works even when it's hidden in the
        // tray and regardless of how either copy's file is named.
        SignalExistingInstanceToShow();
    }

    /// <summary>
    /// Finds another running copy of this app by normalized process name —
    /// independent of the exact ".exe" filename, so browser duplicates like
    /// "DrunkDeer-Control (5)" still resolve to the running instance.
    /// </summary>
    private static System.Diagnostics.Process? FindOtherInstance(int selfPid)
    {
        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcesses())
            {
                try
                {
                    if (p.Id == selfPid) continue;
                    if (OurProcessNamePattern.IsMatch(p.ProcessName))
                        return p;
                }
                catch { /* process exited / access denied — skip */ }
            }
        }
        catch { /* GetProcesses failed — treat as none found */ }
        return null;
    }

    private static void SignalExistingInstanceToShow()
    {
        if (ShowExistingMessage == 0) return; // registration failed — nothing we can do
        PostMessageW(HWND_BROADCAST, (uint)ShowExistingMessage, nint.Zero, nint.Zero);
    }

    private static void RequestExistingInstanceToShutdown()
    {
        if (ShutdownExistingMessage == 0) return;
        PostMessageW(HWND_BROADCAST, (uint)ShutdownExistingMessage, nint.Zero, nint.Zero);
    }

    internal static bool _shouldContinueAfterKillingOld = false;

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial uint RegisterWindowMessageW(string lpString);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(nint hWnd, uint Msg, nint wParam, nint lParam);
}
