using System;
using System.Threading;
using System.Threading.Tasks;
using HidSharp;

namespace Driver;

// ---------------------------------------------------------------------------
// Keystroke-tracking stream listener.
//
// Streaming format — decoded from the official driver bundle
// (`C:\Users\skdes\AppData\Local\Temp\dd-index.js`, identifiers `watch_ap_rt`,
// `updateKeyHeight`, `xC`). There are TWO live-depth packet shapes; we accept
// both so the same listener works on firmwares with and without high-
// precision tracking enabled.
//
// Each incoming HID input report is 64 bytes total. Byte 0 is the report ID
// (0x04 on DrunkDeer); bytes [1..] are the "packet" the JS sees as `e[0..]`.
// The packet IDs and byte maps below are stated in those packet-relative
// offsets (i.e. matching the JS `A.getUint8(N)`).
//
// ─── Low-precision live tracking — packet ID 0xB7 (183) ────────────────────
//   e[0]  = 0xB7
//   e[1]  = (unused by JS)
//   e[2]  = (unused by JS)
//   e[3]  = chunk_idx     // 0 → slots 0..58 (59 keys)
//                         // 1 → slots 59..117 (59 keys)
//                         // 2 → slots 118..125 (8 keys)
//   e[4..] = depth_byte per slot, count depends on chunk_idx
//   depth_mm = depth_byte / 10.0
//   Dead-zone: depth_byte < 2 ⇒ depth_mm = 0
//
//   Example raw packet (60 byte data starting at e[4], shown chunk_idx=0,
//   slot 17 ['e' on A75 Pro] pressed ~1.5 mm, everything else released):
//     04  B7 00 00 00  00 00 00 00 00 00 00 00 00 00 00  00 00 00 00 00 00 00 00 0F  00 00 ...
//                 ^idx                                              ^slot17 = 15 -> 1.5mm
//
// ─── High-precision live tracking — packet ID 0xFD (253), sub-cmd 6 ────────
//   e[0]  = 0xFD
//   e[1]  = 0x06           // sub-cmd: live-tracking high-precision
//   e[2]  = chunk_idx      // 0..3 → 30 keys/packet, 4 → tail of 6 keys
//   e[3..] = (depth_lo, depth_hi) pairs, little-endian uint16
//   depth_mm = (lo | (hi << 8)) / 200.0
//   Dead-zone: raw < 40 ⇒ depth_mm = 0
//
//   Example raw packet (chunk_idx=0, slot 4 pressed at depth=400 → 2.0mm):
//     04  FD 06 00  00 00 00 00 90 01 00 00 ... 00
//                              ^lo ^hi  (0x0190 = 400 → 400/200 = 2.0 mm)
//
// Either format produces (slot_index, depth_mm) events; consumers don't need
// to know which packet shape produced them.
// ---------------------------------------------------------------------------

public sealed class KeyDepthEventArgs(int slotIndex, double depthMm) : EventArgs
{
    public int SlotIndex { get; } = slotIndex;
    public double DepthMm { get; } = depthMm;
}

public sealed class HidStreamListener : IDisposable
{
    // Max value caps (in mm) — anything above this is treated as bogus and
    // dropped. The switches max out around ~4 mm; the JS clamps to Yg=3.1
    // for high-precision, Ng*200 = 600 raw for the limiter. We use 4.0 mm
    // as a wide safety net so unusual firmwares don't truncate legit data.
    private const double MaxDepthMm = 4.0;

    private readonly HidStream _stream;
    private readonly CancellationTokenSource _cts = new();
    private Task? _readLoop;
    private int _started;   // 0/1, guards against double Start()
    private bool _disposed;

    public event EventHandler<KeyDepthEventArgs>? DepthChanged;

    public HidStreamListener(HidStream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        DebugLogger.Log("HidStreamListener: starting read loop");
        _readLoop = Task.Run(() => ReadLoop(_cts.Token));
    }

    public async Task StopAsync()
    {
        if (_started == 0) return;
        DebugLogger.Log("HidStreamListener: stopping read loop");
        try { _cts.Cancel(); } catch { }
        // Forcibly unblock the read by closing the stream — HidSharp's
        // synchronous Read has no cancellation, so we have to drop the
        // underlying handle to wake it up.
        try { _stream.Close(); } catch { }

        if (_readLoop is { } t)
        {
            try
            {
                await Task.WhenAny(t, Task.Delay(500)).ConfigureAwait(false);
            }
            catch { }
        }
        DebugLogger.Log("HidStreamListener: stopped");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts.Cancel(); } catch { }
        try { _stream.Close(); } catch { }
        try { _stream.Dispose(); } catch { }
        try { _cts.Dispose(); } catch { }
    }

    // ---- internals --------------------------------------------------------

    private void ReadLoop(CancellationToken ct)
    {
        // Allocate a single buffer once. HidSharp's HidStream.Read() returns
        // a fresh array on every call, so we just consume that.
        while (!ct.IsCancellationRequested)
        {
            byte[] buf;
            try
            {
                buf = _stream.Read();
            }
            catch (Exception ex)
            {
                // Cancellation causes Close() which surfaces as IOException/
                // ObjectDisposedException — both expected. Log+exit either way.
                if (!ct.IsCancellationRequested)
                    DebugLogger.Log($"HidStreamListener: read failed — {ex.GetType().Name}: {ex.Message}");
                return;
            }

            if (buf.Length < 5) continue;

            // buf[0] = report id (0x04). Packet body starts at buf[1] which
            // matches what the JS sees as e[0].
            byte packetId = buf[1];
            try
            {
                if (packetId == 0xB7)
                    ParseLowPrecision(buf);
                else if (packetId == 0xFD && buf.Length >= 6 && buf[2] == 0x06)
                    ParseHighPrecision(buf);
                // Everything else (sync ACKs, spec responses, etc.) is silently
                // ignored. We don't want to fight other consumers of the stream.
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"HidStreamListener: parse error — {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    // ── 0xB7 — single byte per slot, scale = 1/10 mm/unit ─────────────────
    private void ParseLowPrecision(byte[] buf)
    {
        // Layout (buf-relative): [0]=reportId, [1]=0xB7, [2..3]=unused,
        // [4]=chunk_idx, [5..]=depths.
        if (buf.Length < 6) return;
        byte chunkIdx = buf[4];
        int baseSlot;
        int count;
        switch (chunkIdx)
        {
            case 0: baseSlot = 0;   count = 59; break;
            case 1: baseSlot = 59;  count = 59; break;
            case 2: baseSlot = 118; count = 8;  break;
            default: return;
        }

        int dataStart = 5;
        int available = buf.Length - dataStart;
        if (count > available) count = available;

        var handler = DepthChanged;
        if (handler == null) return;

        for (int i = 0; i < count; i++)
        {
            byte w = buf[dataStart + i];
            double mm = w < 2 ? 0.0 : w / 10.0;
            if (mm > MaxDepthMm) mm = MaxDepthMm;
            handler(this, new KeyDepthEventArgs(baseSlot + i, mm));
        }
    }

    // ── 0xFD/0x06 — uint16 LE per slot, scale = 1/200 mm/unit ─────────────
    private void ParseHighPrecision(byte[] buf)
    {
        // Layout (buf-relative): [0]=reportId, [1]=0xFD, [2]=0x06,
        // [3]=chunk_idx, [4..]=(lo,hi) pairs.
        if (buf.Length < 5) return;
        byte chunkIdx = buf[3];
        // JS: chunk_idx > 3 → tail of 6 keys; else 30 keys.
        int baseSlot = chunkIdx * 30;
        int count = chunkIdx > 3 ? 6 : 30;

        int dataStart = 4;
        int pairs = (buf.Length - dataStart) / 2;
        if (count > pairs) count = pairs;

        var handler = DepthChanged;
        if (handler == null) return;

        for (int i = 0; i < count; i++)
        {
            int lo = buf[dataStart + i * 2];
            int hi = buf[dataStart + i * 2 + 1];
            int raw = lo | (hi << 8);
            double mm = raw < 40 ? 0.0 : raw / 200.0;
            if (mm > MaxDepthMm) mm = MaxDepthMm;
            handler(this, new KeyDepthEventArgs(baseSlot + i, mm));
        }
    }
}
