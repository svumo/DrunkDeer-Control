using Driver;
using Xunit;

namespace DrunkDeer.Tests;

// The Verified table is loaded from embedded JSON (Driver/capabilities).
// These pin the values the pre-JSON hard-coded table had, so a bad or
// missing record shows up here rather than as a silent Beta downgrade.
public class CapabilityResolutionTests
{
    private const int A75Pro = 750;

    [Fact]
    public void Embedded_records_load()
    {
        var rows = CapabilityRecordLoader.Load();
        Assert.Contains(rows, r => r.TypeCode == A75Pro && r.FirmwareVersion == 0x0017);
        Assert.Contains(rows, r => r.TypeCode == A75Pro && r.FirmwareVersion == 0x0009);
    }

    [Theory]
    [InlineData((ushort)0x0017)]
    [InlineData((ushort)0x0009)]
    public void A75Pro_verified_firmware_matches_the_old_table(ushort fw)
    {
        var caps = FirmwareCapabilities.Resolve(A75Pro, fw);
        Assert.Equal(SupportTier.Verified, caps.Tier);
        Assert.True(caps.SupportsRgb);
        Assert.True(caps.SupportsCustomRgb);
        Assert.Equal(
            new[] { LightingProfile.ModeOff, LightingProfile.ModeAlwaysOn, LightingProfile.ModeBreath },
            caps.VerifiedRgbModes.OrderBy(b => b).ToArray());
    }

    [Fact]
    public void Verified_but_outdated_firmware_keeps_verified_tier_and_gets_upgrade_prompt()
    {
        var caps = FirmwareCapabilities.Resolve(A75Pro, 0x0009);
        Assert.True(caps.IsTooOld);
        Assert.Equal((ushort)0x0017, caps.RecommendedFloor);
        Assert.Equal(WirePrecision.Legacy, caps.Precision);
    }

    // #9: an A75 (TypeCode 75) on firmware 0x21, shown as "0.033", is below
    // the 0x27 floor. The early too-old return used to hand it OldHighPrec
    // (mm × 100) although its firmware wants Legacy (mm × 10).
    [Theory]
    [InlineData(75, (ushort)0x0021, WirePrecision.Legacy)]
    [InlineData(75, (ushort)0x0024, WirePrecision.OldHighPrec)]
    [InlineData(751, (ushort)0x0018, WirePrecision.Legacy)]
    [InlineData(750, (ushort)0x0010, WirePrecision.Legacy)]
    public void Too_old_firmware_still_gets_its_resolved_precision(int typeCode, ushort fw, WirePrecision expected)
    {
        var caps = FirmwareCapabilities.Resolve(typeCode, fw);
        Assert.True(caps.IsTooOld);
        Assert.Equal(expected, caps.Precision);
    }

    [Fact]
    public void Unlisted_A75Pro_firmware_is_not_verified()
    {
        var caps = FirmwareCapabilities.Resolve(A75Pro, 0x0030);
        Assert.NotEqual(SupportTier.Verified, caps.Tier);
    }

    [Fact]
    public void Unknown_type_code_is_not_verified_and_has_no_rgb()
    {
        var caps = FirmwareCapabilities.Resolve(null, 0x0017);
        Assert.NotEqual(SupportTier.Verified, caps.Tier);
        Assert.False(caps.SupportsRgb);
    }
}
