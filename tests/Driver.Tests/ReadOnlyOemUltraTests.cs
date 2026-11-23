using Driver;
using Xunit;

namespace DrunkDeer.Tests;

// The OEM A75 Ultra (VID 0x19F5 / PID 0xFB3E) is detection-only: recognised via
// the 0x55 gateway but never written to until its write format is verified.
public class ReadOnlyOemUltraTests
{
    private static KeyboardSpecs UltraSpecs() => new([])
    {
        KeyboardType = 756,
        FirmwareVersion = "OEM 1.21",
        FirmwareVersionNumeric = 0,
        IsOemGen2 = true,
        IsReadOnly = true,
    };

    [Fact]
    public void Specs_are_compatible_so_the_board_is_detected()
        => Assert.True(UltraSpecs().IsCompatible());

    [Fact]
    public void Capabilities_are_beta_with_no_rgb_no_remap_and_no_firmware_nag()
    {
        var caps = UltraSpecs().GetCapabilities();
        Assert.Equal(SupportTier.Beta, caps.Tier);
        Assert.False(caps.IsTooOld);
        Assert.False(caps.SupportsRgb);
        Assert.False(caps.SupportsCustomRgb);
        Assert.False(caps.AllowsRemapAndPairWrites);
        Assert.Contains("A75 Ultra", caps.Label);
    }

    [Fact]
    public void Ultra_pid_is_the_known_oem_ultra_pid()
        => Assert.Equal(0xFB3E, KeyboardManager.OemA75UltraGen2Pid);

    [Fact]
    public void Ultra_resolves_to_the_ultra_layout_model()
        => Assert.Equal("A75 Ultra", KeyboardModels.FindByTypeCode(756)?.DisplayName);
}
