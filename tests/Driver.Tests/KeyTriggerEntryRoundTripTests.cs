using Driver;
using Xunit;

namespace DrunkDeer.Tests;

// Phase G6 (unit round-trip) of docs/diagnostic/gen2-decode-plan.md.
//
// Proves KeyTriggerEntry.Decode is the exact inverse of Encode over the
// firmware's accepted value range. This is the foundation the gen-2 capture
// decoder stands on: if Encode∘Decode isn't identity, every per-key value
// pulled out of a user's capture would be wrong.
public class KeyTriggerEntryRoundTripTests
{
    // Encode applies a −1 bias and 9-bit LE packing; Decode must undo both.
    // Range mirrors the sliders: actuation 10..330, rt 1..310 (we sweep a bit
    // wider to cover the full 1..512 storage range Encode clamps to).
    [Theory]
    [InlineData(60, 15, 50)]   // firmware defaults — the steady-state record
    [InlineData(138, 15, 50)]  // doc example: encodes to a0 00 89 00 0e 00 31 00
    [InlineData(10, 1, 1)]     // low end
    [InlineData(330, 310, 310)]// slider top
    [InlineData(512, 512, 512)]// storage ceiling (stored = 511 = 9 bits)
    public void Encode_then_Decode_is_identity(int actuation, int rtPress, int rtRelease)
    {
        var buf = new byte[KeyTriggerEntry.BYTE_SIZE];
        KeyTriggerEntry.Encode(buf, 0, actuation, rtPress, rtRelease);

        var d = KeyTriggerEntry.Decode(buf);

        Assert.Equal(actuation, d.Actuation);
        Assert.Equal(rtPress, d.RtPress);
        Assert.Equal(rtRelease, d.RtRelease);
        // Encode writes byte0 = 0xA0: switch_type (low nibble) = 0, the 0xA
        // constant lives in the high nibble. Decode masks the low nibble.
        Assert.Equal(0, d.SwitchType);
    }

    // Full sweep across the realistic actuation range with fixed RT values, to
    // catch any off-by-one in the 9th-bit handling at the 256 boundary.
    [Fact]
    public void Actuation_sweep_round_trips_across_the_256_boundary()
    {
        var buf = new byte[KeyTriggerEntry.BYTE_SIZE];
        for (int act = 1; act <= 512; act++)
        {
            KeyTriggerEntry.Encode(buf, 0, act, KeyTriggerEntry.DEFAULT_RT_PRESS, KeyTriggerEntry.DEFAULT_RT_RELEASE);
            var d = KeyTriggerEntry.Decode(buf);
            Assert.Equal(act, d.Actuation);
        }
    }

    // The documented capture bytes must decode to the documented values.
    // KeyTriggerEntry.cs header: actuation=60, rt_press=15, rt_release=50
    // produces `a0 00 3b 00 0e 00 31 00`.
    [Fact]
    public void Documented_capture_bytes_decode_to_documented_values()
    {
        byte[] wire = [0xa0, 0x00, 0x3b, 0x00, 0x0e, 0x00, 0x31, 0x00];
        var d = KeyTriggerEntry.Decode(wire);
        Assert.Equal(60, d.Actuation);
        Assert.Equal(15, d.RtPress);
        Assert.Equal(50, d.RtRelease);
        Assert.Equal(0, d.KeyMode); // beta.21..26 typo was key_mode=1; correct is 0
    }
}
