using HidSharp;
using System.Runtime.InteropServices;

namespace Driver;

// Production I/O path for gen-2 keyboards whose firmware sends spec responses
// on a HID Keyboard or Mouse top-level collection that Windows blocks normal
// user-mode apps from reading via ReadFile / HidStream.Read (kernel-level
// anti-keylogger protection — admin doesn't lift it). The escape hatch
// Microsoft documents: open the handle with `dwDesiredAccess = 0`, which
// succeeds on blocked collections and gives you a handle valid for the
// `HidD_*` family of control-transfer APIs (SetOutputReport, GetInputReport,
// GetFeature). Chrome's WebHID falls back to this path automatically;
// HidSharpCore does not.
//
// Construction strategy:
// - Open the vendor write interface (typically `mi_01`) with full GENERIC
//   access via P/Invoke CreateFile.
// - For every sibling HID collection of the same VID/PID, try opening with
//   zero-access. The kbd/mouse collections that previously refused to open
//   should now succeed. Keep the handles for control-transfer reads.
// - Writes go via HidD_SetOutputReport on the vendor handle.
// - Reads poll HidD_GetInputReport across every read handle until either a
//   non-empty response arrives or the timeout expires.
public sealed class Gen2KeyboardChannel : IDisposable
{
    private const uint GENERIC_READ = 0x80000000u;
    private const uint GENERIC_WRITE = 0x40000000u;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [DllImport("hid.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool HidD_SetOutputReport(IntPtr hidDeviceObject, byte[] reportBuffer, int reportBufferLength);

    [DllImport("hid.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool HidD_GetInputReport(IntPtr hidDeviceObject, byte[] reportBuffer, int reportBufferLength);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    public const byte REPORT_ID = 0x04;

    private readonly IntPtr _writeHandle;
    private readonly (IntPtr handle, int reportSize, string pathTail)[] _readHandles;
    private readonly int _writeReportSize;
    private readonly object _writeLock = new();
    private bool _disposed;

    public string WriteDevicePath { get; }

    private Gen2KeyboardChannel(IntPtr writeHandle, (IntPtr handle, int reportSize, string pathTail)[] readHandles, int writeReportSize, string writeDevicePath)
    {
        _writeHandle = writeHandle;
        _readHandles = readHandles;
        _writeReportSize = writeReportSize;
        WriteDevicePath = writeDevicePath;
    }

    // Attempts to open a gen-2 channel rooted at the given vendor device.
    // `siblings` is the set of HID collections sharing the vendor device's
    // VID/PID — typically every collection of the keyboard, since we don't
    // yet know which one carries the response. The channel keeps zero-access
    // handles to all of them and polls them all on read.
    //
    // Returns null if the vendor write handle can't be opened at all (e.g.
    // device disappeared mid-probe). Returns a channel with zero read
    // handles if all siblings refused to open — caller should treat that as
    // "no useful channel" and not register it.
    public static Gen2KeyboardChannel? TryOpen(HidDevice vendorWriteDevice, IEnumerable<HidDevice> siblings)
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

        var readHandles = new List<(IntPtr handle, int reportSize, string pathTail)>();
        foreach (var sib in siblings)
        {
            var path = sib.DevicePath;
            if (string.IsNullOrEmpty(path)) continue;
            int sibInLen = 64;
            try { sibInLen = sib.GetMaxInputReportLength(); } catch { }

            // Zero-access open — succeeds on keyboard/mouse collections that
            // refuse a normal full-access open. The resulting handle can only
            // be used with HidD_* control-transfer APIs, but that's exactly
            // what we need.
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
            // Read buffer size: at least 64 so it can hold a 63-byte spec
            // payload + Report ID. For tiny collections (consumer controls,
            // 2-3 byte input), we still allocate 64 — HidD_GetInputReport
            // returns the actual data; the extra space is harmless.
            int readSize = Math.Max(sibInLen, 64);
            readHandles.Add((rh, readSize, ShortPath(path)));
            DebugLogger.Log($"Gen2KeyboardChannel.TryOpen: opened zero-access read handle on {ShortPath(path)} (reportSize={readSize})");
        }

        return new Gen2KeyboardChannel(
            writeHandle,
            readHandles.ToArray(),
            writeReportSize,
            writePath);
    }

    public int ReadHandleCount => _readHandles.Length;

    // Sends a 63-byte protocol packet via HidD_SetOutputReport (control
    // transfer), then polls HidD_GetInputReport across every open read
    // handle for up to `pollMs` milliseconds. Returns the response payload
    // (with the Report ID byte stripped, mirroring HidStream.Read's
    // behaviour in HidDeviceExtensions), or empty if no response arrived.
    //
    // Caller can pass `expectFirstByte` to filter responses — e.g. for an
    // identity request we expect a response starting with 0xA0. Polling
    // continues past stale buffered reports that don't match.
    public byte[] WriteAndPoll(byte[] packet, int pollMs = 1500, byte? expectFirstByte = null)
    {
        if (_disposed) return [];
        if (packet.Length < 1) return [];
        if (packet.Length > 63) throw new ArgumentException($"Packet length {packet.Length} > 63", nameof(packet));

        var buffer = new byte[_writeReportSize];
        buffer[0] = REPORT_ID;
        Array.Copy(packet, 0, buffer, 1, Math.Min(packet.Length, _writeReportSize - 1));

        bool writeOk;
        lock (_writeLock)
        {
            DebugLogger.LogVerbose($"  -> (gen2 SetOutputReport, {buffer.Length} bytes) {buffer.PacketToString()}");
            writeOk = HidD_SetOutputReport(_writeHandle, buffer, buffer.Length);
            if (!writeOk)
            {
                var err = Marshal.GetLastWin32Error();
                DebugLogger.Log($"  WRITE FAILED (gen2 SetOutputReport): Win32 err={err}");
                return [];
            }
        }

        // Poll all read handles in a tight loop. ~10ms sleep between
        // sweeps keeps CPU usage negligible while still being responsive.
        var deadline = DateTime.UtcNow.AddMilliseconds(pollMs);
        while (DateTime.UtcNow < deadline)
        {
            foreach (var (handle, size, tail) in _readHandles)
            {
                var rbuf = new byte[size];
                rbuf[0] = REPORT_ID;
                bool ok;
                try { ok = HidD_GetInputReport(handle, rbuf, rbuf.Length); }
                catch { continue; }
                if (!ok) continue;
                // First byte is the report ID. Skip it for the caller (matching
                // HidStream.Read semantics in HidDeviceExtensions which does
                // `response.Skip(1).ToArray()`).
                var payload = rbuf.AsSpan(1).ToArray();
                // Filter out unsolicited / stale reports that don't match the
                // expected protocol header.
                if (expectFirstByte is byte expect && payload.Length > 0 && payload[0] != expect)
                {
                    continue;
                }
                // Also skip all-zero buffers (no actual data).
                bool allZero = true;
                for (int i = 0; i < payload.Length; i++) { if (payload[i] != 0) { allZero = false; break; } }
                if (allZero) continue;
                DebugLogger.LogVerbose($"  <- (gen2 GetInputReport from {tail}, {payload.Length} bytes) {payload.PacketToString()}");
                return payload;
            }
            Thread.Sleep(10);
        }
        DebugLogger.Log($"  READ FAILED (gen2): no response from any of {_readHandles.Length} read handle(s) within {pollMs}ms");
        return [];
    }

    // Fire-and-forget write — no read, no ACK. Mirrors WritePacketNoAck
    // semantics in HidDeviceExtensions.
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
        if (_writeHandle != IntPtr.Zero && _writeHandle != INVALID_HANDLE_VALUE)
        {
            try { CloseHandle(_writeHandle); } catch { }
        }
        foreach (var (h, _, _) in _readHandles)
        {
            if (h != IntPtr.Zero && h != INVALID_HANDLE_VALUE)
            {
                try { CloseHandle(h); } catch { }
            }
        }
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
