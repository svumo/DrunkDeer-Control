using HidSharp;

namespace Driver;

public static class HidDeviceExtensions
{
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
                    DebugLogger.Log($"  <- {trimmed.PacketToString()} (drained {attempt + 1} unrelated packets)");
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

    // Fire-and-forget write — no read, no ACK validation. Use for command
    // packets that the firmware does not echo back (e.g. the 0xFC / 0xFD
    // outlier toggles for Keystroke Tracking, Last-Win Replace, AutoMatch).
    // Returns true if the underlying HID write didn't throw.
    public static bool WritePacketNoAck(this HidStream stream, byte[] packet)
    {
        if (packet.Length < 1) return false;
        if (packet.Length > 63)
        {
            throw new Exception(string.Format("Packet {0}, probably should be of length < 64", PacketToString(packet)));
        }
        DebugLogger.Log($"  -> {packet.PacketToString()} (no-ack)");
        try
        {
            stream.Write([Packets.REPORT_ID, .. packet]);
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
        DebugLogger.Log($"  -> {packet.PacketToString()}");
        try
        {
            stream.Write([Packets.REPORT_ID, .. packet]);
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
            DebugLogger.Log($"  <- {trimmed.PacketToString()}");
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
            // EarlyCommonSwitch — sent BEFORE the pair table so the firmware
            // sees RT=on + LW/RDT bits set when it processes create-pair.
            // Without this, the firmware is still in PreQuiesce state (LW=off,
            // RDT=off) and silently ignores the pair table. Trailing CS in
            // AckedBatch then re-asserts the same state at end-of-sync.
            if (bundle.EarlyCommonSwitch is not null) stream.WritePacketNoAck(bundle.EarlyCommonSwitch);
            // ClearRtp + LwPairs nullable as of 2.2.0 — see Packets.cs
            // FullProfilePackets record. Pre-2.2.0 always sent them even when
            // LW was off, which doesn't match the web driver's observed
            // behaviour (tools/captures/0x17/initial-connect.log) and on some
            // firmwares clobbers per-key state. RDT mode has no create-pair
            // packet at all (the official driver doesn't send fc 03 either).
            if (bundle.ClearRtp is not null) stream.WritePacketNoAck(bundle.ClearRtp);
            if (bundle.LwPairs is not null) stream.WritePacketNoAck(bundle.LwPairs);
            bool ackedOk = stream.WritePacket(bundle.AckedBatch);
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
