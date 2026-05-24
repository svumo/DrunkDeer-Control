using HidSharp;
using System.Collections.Concurrent;

namespace Driver;

// Alternative I/O path for gen-2 OEM keyboards where the Windows HID class
// driver silently filters interrupt-IN reports (VID 0x19F5 confirmed via
// two users 2026-05-23..24 across betas 1–11). After ruling out every
// user-space HID API, we route reads/writes through an embedded WebView2
// hosting a tiny HTML page that uses navigator.hid — the same Chromium
// HID stack that demonstrably reads these keyboards on the official web
// driver page.
//
// Same interface shape as Gen2KeyboardChannel so HidDeviceExtensions can
// route through either one transparently based on which channel is
// registered for the device path.
public sealed class Gen2WebHidChannel : IGen2Channel
{
    public const byte REPORT_ID = 0x04;

    private readonly IGen2WebHidTransport _transport;
    private readonly int _writeReportSize;
    private readonly ConcurrentQueue<byte[]> _inputQueue = new();
    private readonly object _writeLock = new();
    private bool _disposed;

    public string WriteDevicePath { get; }

    public Gen2WebHidChannel(IGen2WebHidTransport transport, HidDevice vendorWriteDevice)
    {
        _transport = transport;
        WriteDevicePath = vendorWriteDevice.DevicePath ?? "";
        int writeReportSize = 65;
        try { writeReportSize = vendorWriteDevice.GetMaxOutputReportLength(); } catch { }
        if (writeReportSize < 64) writeReportSize = 64;
        _writeReportSize = writeReportSize;

        _transport.InputReportReceived += OnInputReportReceived;
        _transport.Disconnected += OnDisconnected;
    }

    private void OnInputReportReceived(byte reportId, byte[] payload)
    {
        if (_disposed) return;
        // Caller's WritePacket strips the Report ID byte. Match that
        // contract here by NOT including the reportId in the queue —
        // payload is the wire bytes after the Report ID slot.
        _inputQueue.Enqueue(payload);
        DebugLogger.LogVerbose($"  <- (gen2 webhid, reportId=0x{reportId:x2}, {payload.Length} bytes) {payload.PacketToString()}");
    }

    private void OnDisconnected()
    {
        DebugLogger.Log("Gen2WebHidChannel: device disconnected event from transport");
    }

    // Sends a 63-byte protocol packet via the WebHID transport, then waits
    // up to pollMs for a response matching expectFirstByte. Mirrors
    // Gen2KeyboardChannel.WriteAndPoll semantics so HidDeviceExtensions
    // can call either interchangeably.
    public byte[] WriteAndPoll(byte[] packet, int pollMs = 1500, byte? expectFirstByte = null)
    {
        if (_disposed) return [];
        if (packet.Length < 1) return [];
        if (packet.Length > 63) throw new ArgumentException($"Packet length {packet.Length} > 63", nameof(packet));

        // Drain stale reports so we don't return a buffered response from a
        // prior write.
        int drained = 0;
        while (_inputQueue.TryDequeue(out _)) drained++;
        if (drained > 0) DebugLogger.LogVerbose($"  (drained {drained} stale webhid report(s) before write)");

        // Build the payload — sendReport(reportId, payload) where payload
        // is the 63 bytes AFTER the Report ID byte. We pad to (reportSize - 1)
        // to match the device's declared output length minus the Report ID.
        int payloadLen = Math.Max(_writeReportSize - 1, 63);
        var payload = new byte[payloadLen];
        Array.Copy(packet, 0, payload, 0, Math.Min(packet.Length, payloadLen));

        bool sentOk;
        lock (_writeLock)
        {
            DebugLogger.LogVerbose($"  -> (gen2 webhid sendReport, reportId=0x{REPORT_ID:x2}, {payload.Length} bytes) {payload.PacketToString()}");
            try
            {
                sentOk = _transport.SendReportAsync(REPORT_ID, payload).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"  WRITE FAILED (gen2 webhid): {ex.GetType().Name}: {ex.Message}");
                return [];
            }
        }
        if (!sentOk) return [];

        var deadline = DateTime.UtcNow.AddMilliseconds(pollMs);
        while (DateTime.UtcNow < deadline)
        {
            if (_inputQueue.TryDequeue(out var report))
            {
                if (PassesFilter(report, expectFirstByte))
                {
                    DebugLogger.LogVerbose($"  <- (gen2 webhid match, {report.Length} bytes) {report.PacketToString()}");
                    return report;
                }
            }
            Thread.Sleep(5);
        }

        DebugLogger.Log($"  READ FAILED (gen2 webhid): no matching response within {pollMs}ms");
        return [];
    }

    public bool WriteNoAck(byte[] packet)
    {
        if (_disposed) return false;
        if (packet.Length < 1) return false;
        if (packet.Length > 63) throw new ArgumentException($"Packet length {packet.Length} > 63", nameof(packet));

        int payloadLen = Math.Max(_writeReportSize - 1, 63);
        var payload = new byte[payloadLen];
        Array.Copy(packet, 0, payload, 0, Math.Min(packet.Length, payloadLen));

        lock (_writeLock)
        {
            DebugLogger.LogVerbose($"  -> (gen2 webhid sendReport no-ack, reportId=0x{REPORT_ID:x2}, {payload.Length} bytes) {payload.PacketToString()}");
            try
            {
                return _transport.SendReportAsync(REPORT_ID, payload).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"  WRITE FAILED (gen2 webhid no-ack): {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _transport.InputReportReceived -= OnInputReportReceived; } catch { }
        try { _transport.Disconnected -= OnDisconnected; } catch { }
    }

    private static bool PassesFilter(byte[] payload, byte? expectFirstByte)
    {
        if (expectFirstByte is byte expect && (payload.Length == 0 || payload[0] != expect)) return false;
        bool allZero = true;
        for (int i = 0; i < payload.Length; i++) { if (payload[i] != 0) { allZero = false; break; } }
        return !allZero;
    }
}
