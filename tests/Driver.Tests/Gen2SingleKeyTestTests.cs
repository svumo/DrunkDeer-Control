using Driver;
using Xunit;

namespace DrunkDeer.Tests;

// The one-key test must change ONLY the actuation field of the record it read.
public class Gen2SingleKeyTestTests
{
    // Real record from the OEM A75 Ultra tester log (2026-11-23): 1.50 mm stock.
    private static readonly byte[] UltraStock = [0xa0, 0x01, 0x95, 0x00, 0x31, 0x00, 0x31, 0x1e];

    [Fact]
    public void Reads_ultra_stock_actuation_as_150()
        => Assert.Equal(150, Gen2SingleKeyTest.ReadActuation(UltraStock));

    [Fact]
    public void WithActuation_changes_only_bytes_2_and_bit0_of_3()
    {
        var next = Gen2SingleKeyTest.WithActuation(UltraStock, 100);
        Assert.Equal(new byte[] { 0xa0, 0x01, 0x63, 0x00, 0x31, 0x00, 0x31, 0x1e }, next);
        Assert.Equal(100, Gen2SingleKeyTest.ReadActuation(next));
    }

    [Fact]
    public void WithActuation_preserves_upper_bits_of_byte_3_and_sets_ninth_bit()
    {
        byte[] rec = [0xa0, 0x00, 0x10, 0b0001_1110, 0, 0, 0, 0];
        var next = Gen2SingleKeyTest.WithActuation(rec, 300); // stored 299 = 0x12B
        Assert.Equal(0x2B, next[2]);
        Assert.Equal(0b0001_1111, next[3]);
        Assert.Equal(300, Gen2SingleKeyTest.ReadActuation(next));
    }

    [Theory]
    [InlineData(150, 100)]
    [InlineData(100, 150)]
    [InlineData(60, 100)]
    public void Toggles_between_test_and_restore(int current, int expected)
        => Assert.Equal(expected, Gen2SingleKeyTest.NextTarget(current));

    [Fact]
    public void Rejects_out_of_range_actuation()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Gen2SingleKeyTest.WithActuation(UltraStock, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Gen2SingleKeyTest.WithActuation(UltraStock, 513));
    }

    [Fact]
    public void Record_check_requires_0xA_marker()
    {
        Assert.True(Gen2SingleKeyTest.LooksLikeRecord(UltraStock));
        Assert.False(Gen2SingleKeyTest.LooksLikeRecord(new byte[8]));
    }
}
