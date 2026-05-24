using HidSharp;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Driver;

// Production I/O path for gen-2 keyboards whose firmware sends spec responses
// on an interface that HidSharp's HidStream.Read can't pick up. Common cause
// on the gen-2 A75 Pro OEM variant (VID 0x19F5 / PID 0xFB5C, observed
// 2026-05-23): the vendor write interface mi_01 declares Generic-Desktop
// usagePage=0x01 + usage=0x00 with Constant-flagged Input/Output fields, and
// the Windows HID class driver appears to drop the constant input reports
// rather than dispatching them to ReadFile listeners — unlike vendor-defined
// (usagePage=0xFF00) interfaces on the gen-1 A75 Pro where ReadFile works
// fine. Chrome's WebHID receives these reports normally; HidSharpCore does
// not. See docs/gen2-oem-investigation.md.
//
// Strategy (in priority order on read):
//   1. Native async overlapped ReadFile on mi_01 (full GENERIC_READ |
//      GENERIC_WRITE, FILE_FLAG_OVERLAPPED). Background reader thread loops
//      on ReadFile waiting on an event handle, queues completed reads. This
//      mirrors Chrome's HidConnectionWin path exactly and is the most
//      likely-to-work read mechanism.
//   2. HidD_GetInputReport polling across zero-access sibling handles, as
//      fallback. Important: rbuf[0] must be 0 (NOT the Report ID we send
//      with) when the descriptor declares no explicit Report ID — per MSDN,
//      asking for a nonzero Report ID on a descriptor that doesn't declare
//      one causes ERROR_GEN_FAILURE (Win32 err 31), which is exactly what
//      beta.5 saw.
//
// Writes always go via HidD_SetOutputReport on the vendor write handle
// (control transfer) — proven to reach the firmware since beta.5.
public sealed class Gen2KeyboardChannel : IGen2Channel
{
    private const uint GENERIC_READ = 0x80000000u;
    private const uint GENERIC_WRITE = 0x40000000u;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    private const uint WAIT_OBJECT_0 = 0;
    private const uint WAIT_TIMEOUT = 0x102;
    private const uint INFINITE = 0xFFFFFFFF;
    private const int ERROR_IO_PENDING = 997;
    private const int ERROR_OPERATION_ABORTED = 995;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [DllImport("hid.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool HidD_SetOutputReport(IntPtr hidDeviceObject, byte[] reportBuffer, int reportBufferLength);

    [DllImport("hid.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool HidD_GetInputReport(IntPtr hidDeviceObject, byte[] reportBuffer, int reportBufferLength);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateEvent(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ResetEvent(IntPtr hEvent);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetEvent(IntPtr hEvent);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForMultipleObjects(uint nCount, IntPtr[] lpHandles, bool bWaitAll, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe bool ReadFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToRead, out uint lpNumberOfBytesRead, NativeOverlapped* lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe bool GetOverlappedResult(IntPtr hFile, NativeOverlapped* lpOverlapped, out uint lpNumberOfBytesTransferred, bool bWait);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe bool CancelIoEx(IntPtr hFile, NativeOverlapped* lpOverlapped);

    public const byte REPORT_ID = 0x04;

    private readonly IntPtr _writeHandle;
    private readonly IntPtr _asyncReadHandle;
    private readonly IntPtr _readEvent;
    private readonly IntPtr _cancelEvent;
    private readonly int _asyncReadSize;
    private readonly Thread? _readerThread;
    private readonly ConcurrentQueue<byte[]> _asyncInputQueue = new();
    private readonly ConcurrentQueue<byte[]> _rawInputQueue = new();
    private readonly (IntPtr handle, int reportSize, string pathTail)[] _zeroAccessHandles;
    private readonly int _writeReportSize;
    private readonly object _writeLock = new();
    private readonly IDisposable? _rawInputSubscription;
    private volatile bool _readerShouldExit;
    private bool _disposed;

    public string WriteDevicePath { get; }
    public bool HasAsyncReader => _asyncReadHandle != IntPtr.Zero;
    public bool HasRawInput => _rawInputSubscription is not null;
    public int ReadHandleCount => _zeroAccessHandles.Length;

    private Gen2KeyboardChannel(
        IntPtr writeHandle,
        IntPtr asyncReadHandle,
        IntPtr readEvent,
        IntPtr cancelEvent,
        int asyncReadSize,
        (IntPtr handle, int reportSize, string pathTail)[] zeroAccessHandles,
        int writeReportSize,
        string writeDevicePath,
        RawInputReceiver? rawInputReceiver,
        int rawInputVid,
        int rawInputPid)
    {
        _writeHandle = writeHandle;
        _asyncReadHandle = asyncReadHandle;
        _readEvent = readEvent;
        _cancelEvent = cancelEvent;
        _asyncReadSize = asyncReadSize;
        _zeroAccessHandles = zeroAccessHandles;
        _writeReportSize = writeReportSize;
        WriteDevicePath = writeDevicePath;

        if (_asyncReadHandle != IntPtr.Zero)
        {
            _readerThread = new Thread(ReaderLoop)
            {
                IsBackground = true,
                Name = "Gen2KeyboardChannel.AsyncReader"
            };
            _readerThread.Start();
        }

        if (rawInputReceiver is not null && rawInputReceiver.IsReady && rawInputVid != 0 && rawInputPid != 0)
        {
            _rawInputSubscription = rawInputReceiver.Subscribe(rawInputVid, rawInputPid, OnRawInputReport);
            DebugLogger.Log($"Gen2KeyboardChannel: raw-input subscribed for VID=0x{rawInputVid:x4} PID=0x{rawInputPid:x4}");
        }
    }

    // Callback invoked on the RawInputReceiver's message-pump thread.
    // Enqueue the report for the next WriteAndPoll cycle. Path is logged
    // so we can see which top-level collection actually carried the
    // response — useful diagnostic for understanding the firmware.
    private void OnRawInputReport(string path, byte[] report)
    {
        if (_disposed) return;
        _rawInputQueue.Enqueue(report);
        DebugLogger.LogVerbose($"  <- (raw input from {ShortPath(path)}, {report.Length} bytes) {report.PacketToString()}");
    }

    // Attempts to open a gen-2 channel rooted at the given vendor device.
    // Opens the vendor mi_01 device twice:
    //   - Once with full access (no OVERLAPPED) for HidD_SetOutputReport writes
    //   - Once with full access + FILE_FLAG_OVERLAPPED for async ReadFile loop
    // Also opens every sibling collection with zero-access for
    // HidD_GetInputReport fallback polling.
    //
    // If a RawInputReceiver is provided, the channel also subscribes to it
    // for WM_INPUT-based reads — the only path that may work on OEM
    // variants whose HidClass.sys filters interrupt-IN reports (gen-2 A75
    // Pro VID 0x19F5 confirmed via two users 2026-05-23..24).
    //
    // Returns null if the vendor write handle can't be opened at all.
    public static Gen2KeyboardChannel? TryOpen(HidDevice vendorWriteDevice, IEnumerable<HidDevice> siblings, RawInputReceiver? rawInputReceiver = null)
    {
        var writePath = vendorWriteDevice.DevicePath;
        if (string.IsNullOrEmpty(writePath))
        {
            DebugLogger.Log("Gen2KeyboardChannel.TryOpen: vendor device path is empty");
            return null;
        }

        int writeReportSize = 65;
        try { writeReportSize = vendorWriteDevice.GetMaxOutputReportLength(); } catch { }
        if (writeReportSize < 64) writeReportSize = 64;

        int readReportSize = 65;
        try { readReportSize = vendorWriteDevice.GetMaxInputReportLength(); } catch { }
        if (readReportSize < 64) readReportSize = 64;

        var writeHandle = CreateFile(
            writePath,
            GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            0,
            IntPtr.Zero);
        if (writeHandle == INVALID_HANDLE_VALUE)
        {
            var err = Marshal.GetLastWin32Error();
            DebugLogger.Log($"Gen2KeyboardChannel.TryOpen: CreateFile (vendor write) failed Win32 err={err}");
            return null;
        }

        // Second handle on mi_01 with OVERLAPPED for the async ReadFile loop.
        // Mirrors Chrome HidConnectionWin's read path. If this open fails
        // (rare — same path, full access), we still return a usable channel
        // that relies on GetInputReport polling.
        IntPtr asyncReadHandle = IntPtr.Zero;
        IntPtr readEvent = IntPtr.Zero;
        IntPtr cancelEvent = IntPtr.Zero;
        var asyncCandidate = CreateFile(
            writePath,
            GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            FILE_FLAG_OVERLAPPED,
            IntPtr.Zero);
        if (asyncCandidate == INVALID_HANDLE_VALUE)
        {
            var err = Marshal.GetLastWin32Error();
            DebugLogger.Log($"Gen2KeyboardChannel.TryOpen: async-read CreateFile failed Win32 err={err} (continuing with polling-only path)");
        }
        else
        {
            var readEv = CreateEvent(IntPtr.Zero, true, false, null);
            var cancelEv = CreateEvent(IntPtr.Zero, true, false, null);
            if (readEv == IntPtr.Zero || cancelEv == IntPtr.Zero)
            {
                var err = Marshal.GetLastWin32Error();
                DebugLogger.Log($"Gen2KeyboardChannel.TryOpen: CreateEvent failed Win32 err={err} (continuing with polling-only path)");
                CloseHandle(asyncCandidate);
                if (readEv != IntPtr.Zero) CloseHandle(readEv);
                if (cancelEv != IntPtr.Zero) CloseHandle(cancelEv);
            }
            else
            {
                asyncReadHandle = asyncCandidate;
                readEvent = readEv;
                cancelEvent = cancelEv;
                DebugLogger.Log($"Gen2KeyboardChannel.TryOpen: opened async ReadFile handle on mi_01 (reportSize={readReportSize})");
            }
        }

        var zeroAccessHandles = new List<(IntPtr handle, int reportSize, string pathTail)>();
        foreach (var sib in siblings)
        {
            var path = sib.DevicePath;
            if (string.IsNullOrEmpty(path)) continue;
            int sibInLen = 64;
            try { sibInLen = sib.GetMaxInputReportLength(); } catch { }

            // Zero-access open — succeeds on keyboard/mouse collections that
            // refuse a normal full-access open. The resulting handle can only
            // be used with HidD_* control-transfer APIs.
            var rh = CreateFile(
                path,
                0,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                0,
                IntPtr.Zero);
            if (rh == INVALID_HANDLE_VALUE)
            {
                var err = Marshal.GetLastWin32Error();
                DebugLogger.Log($"Gen2KeyboardChannel.TryOpen: zero-access CreateFile failed on {ShortPath(path)} (Win32 err={err}) — skipping");
                continue;
            }
            int readSize = Math.Max(sibInLen, 64);
            zeroAccessHandles.Add((rh, readSize, ShortPath(path)));
            DebugLogger.Log($"Gen2KeyboardChannel.TryOpen: opened zero-access read handle on {ShortPath(path)} (reportSize={readSize})");
        }

        return new Gen2KeyboardChannel(
            writeHandle,
            asyncReadHandle,
            readEvent,
            cancelEvent,
            readReportSize,
            zeroAccessHandles.ToArray(),
            writeReportSize,
            writePath,
            rawInputReceiver,
            vendorWriteDevice.VendorID,
            vendorWriteDevice.ProductID);
    }

    // Background reader thread. Loops on async ReadFile. When data arrives,
    // queues a copy of the buffer for WriteAndPoll to consume. Exits on
    // cancellation (Dispose sets _readerShouldExit and signals _cancelEvent).
    private unsafe void ReaderLoop()
    {
        DebugLogger.LogVerbose("Gen2KeyboardChannel.ReaderLoop: started");
        var buffer = new byte[_asyncReadSize];
        var overlapped = new NativeOverlapped();
        overlapped.EventHandle = _readEvent;

        var waitHandles = new IntPtr[] { _readEvent, _cancelEvent };

        while (!_readerShouldExit)
        {
            ResetEvent(_readEvent);
            // Re-initialize overlapped struct each iteration (kernel may modify it).
            overlapped = new NativeOverlapped { EventHandle = _readEvent };

            bool ok = ReadFile(_asyncReadHandle, buffer, (uint)buffer.Length, out uint bytesRead, &overlapped);

            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                if (err != ERROR_IO_PENDING)
                {
                    DebugLogger.Log($"Gen2KeyboardChannel.ReaderLoop: ReadFile failed (Win32 err={err}), exiting reader");
                    break;
                }

                // Wait for either completion or cancellation.
                uint waitResult = WaitForMultipleObjects(2, waitHandles, false, INFINITE);
                if (waitResult == WAIT_OBJECT_0 + 1)
                {
                    // Cancellation requested. Cancel pending I/O and exit.
                    CancelIoEx(_asyncReadHandle, &overlapped);
                    // Wait briefly for the cancellation to complete so the
                    // overlapped struct is safe to deallocate.
                    GetOverlappedResult(_asyncReadHandle, &overlapped, out _, true);
                    break;
                }
                if (waitResult != WAIT_OBJECT_0)
                {
                    DebugLogger.Log($"Gen2KeyboardChannel.ReaderLoop: WaitForMultipleObjects returned {waitResult}, exiting reader");
                    break;
                }

                if (!GetOverlappedResult(_asyncReadHandle, &overlapped, out bytesRead, false))
                {
                    int gerr = Marshal.GetLastWin32Error();
                    if (gerr == ERROR_OPERATION_ABORTED) break;
                    DebugLogger.Log($"Gen2KeyboardChannel.ReaderLoop: GetOverlappedResult failed (Win32 err={gerr})");
                    continue;
                }
            }

            if (bytesRead == 0) continue;

            var copy = new byte[bytesRead];
            Array.Copy(buffer, 0, copy, 0, (int)bytesRead);
            _asyncInputQueue.Enqueue(copy);
            DebugLogger.LogVerbose($"  <- (gen2 async ReadFile, {bytesRead} bytes) {copy.PacketToString()}");
        }

        DebugLogger.LogVerbose("Gen2KeyboardChannel.ReaderLoop: exited");
    }

    // Sends a 63-byte protocol packet via HidD_SetOutputReport, then waits up
    // to `pollMs` for a response via either:
    //   (a) the async ReadFile queue populated by ReaderLoop, or
    //   (b) HidD_GetInputReport polling across zero-access sibling handles
    //       (with rbuf[0]=0, since the mi_01 descriptor declares no Report ID
    //       — see class-level comment).
    //
    // Returns the response payload with the Report ID byte stripped (mirroring
    // HidStream.Read semantics in HidDeviceExtensions which does
    // response.Skip(1).ToArray()), or empty if no response arrived.
    //
    // `expectFirstByte` filters out stale/unsolicited reports.
    public byte[] WriteAndPoll(byte[] packet, int pollMs = 1500, byte? expectFirstByte = null)
    {
        if (_disposed) return [];
        if (packet.Length < 1) return [];
        if (packet.Length > 63) throw new ArgumentException($"Packet length {packet.Length} > 63", nameof(packet));

        // Drain stale reports from both queues before sending. Anything
        // queued before this call is unrelated to the response we're about
        // to wait for.
        int drainedAsync = 0;
        while (_asyncInputQueue.TryDequeue(out _)) drainedAsync++;
        int drainedRaw = 0;
        while (_rawInputQueue.TryDequeue(out _)) drainedRaw++;
        if (drainedAsync > 0 || drainedRaw > 0)
            DebugLogger.LogVerbose($"  (drained {drainedAsync} async + {drainedRaw} raw-input report(s) before write)");

        var buffer = new byte[_writeReportSize];
        buffer[0] = REPORT_ID;
        Array.Copy(packet, 0, buffer, 1, Math.Min(packet.Length, _writeReportSize - 1));

        lock (_writeLock)
        {
            DebugLogger.LogVerbose($"  -> (gen2 SetOutputReport, {buffer.Length} bytes) {buffer.PacketToString()}");
            var ok = HidD_SetOutputReport(_writeHandle, buffer, buffer.Length);
            if (!ok)
            {
                var err = Marshal.GetLastWin32Error();
                DebugLogger.Log($"  WRITE FAILED (gen2 SetOutputReport): Win32 err={err}");
                return [];
            }
        }

        var deadline = DateTime.UtcNow.AddMilliseconds(pollMs);
        int getInputAttempts = 0;
        int getInputSuccesses = 0;
        int rawInputSeen = 0;
        int asyncSeen = 0;
        while (DateTime.UtcNow < deadline)
        {
            // Path 1: raw-input queue (highest-priority — bypasses HidClass.sys
            // filtering, the only path observed to work for this OEM variant).
            if (_rawInputQueue.TryDequeue(out var rawReport))
            {
                rawInputSeen++;
                var payload = StripReportId(rawReport);
                if (PassesFilter(payload, expectFirstByte))
                {
                    DebugLogger.LogVerbose($"  <- (gen2 raw-input match, {payload.Length} bytes) {payload.PacketToString()}");
                    return payload;
                }
            }

            // Path 2: async ReadFile queue.
            if (_asyncInputQueue.TryDequeue(out var asyncReport))
            {
                asyncSeen++;
                var payload = StripReportId(asyncReport);
                if (PassesFilter(payload, expectFirstByte))
                {
                    DebugLogger.LogVerbose($"  <- (gen2 async ReadFile match, {payload.Length} bytes) {payload.PacketToString()}");
                    return payload;
                }
            }

            // Path 3: HidD_GetInputReport polling across zero-access handles.
            // rbuf[0] = 0 matches the mi_01 descriptor's no-Report-ID layout.
            foreach (var (handle, size, tail) in _zeroAccessHandles)
            {
                var rbuf = new byte[size];
                rbuf[0] = 0;
                getInputAttempts++;
                bool gok;
                try { gok = HidD_GetInputReport(handle, rbuf, rbuf.Length); }
                catch { continue; }
                if (!gok) continue;
                getInputSuccesses++;

                var payload = StripReportId(rbuf);
                if (PassesFilter(payload, expectFirstByte))
                {
                    DebugLogger.LogVerbose($"  <- (gen2 GetInputReport from {tail}, {payload.Length} bytes) {payload.PacketToString()}");
                    return payload;
                }
            }

            Thread.Sleep(10);
        }

        DebugLogger.Log($"  READ FAILED (gen2): no response within {pollMs}ms (raw-input {rawInputSeen} report(s){(HasRawInput ? "" : " [no receiver]")}; async-read {asyncSeen} report(s){(HasAsyncReader ? "" : " [no reader]")}; GetInputReport {getInputSuccesses}/{getInputAttempts} succeeded across {_zeroAccessHandles.Length} handle(s))");
        return [];
    }

    // Fire-and-forget write — no read, no ACK.
    public bool WriteNoAck(byte[] packet)
    {
        if (_disposed) return false;
        if (packet.Length < 1) return false;
        if (packet.Length > 63) throw new ArgumentException($"Packet length {packet.Length} > 63", nameof(packet));

        var buffer = new byte[_writeReportSize];
        buffer[0] = REPORT_ID;
        Array.Copy(packet, 0, buffer, 1, Math.Min(packet.Length, _writeReportSize - 1));

        lock (_writeLock)
        {
            DebugLogger.LogVerbose($"  -> (gen2 SetOutputReport no-ack, {buffer.Length} bytes) {buffer.PacketToString()}");
            var ok = HidD_SetOutputReport(_writeHandle, buffer, buffer.Length);
            if (!ok)
            {
                var err = Marshal.GetLastWin32Error();
                DebugLogger.Log($"  WRITE FAILED (gen2 SetOutputReport no-ack): Win32 err={err}");
            }
            return ok;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Unsubscribe from raw input first so no further callbacks queue up.
        try { _rawInputSubscription?.Dispose(); } catch { }

        // Signal the reader thread to exit and wait for it.
        _readerShouldExit = true;
        if (_cancelEvent != IntPtr.Zero) SetEvent(_cancelEvent);
        if (_readerThread is not null)
        {
            try { _readerThread.Join(1000); } catch { }
        }

        if (_writeHandle != IntPtr.Zero && _writeHandle != INVALID_HANDLE_VALUE)
        {
            try { CloseHandle(_writeHandle); } catch { }
        }
        if (_asyncReadHandle != IntPtr.Zero && _asyncReadHandle != INVALID_HANDLE_VALUE)
        {
            try { CloseHandle(_asyncReadHandle); } catch { }
        }
        if (_readEvent != IntPtr.Zero) try { CloseHandle(_readEvent); } catch { }
        if (_cancelEvent != IntPtr.Zero) try { CloseHandle(_cancelEvent); } catch { }
        foreach (var (h, _, _) in _zeroAccessHandles)
        {
            if (h != IntPtr.Zero && h != INVALID_HANDLE_VALUE)
            {
                try { CloseHandle(h); } catch { }
            }
        }
    }

    private static byte[] StripReportId(byte[] raw) => raw.AsSpan(1).ToArray();

    private static bool PassesFilter(byte[] payload, byte? expectFirstByte)
    {
        if (expectFirstByte is byte expect && (payload.Length == 0 || payload[0] != expect)) return false;
        bool allZero = true;
        for (int i = 0; i < payload.Length; i++) { if (payload[i] != 0) { allZero = false; break; } }
        return !allZero;
    }

    private static string ShortPath(string p)
    {
        try
        {
            var first = p.IndexOf('#');
            if (first < 0) return p;
            var second = p.IndexOf('#', first + 1);
            if (second < 0) return p[(first + 1)..];
            return p[(first + 1)..second];
        }
        catch { return p; }
    }
}
