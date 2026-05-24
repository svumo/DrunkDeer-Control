using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace Driver;

// Last-resort HID input path that bypasses the Windows HID class driver's
// per-report filtering. The gen-2 A75 Pro OEM variant (VID 0x19F5 / PID
// 0xFB5C, observed across multiple users 2026-05-23..24) presents a
// vendor interface with Generic-Desktop usagePage=0x01, usage=0x00, and
// Constant-flagged Input/Output fields. Every standard read mechanism
// returns nothing:
//   - HidStream.Read (HidSharpCore overlapped ReadFile): TimeoutException
//   - Native overlapped ReadFile via P/Invoke: no completions
//   - HidD_GetInputReport (control transfer): ERROR_GEN_FAILURE
// All three go through HidClass.sys. Chrome's WebHID, by contrast,
// receives input reports normally — suggesting the OS DOES surface these
// reports somewhere, just not through the standard HID class APIs.
//
// Raw Input (WM_INPUT) is the next-higher-level Windows input channel.
// HID reports flow:  USB function driver -> HidClass.sys -> Raw Input
// subsystem -> WM_INPUT. The Raw Input subsystem may not apply the same
// "Constant input is reserved" filter the HID class driver imposes on
// ReadFile/control-transfer paths. If that holds, WM_INPUT will deliver
// the bytes we couldn't reach any other way.
//
// This class hosts a hidden message-only window on a dedicated background
// thread (Raw Input requires a window with a message pump). The window
// receives WM_INPUT for every HID device the user has subscribed to, and
// dispatches each report's raw bytes to the matching subscriber.
public sealed class RawInputReceiver : IDisposable
{
    private const int WM_INPUT = 0x00FF;
    private const int WM_QUIT = 0x0012;
    private const int WM_DESTROY = 0x0002;
    private const int WM_USER_REGISTER_DEVICE = 0x0400 + 1;
    private const int HWND_MESSAGE = -3;
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const uint RIDEV_REMOVE = 0x00000001;
    private const uint RIDEV_PAGEONLY = 0x00000020;
    private const uint RID_INPUT = 0x10000003;
    private const uint RIDI_DEVICENAME = 0x20000007;
    private const uint RIM_TYPEHID = 2;
    private const uint WS_OVERLAPPED = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    // Inline RAWHID at end of RAWINPUT (after header). We never marshal the
    // full RAWINPUT struct — we read raw bytes manually with Marshal.Copy
    // to handle the variable-length bRawData[] trailing array.
    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public IntPtr lpszMenuName;
        public IntPtr lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetRawInputDeviceInfo(IntPtr hDevice, uint uiCommand, IntPtr pData, ref uint pcbSize);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    // Callback subscribers register with: receives (devicePath, payloadBytes).
    // payloadBytes is the raw HID report including the Report ID byte (= byte 0).
    // It's the caller's responsibility to strip the Report ID if their protocol
    // expects payload-only.
    public sealed record Subscription(int VendorId, int ProductId, Action<string, byte[]> Callback);

    private readonly List<Subscription> _subscriptions = new();
    private readonly object _subscriptionsLock = new();
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _readyEvent = new(false);
    private readonly ManualResetEventSlim _shutdownEvent = new(false);
    private readonly WndProcDelegate _wndProcDelegate;
    private readonly Dictionary<IntPtr, (int vid, int pid, string path)> _deviceCache = new();
    private readonly object _deviceCacheLock = new();
    private IntPtr _hwnd;
    private uint _threadId;
    private string? _windowClassName;
    private bool _disposed;
    private int _wmInputCount;
    private int _wmInputHidCount;
    private int _wmInputDispatchCount;

    public bool IsReady => _hwnd != IntPtr.Zero;
    public int WmInputCount => _wmInputCount;
    public int WmInputHidCount => _wmInputHidCount;
    public int WmInputDispatchCount => _wmInputDispatchCount;

    public RawInputReceiver()
    {
        // Pin the delegate so the GC doesn't move/collect it while Windows
        // still has the function pointer.
        _wndProcDelegate = WindowProc;

        _thread = new Thread(MessageLoop)
        {
            IsBackground = true,
            Name = "RawInputReceiver"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        // Wait up to 3 seconds for the window to be created and Raw Input
        // to be registered. If it never comes up, we still return — callers
        // will see IsReady == false and skip the raw-input path.
        if (!_readyEvent.Wait(3000))
        {
            DebugLogger.Log("RawInputReceiver: setup timed out after 3s — proceeding without raw input");
        }
    }

    // Returns an IDisposable; dispose it to remove the subscription. The same
    // callback can be registered multiple times — each subscription is
    // independent.
    public IDisposable Subscribe(int vendorId, int productId, Action<string, byte[]> callback)
    {
        var sub = new Subscription(vendorId, productId, callback);
        lock (_subscriptionsLock)
        {
            _subscriptions.Add(sub);
        }
        DebugLogger.Log($"RawInputReceiver: subscribed VID=0x{vendorId:x4} PID=0x{productId:x4} (total subscriptions={_subscriptions.Count})");
        return new SubscriptionToken(this, sub);
    }

    private sealed class SubscriptionToken : IDisposable
    {
        private readonly RawInputReceiver _owner;
        private readonly Subscription _sub;
        private bool _disposed;
        public SubscriptionToken(RawInputReceiver owner, Subscription sub) { _owner = owner; _sub = sub; }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            lock (_owner._subscriptionsLock)
            {
                _owner._subscriptions.Remove(_sub);
            }
        }
    }

    private void MessageLoop()
    {
        try
        {
            _threadId = GetCurrentThreadId();
            var hInstance = GetModuleHandle(null);
            _windowClassName = "DrunkDeerRawInputReceiver_" + Guid.NewGuid().ToString("N");

            var wndProcPtr = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);
            var classNamePtr = Marshal.StringToHGlobalUni(_windowClassName);
            try
            {
                var wc = new WNDCLASSEX
                {
                    cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                    style = 0,
                    lpfnWndProc = wndProcPtr,
                    cbClsExtra = 0,
                    cbWndExtra = 0,
                    hInstance = hInstance,
                    hIcon = IntPtr.Zero,
                    hCursor = IntPtr.Zero,
                    hbrBackground = IntPtr.Zero,
                    lpszMenuName = IntPtr.Zero,
                    lpszClassName = classNamePtr,
                    hIconSm = IntPtr.Zero
                };

                var atom = RegisterClassEx(ref wc);
                if (atom == 0)
                {
                    var err = Marshal.GetLastWin32Error();
                    DebugLogger.Log($"RawInputReceiver: RegisterClassEx failed Win32 err={err}");
                    _readyEvent.Set();
                    return;
                }

                _hwnd = CreateWindowEx(
                    0,
                    _windowClassName,
                    "DrunkDeer Raw Input",
                    WS_OVERLAPPED,
                    0, 0, 0, 0,
                    new IntPtr(HWND_MESSAGE),
                    IntPtr.Zero,
                    hInstance,
                    IntPtr.Zero);
                if (_hwnd == IntPtr.Zero)
                {
                    var err = Marshal.GetLastWin32Error();
                    DebugLogger.Log($"RawInputReceiver: CreateWindowEx failed Win32 err={err}");
                    UnregisterClass(_windowClassName, hInstance);
                    _readyEvent.Set();
                    return;
                }

                // Register every plausible (usagePage, usage) combination for
                // the gen-2 OEM keyboard. We don't know which top-level
                // collection the firmware actually streams responses on, and
                // the user's keyboard exposes seven of them. Cast a wide
                // net via RIDEV_PAGEONLY (subscribes to every device in the
                // page regardless of TLC usage). We filter by VID/PID at
                // dispatch time.
                //
                // CRITICAL: usUsage MUST be 0 when RIDEV_PAGEONLY is set,
                // and any RAWINPUTDEVICE with usUsage=0 MUST set
                // RIDEV_PAGEONLY — else RegisterRawInputDevices fails with
                // ERROR_INVALID_PARAMETER (Win32 err 87) for the entire
                // array. This was the beta.10 bug that prevented WM_INPUT
                // from arriving at all.
                //
                // Coverage:
                //   page 0x01 (Generic Desktop) covers mi_00 (kbd usage 6),
                //     mi_02&col01 (kbd), mi_02&col02 (mouse usage 2),
                //     and mi_01 (vendor usage 0).
                //   page 0xFF00 (Vendor-Defined) covers gen-1-style vendor
                //     devices on other variants.
                //   page 0x0C (Consumer) covers mi_02&col03/04/05.
                var devices = new[]
                {
                    new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0, dwFlags = RIDEV_INPUTSINK | RIDEV_PAGEONLY, hwndTarget = _hwnd },
                    new RAWINPUTDEVICE { usUsagePage = 0xFF00, usUsage = 0, dwFlags = RIDEV_INPUTSINK | RIDEV_PAGEONLY, hwndTarget = _hwnd },
                    new RAWINPUTDEVICE { usUsagePage = 0x0C, usUsage = 0, dwFlags = RIDEV_INPUTSINK | RIDEV_PAGEONLY, hwndTarget = _hwnd },
                };
                var size = (uint)Marshal.SizeOf<RAWINPUTDEVICE>();
                var ok = RegisterRawInputDevices(devices, (uint)devices.Length, size);
                if (!ok)
                {
                    var err = Marshal.GetLastWin32Error();
                    DebugLogger.Log($"RawInputReceiver: RegisterRawInputDevices failed Win32 err={err} — message loop still running but won't receive WM_INPUT");
                }
                else
                {
                    DebugLogger.Log($"RawInputReceiver: registered for {devices.Length} (usagePage,usage) combos with RIDEV_INPUTSINK");
                }

                _readyEvent.Set();

                while (true)
                {
                    var msgResult = GetMessage(out var msg, IntPtr.Zero, 0, 0);
                    if (!msgResult) break;
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
            }
            finally
            {
                if (_hwnd != IntPtr.Zero)
                {
                    try { DestroyWindow(_hwnd); } catch { }
                    _hwnd = IntPtr.Zero;
                }
                if (_windowClassName is not null)
                {
                    try { UnregisterClass(_windowClassName, hInstance); } catch { }
                }
                Marshal.FreeHGlobal(classNamePtr);
            }
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"RawInputReceiver.MessageLoop: unhandled exception — {ex.GetType().Name}: {ex.Message}");
            _readyEvent.Set();
        }
        finally
        {
            _shutdownEvent.Set();
        }
    }

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_INPUT)
        {
            try
            {
                HandleRawInput(lParam);
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"RawInputReceiver.WindowProc: exception in HandleRawInput — {ex.GetType().Name}: {ex.Message}");
            }
        }
        else if (msg == WM_DESTROY)
        {
            // PostQuitMessage breaks the GetMessage loop.
            PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void HandleRawInput(IntPtr hRawInput)
    {
        Interlocked.Increment(ref _wmInputCount);

        uint headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        uint dataSize = 0;
        var result = GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref dataSize, headerSize);
        if (result != 0 || dataSize == 0) return;

        var buffer = Marshal.AllocHGlobal((int)dataSize);
        try
        {
            var copied = GetRawInputData(hRawInput, RID_INPUT, buffer, ref dataSize, headerSize);
            if (copied != dataSize) return;

            var header = Marshal.PtrToStructure<RAWINPUTHEADER>(buffer);
            if (header.dwType != RIM_TYPEHID) return;

            Interlocked.Increment(ref _wmInputHidCount);

            // RAWHID immediately follows the header. Its layout:
            //   DWORD dwSizeHid;
            //   DWORD dwCount;
            //   BYTE bRawData[dwSizeHid * dwCount];
            var rawHidOffset = (int)headerSize;
            uint dwSizeHid = (uint)Marshal.ReadInt32(buffer, rawHidOffset);
            uint dwCount = (uint)Marshal.ReadInt32(buffer, rawHidOffset + 4);
            if (dwSizeHid == 0 || dwCount == 0) return;

            var (vid, pid, path) = ResolveDevice(header.hDevice);
            if (vid == 0 && pid == 0) return;

            // Capture each report in the batch separately.
            int dataOffset = rawHidOffset + 8;
            for (uint i = 0; i < dwCount; i++)
            {
                var report = new byte[dwSizeHid];
                Marshal.Copy(buffer + dataOffset + (int)(i * dwSizeHid), report, 0, (int)dwSizeHid);
                DispatchReport(vid, pid, path, report);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private (int vid, int pid, string path) ResolveDevice(IntPtr hDevice)
    {
        lock (_deviceCacheLock)
        {
            if (_deviceCache.TryGetValue(hDevice, out var cached)) return cached;
        }

        // Query device name (path) via GetRawInputDeviceInfo.
        uint pathChars = 0;
        GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, IntPtr.Zero, ref pathChars);
        if (pathChars == 0) return (0, 0, "");
        var pathBuffer = Marshal.AllocHGlobal((int)(pathChars * 2));
        try
        {
            var written = GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, pathBuffer, ref pathChars);
            if (written == 0 || written == uint.MaxValue) return (0, 0, "");
            var path = Marshal.PtrToStringUni(pathBuffer) ?? "";
            var (vid, pid) = ParseVidPid(path);
            var entry = (vid, pid, path);
            lock (_deviceCacheLock)
            {
                _deviceCache[hDevice] = entry;
            }
            return entry;
        }
        finally
        {
            Marshal.FreeHGlobal(pathBuffer);
        }
    }

    // Parse `vid_NNNN&pid_NNNN` substrings out of a Windows HID device path.
    // Returns (0, 0) if not found. Case-insensitive; hex digits.
    private static (int vid, int pid) ParseVidPid(string path)
    {
        int vid = ExtractHexAfter(path, "vid_");
        int pid = ExtractHexAfter(path, "pid_");
        return (vid, pid);
    }

    private static int ExtractHexAfter(string s, string marker)
    {
        var idx = s.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return 0;
        var start = idx + marker.Length;
        var sb = new StringBuilder();
        for (int i = start; i < s.Length && sb.Length < 8; i++)
        {
            char c = s[i];
            if ((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))
                sb.Append(c);
            else break;
        }
        if (sb.Length == 0) return 0;
        return int.TryParse(sb.ToString(), System.Globalization.NumberStyles.HexNumber, null, out var v) ? v : 0;
    }

    private void DispatchReport(int vid, int pid, string path, byte[] report)
    {
        Subscription[] snapshot;
        lock (_subscriptionsLock)
        {
            snapshot = _subscriptions.ToArray();
        }
        bool matched = false;
        foreach (var sub in snapshot)
        {
            if (sub.VendorId == vid && sub.ProductId == pid)
            {
                matched = true;
                try { sub.Callback(path, report); }
                catch (Exception ex)
                {
                    DebugLogger.Log($"RawInputReceiver.DispatchReport: subscriber threw — {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        if (matched) Interlocked.Increment(ref _wmInputDispatchCount);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_hwnd != IntPtr.Zero)
        {
            // Unregister from raw input first so no further WM_INPUT messages
            // queue up while we're tearing down.
            try
            {
                var removeDevices = new[]
                {
                    new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0, dwFlags = RIDEV_REMOVE, hwndTarget = IntPtr.Zero },
                    new RAWINPUTDEVICE { usUsagePage = 0xFF00, usUsage = 0, dwFlags = RIDEV_REMOVE, hwndTarget = IntPtr.Zero },
                    new RAWINPUTDEVICE { usUsagePage = 0x0C, usUsage = 0, dwFlags = RIDEV_REMOVE, hwndTarget = IntPtr.Zero },
                };
                RegisterRawInputDevices(removeDevices, (uint)removeDevices.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
            }
            catch { }
        }

        if (_threadId != 0)
        {
            try { PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero); } catch { }
        }
        if (_thread.IsAlive)
        {
            try { _thread.Join(1500); } catch { }
        }
    }
}
