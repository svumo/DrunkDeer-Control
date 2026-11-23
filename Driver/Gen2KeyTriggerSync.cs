using HidSharp;

namespace Driver;

// Per-key actuation + Rapid Trigger sync for the OEM A75 Ultra (VID 0x19F5 /
// PID 0xFB3E), gen-2 0x55 gateway.
//
// Read-modify-write only. The keyboard's KeyTrigger region (128 slots x 8
// bytes per profile) is read first; for each key the app wants to change, only
// the actuation, rt_press and rt_release fields are patched and every other
// bit (key_mode, priority, dead-zones, switch type) is written back as read.
// Only slots that actually differ are written, one 0x55 0xA1 single-slot write
// each (the write shape verified on the tester's Ultra by the beta.4 one-key
// test), and the region is read back afterwards to confirm every change.
//
// Units: "display" values in 0.01 mm (stored on the wire as value - 1, LE9).
public static class Gen2KeyTriggerSync
{
    public const int ApMin = 10, ApMax = 330;   // 0.10 .. 3.30 mm
    public const int RtMin = 1, RtMax = 310;    // 0.01 .. 3.10 mm

    public readonly record struct KeyValues(int Actuation, int RtPress, int RtRelease);

    // What the app wants for one key. Null fields are left as the keyboard has them.
    public readonly record struct Desired(int KeyIndex, byte Hid, int? Actuation, int? RtPress, int? RtRelease);

    public sealed record Change(int KeyIndex, byte Hid, int Slot, byte[] Before, byte[] After);

    public sealed record ApplyResult(bool Ok, int Written, int Verified, string Message);

    // ---- pure record helpers -------------------------------------------------

    public static KeyValues Decode(ReadOnlySpan<byte> rec) => new(
        (rec[2] | ((rec[3] & 0x01) << 8)) + 1,
        (rec[4] | ((rec[5] & 0x01) << 8)) + 1,
        (rec[6] | ((rec[7] & 0x01) << 8)) + 1);

    public static bool LooksLikeRecord(ReadOnlySpan<byte> rec)
        => rec.Length == KeyTriggerEntry.BYTE_SIZE && (rec[0] & 0xF0) == 0xA0;

    // Copy of rec with only the given LE9 fields replaced. Byte 3/5/7 keep
    // bits 1..7 (precision / dead-zone fields); bytes 0, 1 are untouched.
    public static byte[] Patch(ReadOnlySpan<byte> rec, int? actuation, int? rtPress, int? rtRelease)
    {
        if (rec.Length != KeyTriggerEntry.BYTE_SIZE)
            throw new ArgumentException($"record must be {KeyTriggerEntry.BYTE_SIZE} bytes", nameof(rec));
        var o = rec.ToArray();
        if (actuation is int a) SetLe9(o, 2, Math.Clamp(a, ApMin, ApMax));
        if (rtPress is int p) SetLe9(o, 4, Math.Clamp(p, RtMin, RtMax));
        if (rtRelease is int r) SetLe9(o, 6, Math.Clamp(r, RtMin, RtMax));
        return o;
    }

    private static void SetLe9(byte[] o, int lo, int display)
    {
        int stored = display - 1;
        o[lo] = (byte)(stored & 0xFF);
        o[lo + 1] = (byte)((o[lo + 1] & 0xFE) | ((stored >> 8) & 0x01));
    }

    public static ReadOnlySpan<byte> RecordAt(byte[] region, int slot)
        => region.AsSpan(slot * PacketsGen2.KEY_TRIGGER_RECORD_SIZE, PacketsGen2.KEY_TRIGGER_RECORD_SIZE);

    // Slots whose patched record differs from what the keyboard has now.
    // Keys with no firmware slot, or whose current record doesn't look valid,
    // are skipped (never written).
    public static List<Change> Plan(byte[] region, Func<byte, int> slotForHid, IEnumerable<Desired> desired)
    {
        var changes = new List<Change>();
        var seen = new HashSet<int>();
        foreach (var d in desired)
        {
            if (d.Hid == 0) continue;
            int slot = slotForHid(d.Hid);
            if (slot < 0 || slot >= PacketsGen2.KEY_TRIGGER_SLOT_COUNT || !seen.Add(slot)) continue;
            var before = RecordAt(region, slot);
            if (!LooksLikeRecord(before)) continue;
            var after = Patch(before, d.Actuation, d.RtPress, d.RtRelease);
            if (!before.SequenceEqual(after))
                changes.Add(new Change(d.KeyIndex, d.Hid, slot, before.ToArray(), after));
        }
        return changes;
    }

    // ---- keyboard I/O (call off the UI thread) -------------------------------

    public static KeyMatrixSlotMap? ReadSlotMap(HidStream stream)
    {
        var matrix = new byte[PacketsGen2.KEY_MATRIX_REGION_SIZE];
        int offset = 0;
        foreach (var request in PacketsGen2.BuildReadDefaultKeyMatrixSequence(profileIndex: 0, layer: 0))
        {
            byte len = request[4];
            var resp = ReadGateway(stream, request, PacketsGen2.SUB_READ_DEFAULT_KEY_MATRIX, len);
            if (resp is null) return null;
            Array.Copy(resp, PacketsGen2.RESPONSE_DATA_OFFSET, matrix, offset, len);
            offset += len;
            Thread.Sleep(2);
        }
        var map = KeyMatrixSlotMap.BuildFromMatrix(matrix);
        DebugLogger.Log($"Gen2KeyTriggerSync: slot map {map.StandardKeyCount} keys; A=slot{map.TryGetSlotForHid(0x04)} S=slot{map.TryGetSlotForHid(0x16)} W=slot{map.TryGetSlotForHid(0x1A)}");
        return map;
    }

    // Reads the profile's 1024-byte KeyTrigger region in 56-byte chunks.
    public static byte[]? ReadRegion(HidStream stream, byte profileIndex)
    {
        var region = new byte[PacketsGen2.KEY_TRIGGER_REGION_SIZE];
        int baseAddr = profileIndex * PacketsGen2.KEY_TRIGGER_REGION_SIZE;
        for (int off = 0; off < region.Length; off += 56)
        {
            byte len = (byte)Math.Min(56, region.Length - off);
            int addr = baseAddr + off;
            if (addr > 0xFFFF) return null;
            var req = PacketsGen2.BuildReadRequest(PacketsGen2.SUB_READ_KEY_TRIGGER, len, (ushort)addr);
            var resp = ReadGateway(stream, req, PacketsGen2.SUB_READ_KEY_TRIGGER, len);
            if (resp is null)
            {
                DebugLogger.Log($"Gen2KeyTriggerSync: region read failed at 0x{addr:x4}");
                return null;
            }
            Array.Copy(resp, PacketsGen2.RESPONSE_DATA_OFFSET, region, off, len);
            Thread.Sleep(2);
        }
        return region;
    }

    public static ApplyResult Apply(HidStream stream, byte profileIndex, IReadOnlyList<Change> changes)
    {
        if (changes.Count == 0) return new ApplyResult(true, 0, 0, "Already in sync, nothing to write.");
        int written = 0;
        foreach (var c in changes)
        {
            DebugLogger.Log($"Gen2KeyTriggerSync: key {c.KeyIndex} hid 0x{c.Hid:x2} slot {c.Slot}: {c.Before.PacketToString()} -> {c.After.PacketToString()}");
            var pkt = PacketsGen2.BuildWriteKeyTriggerSingleSlot(c.Slot, profileIndex, c.After);
            if (!stream.WritePacketNoAck(pkt))
                return new ApplyResult(false, written, 0, $"Write failed after {written} of {changes.Count} keys.");
            written++;
            Thread.Sleep(8);
        }
        Thread.Sleep(100);

        var region = ReadRegion(stream, profileIndex);
        if (region is null)
            return new ApplyResult(false, written, 0, $"Wrote {written} keys but couldn't read them back to check.");
        int verified = 0;
        foreach (var c in changes)
        {
            var now = RecordAt(region, c.Slot);
            if (now.SequenceEqual(c.After)) verified++;
            else DebugLogger.Log($"Gen2KeyTriggerSync: VERIFY MISMATCH slot {c.Slot}: expected {c.After.PacketToString()} got {now.ToArray().PacketToString()}");
        }
        DebugLogger.Log($"Gen2KeyTriggerSync: wrote {written}, verified {verified}/{changes.Count}");
        return verified == changes.Count
            ? new ApplyResult(true, written, verified, $"Synced {written} key(s); the keyboard confirmed all of them.")
            : new ApplyResult(false, written, verified, $"Wrote {written} key(s) but only {verified} read back as expected.");
    }

    // Sends a read request; accepts only a 0xAA response for the same
    // sub-command and address. Up to 3 attempts.
    public static byte[]? ReadGateway(HidStream stream, byte[] request, byte subCmd, byte length)
    {
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            var resp = stream.WritePacket(request);
            if (resp.Length >= PacketsGen2.RESPONSE_DATA_OFFSET + length
                && PacketsGen2.IsExtendedGatewayResponse(resp, subCmd)
                && resp[5] == request[5] && resp[6] == request[6])
                return resp;
            DebugLogger.Log($"Gen2KeyTriggerSync: read 0x{subCmd:x2} @0x{request[6]:x2}{request[5]:x2} attempt {attempt} got {resp.PacketToString()}");
            Thread.Sleep(20);
        }
        return null;
    }
}
