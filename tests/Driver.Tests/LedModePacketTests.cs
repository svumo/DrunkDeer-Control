using Driver;
using Xunit;

namespace DrunkDeer.Tests;

// Golden bytes for the preset-lighting packet (0xAE/0x01). The default
// ColourIndex (0) must keep the packet identical to what 2.5.1 sent.
public class LedModePacketTests
{
    private static byte[] Build(byte mode, byte brightness = 5, byte speed = 3, byte colour = 0) =>
        Packets.BuildLedModePacket(new LightingProfile
        {
            Mode = mode,
            Brightness = brightness,
            Speed = speed,
            ColourIndex = colour,
        });

    [Fact]
    public void Header_and_layout_match_the_2_5_1_packet()
    {
        var p = Build(LightingProfile.ModeBreath);
        Assert.Equal(Packets.PACKET_SIZE, p.Length);
        Assert.Equal(new byte[] { 0xAE, 0x01, 0x00, 0x00, LightingProfile.ModeBreath, 3, 5, 0x00 }, p[..8]);
        Assert.All(p[8..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void Colour_index_lands_at_byte_7_for_colour_capable_effects()
    {
        var entry = RgbEffectCatalog.All.First(e => e.SupportsColor && e.Code != LightingProfile.ModeOff);
        Assert.Equal(4, Build(entry.Code, colour: 4)[7]);
    }

    [Fact]
    public void Colour_index_is_clamped_to_palette()
    {
        var entry = RgbEffectCatalog.All.First(e => e.SupportsColor && e.Code != LightingProfile.ModeOff);
        Assert.Equal(LightingProfile.MaxColourIndex, Build(entry.Code, colour: 200)[7]);
    }

    [Fact]
    public void Colour_index_is_zeroed_for_effects_without_colour()
    {
        Assert.Equal(0, Build(LightingProfile.ModeOff, colour: 3)[7]);
        Assert.Equal(0, Build(LightingProfile.ModeCustom, colour: 3)[7]);
    }

    [Fact]
    public void Brightness_and_speed_stay_clamped_to_9()
    {
        var p = Build(LightingProfile.ModeBreath, brightness: 50, speed: 50);
        Assert.Equal(9, p[5]);
        Assert.Equal(9, p[6]);
    }

    [Fact]
    public void Unknown_mode_falls_back_to_off()
    {
        Assert.Equal(LightingProfile.ModeOff, Build(99)[4]);
    }
}
