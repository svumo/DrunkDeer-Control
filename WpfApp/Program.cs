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
            // Another instance is already running — bring it to the foreground and exit
            BringExistingInstanceToFront();
            return;
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
        // Find the existing window by its title prefix and restore it
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
