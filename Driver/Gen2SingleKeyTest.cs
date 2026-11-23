using HidSharp;

namespace Driver;

// One-key, opt-in test write for detection-only gen-2 boards (OEM A75 Ultra,
// VID 0x19F5 / PID 0xFB3E). Changes ONLY the actuation field of the A key's
// 8-byte KeyTrigger record on the keyboard's active profile, then reads it back.
//
// Read-modify-write: the record is read from the keyboard first and every
// other byte is written back unchanged. The Ultra's records differ from the
// A75 Pro's (byte 1 key_mode = 1, byte 7 release dead-zone = 15), so building
// a record from scratch the way the A75 Pro sync does would reset those.
//
// Toggles 1.50 mm <-> 1.00 mm: running it a second time puts the key back.
public static class Gen2SingleKeyTest
{
    public const byte HidUsageA = 0x04;
    public const int TestActuation = 100;    // 1.00 mm (display units, 0.01 mm)
    public const int RestoreActuation = 150; // 1.50 mm, the Ultra's stock value

    public sealed record Result(bool Ok, string Message, int? Slot = null, int? Before = null, int? After = null);

    // Actuation in display units (0.01 mm): LE9 in bytes 2..3, stored = value - 1.
    public static int ReadActuation(ReadOnlySpan<byte> entry)
    {
        if (entry.Length < KeyTriggerEntry.BYTE_SIZE) throw new ArgumentException("entry too short", nameof(entry));
        return (entry[2] | ((entry[3] & 0x01) << 8)) + 1;
    }

    // Copy of the record with only the actuation changed: byte 2 and bit 0 of
    // byte 3. Bits 1..7 of byte 3 and every other byte are preserved.
    public static byte[] WithActuation(ReadOnlySpan<byte> entry, int actuation)
    {
        if (entry.Length != KeyTriggerEntry.BYTE_SIZE)
            throw new ArgumentException($"entry must be {KeyTriggerEntry.BYTE_SIZE} bytes", nameof(entry));
        if (actuation < 1 || actuation > 512)
            throw new ArgumentOutOfRangeException(nameof(actuation));
        var copy = entry.ToArray();
        int stored = actuation - 1;
        copy[2] = (byte)(stored & 0xFF);
        copy[3] = (byte)((copy[3] & 0xFE) | ((stored >> 8) & 0x01));
        return copy;
    }

    public static int NextTarget(int current) => current == TestActuation ? RestoreActuation : TestActuation;

    // A plausible KeyTrigger record: high nibble of byte 0 is the 0xA constant.
    public static bool LooksLikeRecord(ReadOnlySpan<byte> entry)
        => entry.Length == KeyTriggerEntry.BYTE_SIZE && (entry[0] & 0xF0) == 0xA0;

    public static Result Run(HidStream stream, byte profileIndex)
    {
        DebugLogger.Log($"Gen2SingleKeyTest: start (profile {profileIndex})");

        // 1. Where does A live? Read the firmware's default key matrix rather
        //    than guess (the A75 Pro OEM puts A at slot 37, not its KeyIndex).
        var matrix = new byte[PacketsGen2.KEY_MATRIX_REGION_SIZE];
        int offset = 0;
        foreach (var request in PacketsGen2.BuildReadDefaultKeyMatrixSequence(profileIndex: 0, layer: 0))
        {
            byte len = request[4];
            var resp = ReadGateway(stream, request, PacketsGen2.SUB_READ_DEFAULT_KEY_MATRIX, len);
            if (resp is null) return Fail("couldn't read the key map from the keyboard; nothing was written.");
            Array.Copy(resp, PacketsGen2.RESPONSE_DATA_OFFSET, matrix, offset, len);
            offset += len;
            Thread.Sleep(2);
        }
        var slotMap = KeyMatrixSlotMap.BuildFromMatrix(matrix);
        int slot = slotMap.TryGetSlotForHid(HidUsageA);
        DebugLogger.Log($"Gen2SingleKeyTest: slot map {slotMap.StandardKeyCount} keys / {slotMap.SlotCount} slots; A = slot {slot}");
        if (slot < 0 || slot >= PacketsGen2.KEY_TRIGGER_SLOT_COUNT)
            return Fail("couldn't find the A key in the keyboard's key map; nothing was written.");

        // 2. Read A's current record.
        int addr = profileIndex * PacketsGen2.KEY_TRIGGER_REGION_SIZE + slot * PacketsGen2.KEY_TRIGGER_RECORD_SIZE;
        var before = ReadRecord(stream, addr);
        if (before is null) return Fail("couldn't read the A key's settings; nothing was written.", slot);
        if (!LooksLikeRecord(before))
            return Fail($"the A key's settings didn't look as expected ({before.PacketToString()}); nothing was written.", slot);
        int current = ReadActuation(before);
        int target = NextTarget(current);
        var next = WithActuation(before, target);
        DebugLogger.Log($"Gen2SingleKeyTest: A slot {slot} addr 0x{addr:x4} before={before.PacketToString()} ({current / 100.0:F2} mm) -> {next.PacketToString()} ({target / 100.0:F2} mm)");

        // 3. Write that one record (is_last = 1 commits it).
        var packet = PacketsGen2.BuildWriteKeyTriggerSingleSlot(slot, profileIndex, next);
        if (!stream.WritePacketNoAck(packet))
            return Fail("the write didn't go through.", slot, current);
        Thread.Sleep(100);

        // 4. Read it back.
        var after = ReadRecord(stream, addr);
        if (after is null) return Fail("wrote the A key but couldn't read it back.", slot, current);
        int afterAct = ReadActuation(after);
        bool match = after.AsSpan().SequenceEqual(next);
        DebugLogger.Log($"Gen2SingleKeyTest: readback={after.PacketToString()} ({afterAct / 100.0:F2} mm) match={match}");
        return match
            ? new Result(true, $"A key changed from {current / 100.0:F2} mm to {afterAct / 100.0:F2} mm, and the keyboard confirmed it.", slot, current, afterAct)
            : new Result(false, $"wrote {target / 100.0:F2} mm but the keyboard reports {afterAct / 100.0:F2} mm.", slot, current, afterAct);
    }

    private static byte[]? ReadRecord(HidStream stream, int addr)
    {
        if (addr < 0 || addr > 0xFFFF) return null;
        var req = PacketsGen2.BuildReadRequest(PacketsGen2.SUB_READ_KEY_TRIGGER, (byte)PacketsGen2.KEY_TRIGGER_RECORD_SIZE, (ushort)addr);
        var resp = ReadGateway(stream, req, PacketsGen2.SUB_READ_KEY_TRIGGER, (byte)PacketsGen2.KEY_TRIGGER_RECORD_SIZE);
        if (resp is null) return null;
        var rec = new byte[PacketsGen2.KEY_TRIGGER_RECORD_SIZE];
        Array.Copy(resp, PacketsGen2.RESPONSE_DATA_OFFSET, rec, 0, rec.Length);
        return rec;
    }

    // Sends a read request and accepts only a 0xAA response for the same
    // sub-command and address. Up to 3 attempts.
    private static byte[]? ReadGateway(HidStream stream, byte[] request, byte subCmd, byte length)
    {
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            var resp = stream.WritePacket(request);
            if (resp.Length >= PacketsGen2.RESPONSE_DATA_OFFSET + length
                && PacketsGen2.IsExtendedGatewayResponse(resp, subCmd)
                && resp[5] == request[5] && resp[6] == request[6])
                return resp;
            DebugLogger.Log($"Gen2SingleKeyTest: read 0x{subCmd:x2} @0x{request[6]:x2}{request[5]:x2} attempt {attempt} got {resp.PacketToString()}");
            Thread.Sleep(20);
        }
        return null;
    }

    private static Result Fail(string why, int? slot = null, int? before = null)
    {
        DebugLogger.Log($"Gen2SingleKeyTest: FAILED — {why}");
        return new Result(false, why, slot, before);
    }
}
