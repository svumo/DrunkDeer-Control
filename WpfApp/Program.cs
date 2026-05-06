using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace WpfApp;

public partial class Program
{
    public static readonly string APP_DIR = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "DrunkDeer Control"));

    private const string MutexName = "Global\\DrunkDeerControl_SingleInstance";

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllocConsole();

    [STAThread]
    public static void Main(string[] args)
    {
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
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

        App app = new();
        app.InitializeComponent();
        app.Run();
        App.Application_Exit();
    }

    private static void BringExistingInstanceToFront()
    {
        // Find the existing DrunkDeer Control process
        var currentPath = Environment.ProcessPath;
        var existingProcess = System.Diagnostics.Process.GetProcessesByName(
            Path.GetFileNameWithoutExtension(currentPath ?? "DrunkDeer Control"))
            .FirstOrDefault(p => p.Id != System.Diagnostics.Process.GetCurrentProcess().Id);

        if (existingProcess is null)
        {
            // Couldn't find by name — fall back to window search
            BringWindowToFront();
            return;
        }

        // If the running instance is from a different path, it's an update scenario:
        // kill the old process and let the new one continue as the primary instance.
        string? runningPath = null;
        try { runningPath = existingProcess.MainModule?.FileName; } catch { }

        if (currentPath is not null && runningPath is not null &&
            !string.Equals(currentPath, runningPath, StringComparison.OrdinalIgnoreCase))
        {
            // This is an update — signal old instance to close gracefully, then wait.
            try { existingProcess.CloseMainWindow(); } catch { }
            if (!existingProcess.WaitForExit(3000))
                try { existingProcess.Kill(); } catch { }

            // Release the mutex so the current process can re-acquire it and continue.
            // We do this by NOT returning — the caller will exit, but we need to
            // continue. We signal this via the return value pattern instead.
            // Since we can't change the return type here, we re-invoke Main logic:
            _shouldContinueAfterKillingOld = true;
            return;
        }

        // Same path — just restore the existing window.
        BringWindowToFront();
    }

    internal static bool _shouldContinueAfterKillingOld = false;

    private static void BringWindowToFront()
    {
        var hwnd = FindWindow(null, null);
        while (hwnd != nint.Zero)
        {
            var len = GetWindowTextLength(hwnd);
            if (len > 0)
            {
                var sb = new System.Text.StringBuilder(len + 1);
                GetWindowText(hwnd, sb, sb.Capacity);
                if (sb.ToString().StartsWith("DrunkDeer Control", StringComparison.OrdinalIgnoreCase))
                {
                    if (IsIconic(hwnd))
                        ShowWindow(hwnd, SW_RESTORE);
                    SetForegroundWindow(hwnd);
                    return;
                }
            }
            hwnd = GetNextWindow(hwnd, GW_HWNDNEXT);
        }
    }

    private const int SW_RESTORE = 9;
    private const uint GW_HWNDNEXT = 2;

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint FindWindow([MarshalAs(UnmanagedType.LPStr)] string? lpClassName,
                                           [MarshalAs(UnmanagedType.LPStr)] string? lpWindowName);
    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int GetWindowTextLength(nint hWnd);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowText(nint hWnd, System.Text.StringBuilder lpString, int nMaxCount);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint hWnd);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint hWnd, int nCmdShow);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hWnd);
    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint GetNextWindow(nint hWnd, uint wCmd);
}
