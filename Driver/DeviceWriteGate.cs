using System.Collections.Concurrent;

namespace Driver;

// One write lock per HID device. Profile sync (ProfileManager) and RGB sync
// (LightingView) each open their own HidStream; without a shared lock their
// packets could interleave on the wire, e.g. a 0xAE lighting packet landing
// between remap packets. Every writer holds this for the whole
// open-write-close sequence.
//
// Lock order: ProfileManager's push lock (if any) is taken BEFORE this gate,
// and nothing takes the push lock while holding a gate, so they can't
// deadlock.
public static class DeviceWriteGate
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates =
        new(StringComparer.OrdinalIgnoreCase);

    private static SemaphoreSlim For(string? devicePath) =>
        Gates.GetOrAdd(devicePath ?? string.Empty, _ => new SemaphoreSlim(1, 1));

    /// <summary>
    /// Blocks until the device is free or <paramref name="timeout"/> passes.
    /// Returns null on timeout (the caller should report "keyboard busy").
    /// Dispose the result to release.
    /// </summary>
    public static IDisposable? TryAcquire(string? devicePath, TimeSpan timeout)
    {
        var gate = For(devicePath);
        return gate.Wait(timeout) ? new Releaser(gate) : null;
    }

    public static async Task<IDisposable?> TryAcquireAsync(string? devicePath, TimeSpan timeout)
    {
        var gate = For(devicePath);
        return await gate.WaitAsync(timeout).ConfigureAwait(false) ? new Releaser(gate) : null;
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private int released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0) gate.Release();
        }
    }
}
