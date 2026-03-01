// Credit to: https://github.com/juv/vibranceGUI/blob/master/vibrance.GUI/common/WinEventHook.cs

using Microsoft.Extensions.DependencyInjection;
using System;
using System.Diagnostics;
using System.Media;
using System.Runtime.InteropServices;
using System.Text;

namespace WpfApp.Hooks;

public class WinEventHookEventArgs : EventArgs
{
    public uint ProcessId { get; set; }
    public required Process Process { get; set; }
    public required string WindowText { get; set; }
    public required string ProcessName { get; set; }
    public required string MainWindowTitle { get; set; }
    public IntPtr Handle { get; set; }
}

public sealed partial class WinEventHook
{

    [LibraryImport("user32.dll")]
    private static partial IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr
       hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess,
       uint idThread, uint dwFlags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength([In] IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowTextA([In] IntPtr hWnd, [In, Out] StringBuilder lpString, [In] int nMaxCount);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    private struct WinEvent
    {
        public const uint WineventOutofcontext = 0x0000; // Events are ASYNC

        public const uint EventSystemForeground = 0x0003;
    }

    public event EventHandler<WinEventHookEventArgs>? WinEventHookHandler;
    readonly WinEventDelegate procDelegate = new(WinEventProc);

    private readonly IntPtr _winEventHookHandle;

    public WinEventHook()
    {
        _winEventHookHandle = SetWinEventHook(WinEvent.EventSystemForeground, WinEvent.EventSystemForeground, IntPtr.Zero, procDelegate, 0, 0, WinEvent.WineventOutofcontext);
    }

    public void RemoveWinEventHook()
    {
        try
        {
            bool result = UnhookWinEvent(_winEventHookHandle);
            if (!result)
            {
                Console.WriteLine(new Exception("UnhookWinEvent(winEventHookHandle) failed. winEventHookHandle = " + _winEventHookHandle));
            }
        }
        catch (Exception)
        {
            Console.WriteLine(new Exception("UnhookWinEvent(winEventHookHandle) failed."));
        }
        finally
        {

        }
    }

    static void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        GetWindowThreadProcessId(hwnd, out uint processId);
        int windowTextLength = GetWindowTextLength(hwnd);
        StringBuilder sb = new(windowTextLength + 1);
        _ = GetWindowTextA(hwnd, sb, sb.Capacity);

        try
        {
            using (Process p = Process.GetProcessById((int)processId))
            {
                WinEventHookEventArgs e = new WinEventHookEventArgs
                {
                    Handle = hwnd,
                    Process = p,
                    ProcessId = processId,
                    MainWindowTitle = p.MainWindowTitle,
                    ProcessName = p.ProcessName,
                    WindowText = sb.ToString()
                };
                App.ServiceProvider.GetRequiredService<WinEventHook>().DispatchWinEventHookEvent(e);
            }
        }
        catch (InvalidOperationException)
        {
            // The process property is not defined because the process has exited or it does not have an identifier.
        }
        catch (ArgumentException)
        {
            // The process specified by the processId parameter is not running.
        }
    }

    private void DispatchWinEventHookEvent(WinEventHookEventArgs e)
    {
        WinEventHookHandler?.Invoke(this, e);
    }
}
