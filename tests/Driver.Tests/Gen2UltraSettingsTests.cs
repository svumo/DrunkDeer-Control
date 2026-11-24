using Driver;
using Xunit;
using S = Driver.Gen2UltraSettings.Setting;

public class Gen2UltraSettingsTests
{
    // Tester's baseline FuncBlock bytes 0x00..0x07 (rest zero for the test):
    // 0x04 = 0x01 (8000 Hz, no tick rate), 0x06 = 0x01, 0x07 = 0x04.
    private static byte[] Baseline()
    {
        var b = new byte[Gen2UltraSettings.BlockLength];
        b[0x04] = 0x01; b[0x06] = 0x01; b[0x07] = 0x04;
        return b;
    }

    [Fact]
    public void Decodes_tester_baseline()
        => Assert.Equal(new Gen2UltraSettings.Values(true, false, false, false), Gen2UltraSettings.Decode(Baseline()));

    [Theory]
    [InlineData(S.LockAltTab, 0x06, 0x03)]      // logged: Alt+Tab on = 0x00 -> 0x02 (bit1)
    [InlineData(S.LockAltF4, 0x06, 0x05)]       // logged: Alt+F4 on = 0x00 -> 0x04 (bit2)
    [InlineData(S.AdaptiveStable, 0x07, 0x05)]  // logged: 0x04 -> 0x05
    public void Patch_sets_one_bit_only(S s, int at, int expected)
    {
        var before = Baseline();
        var after = Gen2UltraSettings.Patch(before, s, true);
        Assert.Equal((byte)expected, after[at]);
        for (int i = 0; i < before.Length; i++) if (i != at) Assert.Equal(before[i], after[i]);
    }

    [Fact]
    public void Patch_off_keeps_anti_shake_bits()
    {
        var b = Baseline(); b[0x07] = 0x45; // anti-shake high (0x40) + adaptive on
        Assert.Equal(0x44, Gen2UltraSettings.Patch(b, S.AdaptiveStable, false)[0x07]);
    }

    // ultra-learn-5 (2026-11-24): every value the tester stepped through.
    [Theory]
    [InlineData(Gen2UltraSettings.Field.TickRate, 1, 0x04, 0x11)]   // 30 tick
    [InlineData(Gen2UltraSettings.Field.TickRate, 2, 0x04, 0x21)]   // 60 tick
    [InlineData(Gen2UltraSettings.Field.Polling, 2, 0x04, 0x02)]    // 4000 Hz
    [InlineData(Gen2UltraSettings.Field.Polling, 3, 0x04, 0x03)]    // 2000 Hz
    [InlineData(Gen2UltraSettings.Field.Polling, 4, 0x04, 0x04)]    // 1000 Hz
    [InlineData(Gen2UltraSettings.Field.AntiShake, 1, 0x07, 0x24)]  // medium
    [InlineData(Gen2UltraSettings.Field.AntiShake, 2, 0x07, 0x44)]  // high
    public void Field_patch_matches_official_site(Gen2UltraSettings.Field f, int raw, int at, int expected)
    {
        var after = Gen2UltraSettings.Patch(Baseline(), f, raw);
        Assert.Equal((byte)expected, after[at]);
        Assert.Equal(raw, Gen2UltraSettings.Get(Gen2UltraSettings.Decode(after), f));
    }

    [Fact]
    public void Fields_share_byte_without_clobbering()
    {
        var b = Gen2UltraSettings.Patch(Baseline(), Gen2UltraSettings.Field.TickRate, 2);
        b = Gen2UltraSettings.Patch(b, Gen2UltraSettings.Field.Polling, 4);
        Assert.Equal(0x24, b[0x04]);
        b = Gen2UltraSettings.Patch(b, Gen2UltraSettings.Field.AntiShake, 2);
        b = Gen2UltraSettings.Patch(b, S.AdaptiveStable, true);
        Assert.Equal(0x45, b[0x07]);
    }

    [Fact]
    public void Unknown_value_is_refused()
        => Assert.Throws<System.ArgumentOutOfRangeException>(() => Gen2UltraSettings.Patch(Baseline(), Gen2UltraSettings.Field.TickRate, 3));

    [Fact]
    public void Narrow_write_packet_targets_0x04_with_commit()
    {
        var p = PacketsGen2.BuildWriteFuncBlockChunk(Baseline().AsSpan(4, 4), 0x0004, isLast: true);
        Assert.Equal(new byte[] { 0x55, PacketsGen2.SUB_WRITE_FUNC_BLOCK }, p[..2]);
        Assert.Equal(new byte[] { 4, 0x04, 0x00, 1, 0x01, 0x00, 0x01, 0x04 }, p[4..12]);
    }
}
