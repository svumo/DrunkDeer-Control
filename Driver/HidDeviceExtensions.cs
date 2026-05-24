using HidSharp;
using System.Collections.Concurrent;

namespace Driver;

public static class HidDeviceExtensions
{
    // Per-device-path lookup of an active IGen2Channel. When a key
    // exists for a stream's underlying device path, all WritePacket /
    // WritePacketNoAck calls on that stream get redirected through the
    // control-transfer channel (HidD_SetOutputReport for writes,
    // HidD_GetInputReport polling across sibling collections for reads).
    // KeyboardManager populates this registry when it detects a gen-2
    // keyboard via Strategy K, and clears entries when the device
    // disconnects. The HidStream itself is still opened and held by the
    // caller (e.g. ProfileManager) — it remains valid for keep-alive and
    // device-presence checks even though we don't actually transmit data
    // through it for gen-2 devices.
    private static readonly ConcurrentDictionary<string, IGen2Channel> _gen2Channels = new();

    public static void RegisterGen2Channel(string devicePath, IGen2Channel channel)
    {
        if (string.IsNullOrEmpty(devicePath)) return;
        if (_gen2Channels.TryRemove(devicePath, out var old))
        {
            try { old.Dispose(); } catch { }
        }
        _gen2Channels[devicePath] = channel;
    }

    public static void UnregisterGen2Channel(string devicePath)
    {
        if (string.IsNullOrEmpty(devicePath)) return;
        if (_gen2Channels.TryRemove(devicePath, out var ch))
        {
            try { ch.Dispose(); } catch { }
        }
    }

    public static void ClearAllGen2Channels()
    {
        foreach (var key in _gen2Channels.Keys.ToList())
        {
            if (_gen2Channels.TryRemove(key, out var ch))
            {
                try { ch.Dispose(); } catch { }
            }
        }
    }

    public static IGen2Channel? TryGetGen2Channel(HidDevice device)
    {
        try
        {
            var path = device?.DevicePath;
            if (string.IsNullOrEmpty(path)) return null;
            return _gen2Channels.TryGetValue(path, out var ch) ? ch : null;
        }
        catch { return null; }
    }

    public static TResult Using<TResult, T>(
        this T factory,
        Func<T, TResult> use) where T : IDisposable
    {
        using var disposable = factory;
        return use(disposable);
    }

    public static string PacketToString(this byte[] packet)
        => string.Format("[{0}]", string.Join(" ", packet.Select(b => string.Format("{0:x2}", b))));

    public static bool WritePacket(this HidStream stream, byte[][] packets)
    {
        DebugLogger.Log($"WritePacket batch start ({packets.Length} packets)");
        int failed = 0;
        for (int i = 0; i < packets.Length; i++)
        {
            if (!stream.TryWritePacket(packets[i]))
            {
                DebugLogger.Log($"  packet {i}/{packets.Length} failed (continuing batch)");
                failed++;
            }
        }
        if (failed == 0)
            DebugLogger.Log($"WritePacket batch complete ({packets.Length} packets ok)");
        else
            DebugLogger.Log($"WritePacket batch complete ({failed}/{packets.Length} packets failed)");
        return failed == 0;
    }

    public static bool TryWritePacket(this HidStream stream, byte[] packet)
    {
        var response = stream.WritePacket(packet);
        if (response.Length > 0 && response.First() == packet[0]) return true;

        // The sync handle may be sharing the device with the keystroke-tracking
        // listener. Windows delivers every input report (including 0xB7 depth
        // chunks meant for the listener) to every open HID handle, so the
        // sync handle's queue can fill with unrelated packets between writes.
        // Drain them until we find the echo of the byte we just sent.
        for (int attempt = 0; attempt < 16; attempt++)
        {
            try
            {
                var raw = stream.Read();
                var trimmed = raw.Skip(1).ToArray();
                if (trimmed.Length > 0 && trimmed[0] == packet[0])
                {
                    DebugLogger.LogVerbose($"  <- {trimmed.PacketToString()} (drained {attempt + 1} unrelated packets)");
                    return true;
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"  drain READ FAILED: {ex.GetType().Name}: {ex.Message}");
                break;
            }
        }

        DebugLogger.Log($"  TryWritePacket mismatch: sent[0]=0x{packet[0]:x2} resp.len={response.Length} resp[0]={(response.Length > 0 ? $"0x{response[0]:x2}" : "n/a")} (drain exhausted)");
        return false;
    }

    // Builds the wire-level HID report buffer for an outgoing protocol packet.
    // Result is sized to the device's actual MaxOutputReportLength rather than
    // a hardcoded 64 bytes:
    //   - Gen-1 firmware: MaxOutputReportLength == 64 → buffer is [REPORT_ID, ...63 bytes...]
    //   - Gen-2 firmware: MaxOutputReportLength == 65 → buffer is [REPORT_ID, ...63 bytes..., 0]
    //
    // The gen-2 firmware (drunkdeer.keybord.net.cn lineage) defines its HID
    // output report with explicit Report ID 4 and 64 bytes of payload, totalling
    // 65 bytes on the wire. Sending only 64 bytes to a 65-byte report results
    // in the device silently dropping the packet (write succeeds, no response).
    // Reverse-engineered from index.QC8Mvgui.js sendIdentityData / sendReport
    // (see docs/keyboard-protocol-gen2.md).
    //
    // Gen-1 behaviour is unchanged: REPORT_ID was already 0x04 in our gen-1
    // path, and the buffer is still 64 bytes for VID 0x352D devices.
    private static byte[] BuildOutputReport(HidStream stream, byte[] packet)
    {
        int reportSize;
        try { reportSize = stream.Device.GetMaxOutputReportLength(); }
        catch { reportSize = 64; }
        if (reportSize < 64) reportSize = 64;
        var buffer = new byte[reportSize];
        buffer[0] = Packets.REPORT_ID;
        var copyLen = Math.Min(packet.Length, reportSize - 1);
        Array.Copy(packet, 0, buffer, 1, copyLen);
        return buffer;
    }

    // Fire-and-forget write — no read, no ACK validation. Use for command
    // packets that the firmware does not echo back (e.g. the 0xFC / 0xFD
    // outlier toggles for Keystroke Tracking, Last-Win Replace, AutoMatch).
    // Returns true if the underlying HID write didn't throw.
    //
    // Gen-2 devices: routed through the registered IGen2Channel,
    // which uses HidD_SetOutputReport (control transfer) instead of
    // ReadFile/WriteFile against the output endpoint. See
    // IGen2Channel.cs for why this is required.
    public static bool WritePacketNoAck(this HidStream stream, byte[] packet)
    {
        if (packet.Length < 1) return false;
        if (packet.Length > 63)
        {
            throw new Exception(string.Format("Packet {0}, probably should be of length < 64", PacketToString(packet)));
        }

        var gen2 = TryGetGen2Channel(stream.Device);
        if (gen2 is not null)
        {
            return gen2.WriteNoAck(packet);
        }

        DebugLogger.LogVerbose($"  -> {packet.PacketToString()} (no-ack)");
        try
        {
            stream.Write(BuildOutputReport(stream, packet));
            return true;
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"  WRITE FAILED: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public static byte[] WritePacket(this HidStream stream, byte[] packet)
    {
        if (packet.Length < 1) return [];
        if (packet.Length > 63)
        {
            throw new Exception(string.Format("Packet {0}, probably should be of length < 64", PacketToString(packet)));
        }

        var gen2 = TryGetGen2Channel(stream.Device);
        if (gen2 is not null)
        {
            // Gen-2 firmware echoes back via control transfer. For ACK-style
            // packets (identity probe, per-key write), we expect the response
            // to mirror the command byte; the caller compares response[0] to
            // packet[0]. Don't filter by expectFirstByte here — let the caller
            // do that. 1500ms poll matches the existing TryWritePacket drain
            // loop's effective timeout.
            return gen2.WriteAndPoll(packet, pollMs: 1500, expectFirstByte: null);
        }

        DebugLogger.LogVerbose($"  -> {packet.PacketToString()}");
        try
        {
            stream.Write(BuildOutputReport(stream, packet));
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"  WRITE FAILED: {ex.GetType().Name}: {ex.Message}");
            return [];
        }
        try
        {
            var response = stream.Read();
            var trimmed = response.Skip(1).ToArray();
            DebugLogger.LogVerbose($"  <- {trimmed.PacketToString()}");
            return trimmed;
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"  READ FAILED: {ex.GetType().Name}: {ex.Message}");
        }
        return [];
    }

    // Walks the structured FullProfilePackets bundle in the exact order the
    // firmware expects — see Packets.BuildFullProfilePackets for the sequence
    // rationale. Centralized here so ProfileManager.PushCurrentProfile and the
    // keyboard view's Sync button can share one implementation and not drift
    // apart. Returns false if any ACK'd batch failed; fire-and-forget packets
    // don't affect the return value (firmware doesn't echo them).
    public static (bool ok, bool disconnected) WriteFullProfile(this HidStream stream, Packets.FullProfilePackets bundle)
    {
        try
        {
            // PreQuiesce (LW/RDT off) first — calms the firmware's deconflict
            // pipeline so the remap-table rewrite below doesn't trigger ghost
            // keystrokes from partially-written pair-member slots. See
            // Packets.BuildFullProfilePackets for the rationale.
            if (bundle.PreQuiesce is not null) stream.WritePacketNoAck(bundle.PreQuiesce);
            // PrePassClearRtpUpper — extra `aa 00 01` BEFORE the remap stream
            // flushes stale rtpNumber→HID mappings the firmware retained from
            // a previous profile push. Without it, switching profiles can
            // leave Y release emitting the previous profile's T HID code.
            if (bundle.PrePassClearRtpUpper is not null) stream.WritePacketNoAck(bundle.PrePassClearRtpUpper);
            bool remapOk = bundle.Remap.Length == 0 || stream.WritePacket(bundle.Remap);
            // Re-send group=1 immediately before ClearRtpUpper when LW/RDT pairs
            // are active — mirrors JS rtpSaveToKeyboard which re-flushes the
            // KeyType=4-modified group=1 packets right before the 0xAA sentinel.
            if (bundle.RtpRemapReflush is not null) stream.WritePacket(bundle.RtpRemapReflush);
            if (bundle.ClearRtpUpper is not null) stream.WritePacketNoAck(bundle.ClearRtpUpper);
            bool rtpAuthOk = bundle.RtpAuthority.Length == 0 || stream.WritePacket(bundle.RtpAuthority);
            // ClearRtp BEFORE EarlyCommonSwitch — purge the firmware's stale
            // pair-table cache from the previous profile (or previous RDT
            // toggle-on) BEFORE re-enabling the LW/RDT mode bit. Previously
            // the order was inverted (EarlyCS first, then ClearRtp), which
            // briefly activated the new mode against the old pair table —
            // user-visible during profile switches as "press the new
            // profile's press key, get the OLD profile's release HID for
            // ~600ms until the commit re-push fixes it" (the R→U from R→T
            // flicker observed 2026-05-21).
            //
            // For LW: clear-old → enable-mode → install-new-pairs is the
            // safer sequencing (firmware still sees mode-on when LwPairs
            // lands; firmware-side intent preserved).
            // For RDT: no LwPairs follows; Type-4 entries already installed
            // by the remap stream above, so by the time RDT mode flips on
            // the new pair table is in place.
            // ClearRtpAll — defensive `fc 0a 00` wipe of the firmware's
            // pair table BEFORE the mode-specific ClearRtp lands. Without
            // it, profile-switching can leave stale rtpNumber→HID-code
            // mappings so the new profile's pair fires the OLD profile's
            // release HID. Two-step wipe-then-set is more reliable than
            // a single mode-set on 0.017+. See
            // Packets.FullProfilePackets.ClearRtpAll for the full rationale.
            if (bundle.ClearRtpAll is not null) stream.WritePacketNoAck(bundle.ClearRtpAll);
            if (bundle.ClearRtp is not null) stream.WritePacketNoAck(bundle.ClearRtp);
            // EarlyCommonSwitch — sent AFTER ClearRtp (see above) but BEFORE
            // LwPairs / AckedBatch so the firmware sees the LW/RDT mode bits
            // set when subsequent pair-table writes land. Without this the
            // firmware is still in PreQuiesce state and silently ignores
            // the pair-table packet. Trailing CS in AckedBatch re-asserts
            // the same state at end-of-sync.
            if (bundle.EarlyCommonSwitch is not null) stream.WritePacketNoAck(bundle.EarlyCommonSwitch);
            // LwPairs (fc 01) — registers Last-Win pair table when LW is on.
            if (bundle.LwPairs is not null) stream.WritePacketNoAck(bundle.LwPairs);
            // RdtPairs (fc 03) — registers Release-Dual-Trigger pair table
            // with per-pair active/reset thresholds. Added v2.4.0 after
            // re-examining the newer keybord.net.cn bundle (see
            // docs/protocol-findings-keybord-net-cn.md). Without this the
            // firmware's pair table is half-configured: RDT works on the
            // first pair after a fresh power cycle but stale pair-table
            // entries leak across profile-switches, producing the
            // phantom-fires-on-release symptom observed 2026-05-22.
            //
            // If both LW and RDT are enabled simultaneously (rare —
            // CommonSwitch byte 10 = 3), we send both packets back-to-back
            // and let the firmware register each pair table independently.
            if (bundle.RdtPairs is not null) stream.WritePacketNoAck(bundle.RdtPairs);
            bool ackedOk = bundle.AckedBatch.Length == 0 || stream.WritePacket(bundle.AckedBatch);
            foreach (var p in bundle.FireForget) stream.WritePacketNoAck(p);
            return (ok: ackedOk && remapOk && rtpAuthOk, disconnected: false);
        }
        catch (Exception ex)
        {
            bool disconnected = ex.GetType().Name == "DeviceIOException";
            DebugLogger.Log($"WriteFullProfile: {(disconnected ? "device unavailable" : "exception")} — {ex.GetType().Name}: {ex.Message}");
            return (ok: false, disconnected: disconnected);
        }
    }

    public static bool Ping(this HidStream stream)
        => stream.TryWritePacket(Packets.IDENTITY_PACKET);

    public static KeyboardSpecs GetKeyboardSpecs(this HidStream stream)
        => new(stream.WritePacket(Packets.IDENTITY_PACKET));

    public static bool IsCompatible(this KeyboardSpecs specs)
        => specs.KeyboardType is not null;
}
