using HidSharp;

namespace Driver;

// OEM A75 Ultra (VID 0x19F5 / PID 0xFB3E) "Settings page" switches, which live
// in the FuncBlock area read from address 0 (sub 0x05). Mapped 2026-11-23 from
// the tester toggling each one on the official site and diffing reads:
//   0x06 bit0 Lock Win (by elimination), bit1 Lock Alt+Tab, bit2 Lock Alt+F4
//   0x07 bit0 Adaptive Stable
// Multi-value settings, mapped 2026-11-24 by stepping each dropdown:
//   0x04 bits0-3 polling: 1=8000 Hz, 2=4000, 3=2000, 4=1000 (keyboard reconnects)
//   0x04 bits4-5 tick rate: 0=none, 1=30, 2=60
//   0x07 bits5-6 anti-shake: 0=low, 1=medium, 2=high
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

    public sealed record Values(bool LockWin, bool LockAltTab, bool LockAltF4, bool AdaptiveStable,
        int TickRate = 0, int AntiShake = 0, int Polling = 1);

    public enum Field { TickRate, AntiShake, Polling }

    public const int SettingsByte = 0x04;

    private static (int Byte, byte Mask, int Shift) Where(Field f) => f switch
    {
        Field.TickRate => (SettingsByte, 0x30, 4),
        Field.AntiShake => (AdaptiveByte, 0x60, 5),
        Field.Polling => (SettingsByte, 0x0F, 0),
        _ => throw new ArgumentOutOfRangeException(nameof(f)),
    };

    // Known values (raw -> label). Anything else on the keyboard is shown as
    // unknown and left alone.
    public static readonly IReadOnlyDictionary<Field, (int Raw, string Label)[]> Options = new Dictionary<Field, (int, string)[]>
    {
        [Field.TickRate] = [(0, "No tick rate"), (1, "30 tick rate"), (2, "60 tick rate")],
        [Field.AntiShake] = [(0, "Low"), (1, "Medium"), (2, "High")],
        [Field.Polling] = [(1, "8000 Hz"), (2, "4000 Hz"), (3, "2000 Hz"), (4, "1000 Hz")],
    };

    public static int Get(Values v, Field f) => f switch
    {
        Field.TickRate => v.TickRate,
        Field.AntiShake => v.AntiShake,
        _ => v.Polling,
    };

    public static byte[] Patch(ReadOnlySpan<byte> block, Field f, int raw)
    {
        var (b, mask, shift) = Where(f);
        if (!Options[f].Any(o => o.Raw == raw)) throw new ArgumentOutOfRangeException(nameof(raw), $"{f} has no known value {raw}");
        var o = block.ToArray();
        o[b] = (byte)((o[b] & ~mask) | ((raw << shift) & mask));
        return o;
    }

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
        (block[AdaptiveByte] & 0x01) != 0,
        (block[SettingsByte] & 0x30) >> 4,
        (block[AdaptiveByte] & 0x60) >> 5,
        block[SettingsByte] & 0x0F);

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
        var (b, _) = Where(s);
        return Write(stream, before, Patch(before, s, on), b, $"{s} -> {(on ? "on" : "off")}", reconnects: false);
    }

    public static WriteResult Set(HidStream stream, Field f, int raw)
    {
        var before = Read(stream);
        if (before is null) return new(false, null, "Couldn't read the keyboard's settings. Nothing was changed.");
        var (b, _, _) = Where(f);
        return Write(stream, before, Patch(before, f, raw), b, $"{f} -> {raw}", reconnects: f == Field.Polling);
    }

    private static WriteResult Write(HidStream stream, byte[] before, byte[] after, int b, string what, bool reconnects)
    {
        Gen2KeyTriggerSync.Learn($"SETTINGS: {what}: byte 0x{b:x2} 0x{before[b]:x2} -> 0x{after[b]:x2}");
        if (after[b] == before[b]) return new(true, Decode(before), "Already set.");

        if (reconnects)
        {
            // Polling rate: the keyboard drops off USB and comes back as soon as
            // it applies this, so there's nothing to read back on this
            // connection. One narrow write only (no fallbacks, no restore); the
            // panel re-reads the keyboard when it reconnects.
            var ack = stream.WritePacket(PacketsGen2.BuildWriteFuncBlockChunk(after.AsSpan(4, 4), 0x0004, isLast: true));
            bool acked = PacketsGen2.IsExtendedGatewayResponse(ack, PacketsGen2.SUB_WRITE_FUNC_BLOCK);
            Gen2KeyTriggerSync.Learn($"SETTINGS: reconnecting write ack={(acked ? "yes" : ack.Length == 0 ? "none" : ack.PacketToString())}");
            if (acked) return new(true, Decode(after), "Saved. The keyboard reconnects to apply it; this takes a few seconds.");
            Thread.Sleep(300);
            var check = Read(stream);
            if (check is not null && check[b] == after[b]) return new(true, Decode(check), "Saved. The keyboard may reconnect to apply it.");
            return new(false, check is null ? null : Decode(check), "The keyboard didn't confirm the change. If it reconnects, check the setting again; otherwise please send ultra-learn.log.");
        }

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
