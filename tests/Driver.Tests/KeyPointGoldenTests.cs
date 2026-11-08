using Driver;
using Xunit;
using static Driver.Packets;

namespace DrunkDeer.Tests;

// Golden bytes for the per-key AP / DS / US packets in each wire dialect.
// OldHighPrec (mm x 100) is the hardware-verified A75 Pro 0x0017 path;
// Legacy (mm x 10) is what too-old A75 / A75 Pro firmware wants (#9).
public class KeyPointGoldenTests
{
    private static Profile ProfileWith(decimal ap, decimal ds, decimal us)
    {
        var keys = Enumerable.Range(0, 126)
            .Select(_ => new KeySetting { Action_Point = ap, Downstroke = ds, Upstroke = us })
            .ToArray();
        keys[0] = keys[0] with { Action_Point = 0.2m };
        keys[125] = keys[125] with { Action_Point = 2.0m };
        return new Profile { Keys_Array = keys };
    }

    [Fact]
    public void OldHighPrec_ap_is_mm_times_100_in_three_packets()
    {
        var batch = ProfileWith(1.5m, 0.3m, 0.25m).BuildKeyPointBatch(WirePrecision.OldHighPrec, KeyPointType.ActuationPoint);

        Assert.Equal(3, batch.Length);
        for (byte n = 0; n < 3; n++)
            Assert.Equal(new byte[] { 0xB6, 0x01, 0x00, n }, batch[n][..4]);

        Assert.Equal(20, batch[0][4]);    // slot 0   = 0.2 mm
        Assert.Equal(150, batch[0][5]);   // slot 1   = 1.5 mm
        Assert.Equal(150, batch[1][4]);   // slot 59
        Assert.Equal(200, batch[2][4 + 7]); // slot 125 = 2.0 mm (last of 8)
        Assert.All(batch[2][(4 + 8)..], b => Assert.Equal(0, b)); // nothing past slot 125
    }

    [Fact]
    public void Legacy_ap_is_mm_times_10_and_clamped_to_its_range()
    {
        var batch = ProfileWith(1.5m, 0.3m, 0.25m).BuildKeyPointBatch(WirePrecision.Legacy, KeyPointType.ActuationPoint);

        Assert.Equal(3, batch.Length);
        Assert.Equal(LEGACY_RAW_MIN, batch[0][4]); // 0.2 mm -> 2
        Assert.Equal(15, batch[0][5]);             // 1.5 mm -> 15
        Assert.Equal(20, batch[2][4 + 7]);         // 2.0 mm -> 20
    }

    [Fact]
    public void Legacy_and_OldHighPrec_differ_by_a_factor_of_ten()
    {
        var p = ProfileWith(1.2m, 0.5m, 0.5m);
        var legacy = p.BuildKeyPointBatch(WirePrecision.Legacy, KeyPointType.ActuationPoint);
        var high = p.BuildKeyPointBatch(WirePrecision.OldHighPrec, KeyPointType.ActuationPoint);
        Assert.Equal(12, legacy[0][10]);
        Assert.Equal(120, high[0][10]);
    }

    [Theory]
    [InlineData(KeyPointType.Downstroke, 0x04, 30)]
    [InlineData(KeyPointType.Upstroke, 0x05, 25)]
    public void OldHighPrec_ds_us_use_their_own_type_byte(KeyPointType type, byte typeByte, byte expected)
    {
        var batch = ProfileWith(1.5m, 0.3m, 0.25m).BuildKeyPointBatch(WirePrecision.OldHighPrec, type);
        Assert.Equal(typeByte, batch[0][1]);
        Assert.Equal(expected, batch[0][5]);
    }

    [Fact]
    public void Full_profile_push_uses_the_resolved_precision()
    {
        var item = new ProfileItem { Name = "t", Profile = ProfileWith(1.5m, 0.3m, 0.25m) };
        var tooOldA75 = FirmwareCapabilities.Resolve(75, 0x0021);        // Legacy (#9)
        var verifiedPro = FirmwareCapabilities.Resolve(750, 0x0017);     // OldHighPrec

        var legacy = item.BuildFullProfilePackets(KeyboardLayout.A75ProFlat, tooOldA75);
        var high = item.BuildFullProfilePackets(KeyboardLayout.A75ProFlat, verifiedPro);

        static byte FirstApByte(FullProfilePackets b) =>
            b.AckedBatch.First(p => p[0] == 0xB6 && p[1] == 0x01 && p[3] == 0)[5];
        Assert.Equal(15, FirstApByte(legacy));
        Assert.Equal(150, FirstApByte(high));
    }
}
