using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace WpfApp.Hooks;

public sealed partial class KeyHandler
{
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(nint hWnd, int id, int fsModifiers, int vk);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(nint hWnd, int id);

    public static readonly int MOD_ALT = 0x01;
    public static readonly int MOD_CONTROL = 0x02;
    public static readonly int MOD_NOREPEAT = 0x4000;
    public static readonly int MOD_SHIFT = 0x04;
    // Can't be used "Either WINDOWS key must be held down. These keys are labeled with the Windows logo. Keyboard shortcuts that involve the WINDOWS key are reserved for use by the operating system."
    public static readonly int MOD_WIN = 0x08;

    public static int ToKeycode(Key key)
    {
       return KeyInterop.VirtualKeyFromKey(key);
    }

    private readonly int key;
    private readonly nint hWnd;
    private readonly int id;
    private readonly int modifiers;
    private readonly HwndSource hwndSource;

    public Action Callback { get; set; } = () => { };

    public KeyHandler(int key, nint windowHandle, HwndSource hwndSource, int modifiers = 0)
    {
        this.key = (int)key;
        this.hWnd = windowHandle;
        this.hwndSource = hwndSource;
        this.modifiers = modifiers;
        id = GetHashCode();
    }

    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_HOTKEY = 0x0312;
        switch (msg)
        {
            case WM_HOTKEY:
                if (wParam == GetHashCode())
                {
                    Callback.Invoke();
                    handled = false;
                }
                break;
        }
        return IntPtr.Zero;
    }

    public override int GetHashCode()
    {
        return key ^ modifiers ^ hWnd.ToInt32();
    }

    public bool Register()
    {
        hwndSource.AddHook(HwndHook);
        return RegisterHotKey(hWnd, id, modifiers, key);
    }

    public bool Unregiser()
    {
        hwndSource.RemoveHook(HwndHook);
        return UnregisterHotKey(hWnd, id);
    }
}
