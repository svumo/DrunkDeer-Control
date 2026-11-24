using HidSharp;

namespace Driver;

// OEM A75 Ultra (VID 0x19F5 / PID 0xFB3E) "Settings page" switches, which live
// in the FuncBlock area read from address 0 (sub 0x05). Mapped 2026-11-23 from
// the tester toggling each one on the official site and diffing reads:
//   0x06 bit0 Lock Win (by elimination), bit1 Lock Alt+Tab, bit2 Lock Alt+F4
//   0x07 bit0 Adaptive Stable
// Tick rate (0x04 0x20), anti-shake (0x07 0x40) and polling rate (0x04 low
// bits) are known by one value each, so they are left alone.
//
// Writing this area is new (beta): every write is read back, a narrow write is
// tried first, then the full block, and the original bytes are restored if
// neither sticks.
public static class Gen2UltraSettings
{
    public const int BlockLength = 64;
    public const int LocksByte = 0x06;
    public const int AdaptiveByte = 0x07;

    public enum Setting { LockWin, LockAltTab, LockAltF4, AdaptiveStable }

    public sealed record Values(bool LockWin, bool LockAltTab, bool LockAltF4, bool AdaptiveStable);

    public sealed record WriteResult(bool Ok, Values? Now, string Message);

    private static (int Byte, byte Bit) Where(Setting s) => s switch
    {
        Setting.LockWin => (LocksByte, 0x01),
        Setting.LockAltTab => (LocksByte, 0x02),
        Setting.LockAltF4 => (LocksByte, 0x04),
        Setting.AdaptiveStable => (AdaptiveByte, 0x01),
        _ => throw new ArgumentOutOfRangeException(nameof(s)),
    };

    public static Values Decode(ReadOnlySpan<byte> block) => new(
        (block[LocksByte] & 0x01) != 0,
        (block[LocksByte] & 0x02) != 0,
        (block[LocksByte] & 0x04) != 0,
        (block[AdaptiveByte] & 0x01) != 0);

    public static bool Get(Values v, Setting s) => s switch
    {
        Setting.LockWin => v.LockWin,
        Setting.LockAltTab => v.LockAltTab,
        Setting.LockAltF4 => v.LockAltF4,
        _ => v.AdaptiveStable,
    };

    // Returns a copy with exactly one bit changed.
    public static byte[] Patch(ReadOnlySpan<byte> block, Setting s, bool on)
    {
        var o = block.ToArray();
        var (b, bit) = Where(s);
        o[b] = on ? (byte)(o[b] | bit) : (byte)(o[b] & ~bit);
        return o;
    }

    public static byte[]? Read(HidStream stream)
    {
        var block = Gen2KeyTriggerSync.ReadArea(stream, PacketsGen2.SUB_READ_FUNC_BLOCK, 0, BlockLength);
        return block.Length == BlockLength ? block : null;
    }

    public static WriteResult Set(HidStream stream, Setting s, bool on)
    {
        var before = Read(stream);
        if (before is null) return new(false, null, "Couldn't read the keyboard's settings. Nothing was changed.");
        var after = Patch(before, s, on);
        var (b, _) = Where(s);
        Gen2KeyTriggerSync.Learn($"SETTINGS: {s} -> {(on ? "on" : "off")}: byte 0x{b:x2} 0x{before[b]:x2} -> 0x{after[b]:x2}");
        if (after[b] == before[b]) return new(true, Decode(before), "Already set.");

        // 1) Narrow: just bytes 0x04..0x07, committed with is_last.
        Send(stream, PacketsGen2.BuildWriteFuncBlockChunk(after.AsSpan(4, 4), 0x0004, isLast: true), "narrow");
        var now = Read(stream);
        if (now is not null && now.AsSpan().SequenceEqual(after))
            return Done(now, "narrow");

        // 2) Full block: 56 + 8 bytes, the second chunk commits (the layout the
        //    other gen-2 boards use for their FuncBlock window).
        Send(stream, PacketsGen2.BuildWriteFuncBlockChunk(after.AsSpan(0, 56), 0x0000, isLast: false), "full 1/2");
        Send(stream, PacketsGen2.BuildWriteFuncBlockChunk(after.AsSpan(56, 8), 0x0038, isLast: true), "full 2/2");
        now = Read(stream);
        if (now is not null && now.AsSpan().SequenceEqual(after))
            return Done(now, "full");

        // 3) Neither stuck: put back exactly what was there.
        Gen2KeyTriggerSync.Learn($"SETTINGS: not confirmed (readback {(now is null ? "failed" : now.PacketToString())}); restoring original");
        if (now is not null && !now.AsSpan().SequenceEqual(before))
        {
            Send(stream, PacketsGen2.BuildWriteFuncBlockChunk(before.AsSpan(0, 56), 0x0000, isLast: false), "restore 1/2");
            Send(stream, PacketsGen2.BuildWriteFuncBlockChunk(before.AsSpan(56, 8), 0x0038, isLast: true), "restore 2/2");
            now = Read(stream);
        }
        bool restored = now is not null && now.AsSpan().SequenceEqual(before);
        Gen2KeyTriggerSync.Learn($"SETTINGS: restore {(restored ? "ok" : "NOT confirmed")}");
        return new(false, now is null ? null : Decode(now),
            restored ? "The keyboard didn't accept the change, so it was left as it was. Please send ultra-learn.log."
                     : "The keyboard didn't accept the change and couldn't be confirmed afterwards. Please check it on the official site and send ultra-learn.log.");
    }

    private static WriteResult Done(byte[] now, string how)
    {
        Gen2KeyTriggerSync.Learn($"SETTINGS: confirmed ({how} write)");
        return new(true, Decode(now), "Saved to the keyboard.");
    }

    private static void Send(HidStream stream, byte[] packet, string what)
    {
        var ack = stream.WritePacket(packet);
        bool acked = PacketsGen2.IsExtendedGatewayResponse(ack, PacketsGen2.SUB_WRITE_FUNC_BLOCK);
        Gen2KeyTriggerSync.Learn($"SETTINGS: {what} write ack={(acked ? "yes" : ack.Length == 0 ? "none" : ack.PacketToString())}");
        Thread.Sleep(acked ? 10 : 50);
    }
}
