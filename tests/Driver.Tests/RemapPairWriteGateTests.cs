using Driver;
using Xunit;

namespace DrunkDeer.Tests;

// #6: on boards where remap / LW / RDT pair writes are untested (A75 Ultra,
// A75 Master), the profile push must leave the key map and pair table alone
// unless the user confirmed it for that keyboard.
public class RemapPairWriteGateTests
{
    private const int A75Pro = 750, A75Ultra = 756, A75Master = 757;

    private static ProfileItem ProfileWithRemapAndLwPairs()
    {
        var hid = new byte[126];
        hid[10] = 0x04; // one custom remap
        return new ProfileItem
        {
            Name = "test",
            Profile = new Profile { Settings = new ProfileSettings { LastWinEnabled = true, RapidTriggerEnabled = true } },
            RemapProfile = new RemapProfile
            {
                PerSlotHidUsage = hid,
                LwPairs = [[30, 32]], // arbitrary two slots
            },
        };
    }

    [Theory]
    [InlineData(A75Ultra)]
    [InlineData(A75Master)]
    public void Untested_models_start_with_remap_and_pair_writes_off(int typeCode)
    {
        var caps = FirmwareCapabilities.Resolve(typeCode, 0x0055);
        Assert.False(caps.AllowsRemapAndPairWrites);
        Assert.True(caps.RemapAndPairWritesNeedConfirmation);
    }

    [Fact]
    public void A75Pro_keeps_remap_and_pair_writes()
    {
        var caps = FirmwareCapabilities.Resolve(A75Pro, 0x0017);
        Assert.True(caps.AllowsRemapAndPairWrites);
        Assert.False(caps.RemapAndPairWritesNeedConfirmation);
    }

    [Fact]
    public void Gated_bundle_has_no_remap_pairs_or_pair_clears_but_keeps_actuation()
    {
        var caps = FirmwareCapabilities.Resolve(A75Ultra, 0x0055);
        var b = ProfileWithRemapAndLwPairs().BuildFullProfilePackets(KeyboardLayout.A75ProFlat, caps);

        Assert.Empty(b.Remap);
        Assert.Null(b.RtpRemapReflush);
        Assert.Null(b.LwPairs);
        Assert.Null(b.RdtPairs);
        Assert.Null(b.ClearRtp);
        Assert.Null(b.ClearRtpAll);
        Assert.Null(b.PrePassClearRtpUpper);
        Assert.False(b.HasAnyPairs);
        Assert.NotEmpty(b.AckedBatch); // AP / DS / US + Common Switch still go out
    }

    [Fact]
    public void Ungated_bundle_still_carries_remap_and_pairs()
    {
        var caps = FirmwareCapabilities.Resolve(A75Pro, 0x0017);
        var b = ProfileWithRemapAndLwPairs().BuildFullProfilePackets(KeyboardLayout.A75ProFlat, caps);

        Assert.NotEmpty(b.Remap);
        Assert.True(b.HasAnyPairs);
        Assert.NotNull(b.ClearRtp);
    }

    [Fact]
    public void Acknowledged_gate_restores_the_full_bundle()
    {
        var gated = FirmwareCapabilities.Resolve(A75Ultra, 0x0055);
        var acked = gated with { AllowsRemapAndPairWrites = true };
        var full = FirmwareCapabilities.Resolve(A75Pro, 0x0017) with { Precision = gated.Precision };

        var a = ProfileWithRemapAndLwPairs().BuildFullProfilePackets(KeyboardLayout.A75ProFlat, acked);
        var f = ProfileWithRemapAndLwPairs().BuildFullProfilePackets(KeyboardLayout.A75ProFlat, full);
        Assert.Equal(f.Remap.Length, a.Remap.Length);
        Assert.Equal(f.HasAnyPairs, a.HasAnyPairs);
    }
}
