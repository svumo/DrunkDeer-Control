using Driver;
using Xunit;

namespace DrunkDeer.Tests;

// Pins the device filter. Widening it is a deliberate act: every new
// VID/PID here starts receiving identity probes, and (if it resolves to a
// known TypeCode) profile writes.
public class PidFilterTests
{
    private static readonly KeyboardFilter[] Filters = KeyboardManager.DrunkDeerKeyboards;

    [Fact]
    public void Mis_encoded_352D_entries_are_gone()
    {
        Assert.DoesNotContain(Filters, f => f.VendorId == 0x352d && f.ProductId is 0x1a81 or 0x2094);
    }

    [Fact]
    public void Only_FB5C_is_listed_for_gen2_vendor()
    {
        var gen2 = Filters.Where(f => f.VendorId == 0x19f5).Select(f => f.ProductId).ToArray();
        Assert.Equal(new[] { KeyboardManager.OemA75ProGen2Pid }, gen2.Select(p => (int)p).ToArray());
    }

    [Fact]
    public void No_generic_chip_vendor_ids_without_a_write_gate()
    {
        Assert.DoesNotContain(Filters, f => f.VendorId is 0x0c45 or 0x1a81);
    }

    [Fact]
    public void All_entries_use_the_vendor_defined_usage_page()
    {
        Assert.All(Filters, f => Assert.Equal(0xff00, (int)f.UsagePage));
    }

    [Fact]
    public void No_duplicate_entries()
    {
        var keys = Filters.Select(f => (f.VendorId, f.ProductId)).ToArray();
        Assert.Equal(keys.Length, keys.Distinct().Count());
    }

    [Fact]
    public void Users_A75Pro_pid_2383_is_still_detected()
    {
        Assert.Contains(Filters, f => f.VendorId == 0x352d && f.ProductId == 0x2383);
    }
}
