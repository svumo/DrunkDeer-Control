using Driver;
using Xunit;

namespace DrunkDeer.Tests;

// Read-modify-write rules for the OEM A75 Ultra key-trigger sync.
public class Gen2KeyTriggerSyncTests
{
    // Tester's real records (2026-11-23): A at 0.30 mm with RT 0.10/0.10.
    private static readonly byte[] TesterA = [0xa0, 0x01, 0x1d, 0x00, 0x09, 0x12, 0x09, 0x1c];
    private static readonly byte[] UltraStock = [0xa0, 0x01, 0x95, 0x00, 0x31, 0x00, 0x31, 0x1e];

    [Fact]
    public void Decodes_tester_record()
    {
        var v = Gen2KeyTriggerSync.Decode(TesterA);
        Assert.Equal(30, v.Actuation);
        Assert.Equal(10, v.RtPress);
        Assert.Equal(10, v.RtRelease);
    }

    [Fact]
    public void Patch_actuation_only_matches_the_verified_beta4_write()
        => Assert.Equal(new byte[] { 0xa0, 0x01, 0x63, 0x00, 0x09, 0x12, 0x09, 0x1c },
                        Gen2KeyTriggerSync.Patch(TesterA, 100, null, null));

    [Fact]
    public void Patch_rt_keeps_deadzone_bits()
    {
        var o = Gen2KeyTriggerSync.Patch(TesterA, null, 50, 300); // 300 -> stored 299 = 0x12B
        Assert.Equal(new byte[] { 0xa0, 0x01, 0x1d, 0x00, 0x31, 0x12, 0x2b, 0x1d }, o);
        var v = Gen2KeyTriggerSync.Decode(o);
        Assert.Equal((30, 50, 300), (v.Actuation, v.RtPress, v.RtRelease));
    }

    [Fact]
    public void Patch_clamps_to_safe_range()
    {
        var v = Gen2KeyTriggerSync.Decode(Gen2KeyTriggerSync.Patch(TesterA, 900, 0, 999));
        Assert.Equal(Gen2KeyTriggerSync.ApMax, v.Actuation);
        Assert.Equal(Gen2KeyTriggerSync.RtMin, v.RtPress);
        Assert.Equal(Gen2KeyTriggerSync.RtMax, v.RtRelease);
    }

    // From the tester's beta.6 learning dump: Del after "RT off, 0.77 mm" in the
    // official driver, and an untouched RT-on key.
    private static readonly byte[] DelRtOff = [0xa0, 0x00, 0x4c, 0x00, 0x31, 0x00, 0x31, 0x1e];
    private static readonly byte[] JRtOn = [0xa0, 0x01, 0x95, 0x00, 0x31, 0x00, 0x31, 0x1e];

    [Fact]
    public void Decodes_rt_mode_and_dead_zones()
    {
        Assert.False(Gen2KeyTriggerSync.Decode(DelRtOff).RtOn);
        Assert.True(Gen2KeyTriggerSync.Decode(JRtOn).RtOn);
        var a = Gen2KeyTriggerSync.Decode(TesterA); // official driver showed 0.09 / 0.14
        Assert.Equal((9, 14), (a.TopDeadZone, a.BottomDeadZone));
    }

    [Fact]
    public void Patch_rt_off_reproduces_official_driver_byte()
        => Assert.Equal(DelRtOff, Gen2KeyTriggerSync.Patch(JRtOn, 77, null, null, rtOn: false));

    [Fact]
    public void Patch_rt_leaves_unknown_modes_alone()
    {
        byte[] mode2 = [0xa0, 0x02, 0x95, 0x00, 0x31, 0x00, 0x31, 0x1e];
        Assert.Equal(mode2, Gen2KeyTriggerSync.Patch(mode2, null, null, null, rtOn: true));
    }

    [Fact]
    public void Patch_dead_zones_keep_ninth_bits()
    {
        byte[] rec = [0xa0, 0x01, 0x95, 0x00, 0xff, 0x01, 0xff, 0x01]; // 9th bits set
        var o = Gen2KeyTriggerSync.Patch(rec, null, null, null, topDeadZone: 9, bottomDeadZone: 200);
        Assert.Equal(0x13, o[5]);                                   // (9 << 1) | 1
        Assert.Equal((byte)((Gen2KeyTriggerSync.DeadZoneMax << 1) | 1), o[7]); // clamped to 127
        Assert.Equal(512, Gen2KeyTriggerSync.Decode(o).RtPress);    // LE9 untouched
    }

    private static byte[] Region(params (int slot, byte[] rec)[] recs)
    {
        var r = new byte[PacketsGen2.KEY_TRIGGER_REGION_SIZE];
        foreach (var (slot, rec) in recs) Array.Copy(rec, 0, r, slot * 8, 8);
        return r;
    }

    private static int SlotFor(byte hid) => hid switch { 0x04 => 88, 0x16 => 89, 0x07 => 90, _ => -1 };

    [Fact]
    public void Plan_writes_only_keys_that_differ()
    {
        var region = Region((88, TesterA), (89, TesterA), (90, UltraStock));
        var plan = Gen2KeyTriggerSync.Plan(region, SlotFor,
        [
            new(64, 0x04, 30, 10, 10),     // A: unchanged
            new(65, 0x16, 100, null, null), // S: 0.30 -> 1.00
            new(66, 0x07, null, null, null),// D: nothing requested
            new(99, 0x2C, 50, null, null),  // no slot -> skipped
        ]);
        var c = Assert.Single(plan);
        Assert.Equal(89, c.Slot);
        Assert.Equal(65, c.KeyIndex);
        Assert.Equal(100, Gen2KeyTriggerSync.Decode(c.After).Actuation);
        Assert.Equal(TesterA, c.Before);
    }

    [Fact]
    public void Plan_skips_slots_without_a_valid_record()
    {
        var region = Region((88, new byte[8]));
        Assert.Empty(Gen2KeyTriggerSync.Plan(region, SlotFor, [new(64, 0x04, 100, null, null)]));
    }

    [Fact]
    public void Plan_never_writes_the_same_slot_twice()
    {
        var region = Region((88, TesterA));
        var plan = Gen2KeyTriggerSync.Plan(region, SlotFor,
            [new(64, 0x04, 100, null, null), new(70, 0x04, 200, null, null)]);
        Assert.Single(plan);
    }
}
