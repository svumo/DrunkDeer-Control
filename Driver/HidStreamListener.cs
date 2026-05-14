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
    private int _rearmSuppressed; // 0/1, set during a profile sync — Pause()/Resume()
    private bool _disposed;

    public event EventHandler<KeyDepthEventArgs>? DepthChanged;
    // Fired after the final chunk of a 0xB7 low-precision tracking cycle
    // (chunkIdx == 2, slots 118..125). The parent is expected to re-arm
    // the firmware by re-sending the enable packet after ~20ms — without
    // this, the firmware pushes one round of 3 chunks and goes idle. The
    // official JS bundle's `setTimeout(() => $B(), 20)` does the same.
    public event EventHandler? TrackingCycleComplete;

    public HidStreamListener(HidStream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        DebugLogger.Log("HidStreamListener: starting read loop");
        // Block reads forever — we wake the loop by closing the stream
        // from Stop()/Dispose(). The default HidSharp timeout (≈3 s)
        // surfaces as TimeoutException when sync writes are hogging the
        // device, which would otherwise tear down our depth subscription.
        try { _stream.ReadTimeout = System.Threading.Timeout.Infinite; } catch { }
        _readLoop = Task.Run(() => ReadLoop(_cts.Token));
    }

    // Suppress the TrackingCycleComplete re-arm signal. The firmware-side
    // stream is independently stopped by the caller (sending b6 03 00);
    // this flag prevents any chunk already in flight from kicking a stray
    // re-arm and undoing that. The read loop itself keeps running — it
    // just blocks in Read() until firmware streaming resumes.
    public void Pause()
    {
        if (Interlocked.Exchange(ref _rearmSuppressed, 1) == 0)
            DebugLogger.Log("HidStreamListener: pause (re-arm suppressed)");
    }

    public void Resume()
    {
        if (Interlocked.Exchange(ref _rearmSuppressed, 0) == 1)
            DebugLogger.Log("HidStreamListener: resume");
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
        // Diagnostic: log up to 50 received packet IDs per Start() session so
        // we can see what the firmware actually streams during a key press.
        // Sync-echo IDs (0xA0, 0xA5..0xA8, 0xB5, 0xB6 sub=0x03, 0xFC, 0xFD)
        // are filtered from the log so they don't burn the budget — the same
        // HID interface delivers every input report to every open handle,
        // including the sync-writer's echoes, which dwarf the real stream.
        int diagLogged = 0;
        const int diagBudget = 50;

        // Allocate a single buffer once. HidSharp's HidStream.Read() returns
        // a fresh array on every call, so we just consume that.
        while (!ct.IsCancellationRequested)
        {
            byte[] buf;
            try
            {
                buf = _stream.Read();
            }
            catch (TimeoutException)
            {
                // Transient: firmware was busy answering sync writes on a
                // parallel HID handle and didn't push anything in time. Just
                // try again. (Should be rare now that we set the timeout to
                // Infinite in Start(), but kept defensively.)
                continue;
            }
            catch (Exception ex)
            {
                // Cancellation closes the stream which surfaces as IOException/
                // ObjectDisposedException — both expected. Log+exit either way.
                if (!ct.IsCancellationRequested)
                    DebugLogger.Log($"HidStreamListener: read failed — {ex.GetType().Name}: {ex.Message}");
                return;
            }

            if (buf.Length < 5) continue;

            // buf[0] = report id (0x04). Packet body starts at buf[1] which
            // matches what the JS sees as e[0].
            byte packetId = buf[1];
            byte sub = buf.Length > 2 ? buf[2] : (byte)0;

            bool isSyncEcho =
                packetId == 0xA0 || packetId == 0xA5 || packetId == 0xA7 || packetId == 0xA8 ||
                packetId == 0xAA || packetId == 0xB5 || packetId == 0xFC ||
                (packetId == 0xB6 && sub == 0x03) ||
                (packetId == 0xFD && (sub == 0x03 || sub == 0x0C));

            if (!isSyncEcho && diagLogged < diagBudget)
            {
                diagLogged++;
                int dump = Math.Min(buf.Length, 24);
                var hex = new System.Text.StringBuilder(dump * 3);
                for (int i = 0; i < dump; i++) hex.Append($"{buf[i]:x2} ");
                DebugLogger.Log($"HidStreamListener: rx[{diagLogged}/{diagBudget}] len={buf.Length} id=0x{packetId:x2} sub=0x{sub:x2}  {hex.ToString().TrimEnd()}");
            }

            try
            {
                if (packetId == 0xB7)
                    ParseLowPrecision(buf);
                else if (packetId == 0xFD && buf.Length >= 6 && buf[2] == 0x06)
                    ParseHighPrecision(buf);
                // Everything else (sync ACKs, spec responses, etc.) is silently
                // ignored. The speculative 0xB6-inbound parser was removed —
                // it was lighting up every key on unrelated packets. Once we
                // see the real depth packet's ID/shape in the diag log we
                // can add a precise parser.
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

        // Diagnostic: log the slot of the first non-zero depth we ever see
        // (per chunk). If the user presses keys but this never fires, the
        // firmware isn't reporting depths for this device/firmware combo
        // even though tracking is "on". If it fires, parser + slot mapping
        // are correct and we can focus on UI plumbing.
        int firstNonZeroSlot = -1;
        byte firstNonZeroRaw = 0;
        for (int i = 0; i < count; i++)
        {
            byte w = buf[dataStart + i];
            if (firstNonZeroSlot < 0 && w >= 2) { firstNonZeroSlot = baseSlot + i; firstNonZeroRaw = w; }
            double mm = w < 2 ? 0.0 : w / 10.0;
            if (mm > MaxDepthMm) mm = MaxDepthMm;
            handler(this, new KeyDepthEventArgs(baseSlot + i, mm));
        }
        if (firstNonZeroSlot >= 0)
            DebugLogger.Log($"HidStreamListener: 0xB7 chunk {chunkIdx} non-zero depth at slot {firstNonZeroSlot} raw={firstNonZeroRaw} (~{firstNonZeroRaw / 10.0:F1} mm)");

        // chunkIdx == 2 is the final chunk of a single firmware push cycle.
        // Signal the parent so it can re-send the enable packet (firmware
        // only streams ONE round of 3 chunks per enable — re-issue keeps
        // the stream alive). Suppress while paused — see Pause().
        if (chunkIdx == 2 && Volatile.Read(ref _rearmSuppressed) == 0)
        {
            try { TrackingCycleComplete?.Invoke(this, EventArgs.Empty); }
            catch (Exception ex) { DebugLogger.Log($"HidStreamListener: cycle-complete handler threw — {ex.Message}"); }
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
