using Driver;
using Xunit;

namespace DrunkDeer.Tests;

public class DeviceWriteGateTests
{
    private static string NewPath() => @"\\?\hid#test-" + Guid.NewGuid().ToString("N");

    [Fact]
    public void Second_writer_waits_until_the_first_releases()
    {
        var path = NewPath();
        using (var first = DeviceWriteGate.TryAcquire(path, TimeSpan.FromSeconds(1)))
        {
            Assert.NotNull(first);
            Assert.Null(DeviceWriteGate.TryAcquire(path, TimeSpan.FromMilliseconds(50)));
        }
        using var again = DeviceWriteGate.TryAcquire(path, TimeSpan.FromMilliseconds(50));
        Assert.NotNull(again);
    }

    [Fact]
    public void Path_comparison_ignores_case()
    {
        var path = NewPath();
        using var held = DeviceWriteGate.TryAcquire(path.ToUpperInvariant(), TimeSpan.FromSeconds(1));
        Assert.Null(DeviceWriteGate.TryAcquire(path.ToLowerInvariant(), TimeSpan.FromMilliseconds(50)));
    }

    [Fact]
    public async Task Different_devices_do_not_block_each_other()
    {
        using var a = DeviceWriteGate.TryAcquire(NewPath(), TimeSpan.FromSeconds(1));
        using var b = await DeviceWriteGate.TryAcquireAsync(NewPath(), TimeSpan.FromMilliseconds(50));
        Assert.NotNull(a);
        Assert.NotNull(b);
    }

    [Fact]
    public void Double_dispose_releases_once()
    {
        var path = NewPath();
        var g = DeviceWriteGate.TryAcquire(path, TimeSpan.FromSeconds(1))!;
        g.Dispose();
        g.Dispose();
        using var x = DeviceWriteGate.TryAcquire(path, TimeSpan.FromSeconds(1));
        Assert.NotNull(x);
        // If the second Dispose had released again, a third writer would get in.
        Assert.Null(DeviceWriteGate.TryAcquire(path, TimeSpan.FromMilliseconds(50)));
    }
}
