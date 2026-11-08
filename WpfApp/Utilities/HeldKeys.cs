using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace WpfApp.Utilities;

// Waits for the physical keyboard to be idle before a profile push.
//
// The A75 Pro firmware can latch a key "stuck" (held down, or dead until a
// replug) when its keymap / Last Win pair table is rewritten while it is
// processing key input. Pushing only when no key is held removes one way
// into that state. Mouse buttons are ignored, so clicking Sync never blocks.
internal static class HeldKeys
{
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private static bool IsMouseButton(int vk) => vk is 0x01 or 0x02 or 0x04 or 0x05 or 0x06;

    public static HashSet<int> Snapshot()
    {
        var held = new HashSet<int>();
        for (int vk = 0x08; vk <= 0xFE; vk++)
        {
            if (IsMouseButton(vk)) continue;
            if ((GetAsyncKeyState(vk) & 0x8000) != 0) held.Add(vk);
        }
        return held;
    }

    // Waits until no key is held, for at most maxWaitMs. Short on purpose:
    // a normal Alt+Tab or keystroke releases well inside a second, while
    // Windows can report a key as held indefinitely (seen 2026-11-08: F1 /
    // 0x70 phantom "down" for minutes), which must not stall every sync.
    // Returns (ms waited, keys waited on).
    public static async Task<(int waitedMs, string keys)> WaitForReleaseAsync(
        int maxWaitMs = 1000, CancellationToken ct = default)
    {
        int waited = 0;
        var seen = new SortedSet<int>();
        while (waited < maxWaitMs && !ct.IsCancellationRequested)
        {
            var held = Snapshot();
            if (held.Count == 0) break;
            seen.UnionWith(held);
            await Task.Delay(50, ct).ConfigureAwait(false);
            waited += 50;
        }
        return (waited, string.Join(" ", seen.Select(v => $"0x{v:X2}")));
    }
}
