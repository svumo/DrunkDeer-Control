using Driver;
using HidSharp;

namespace WpfApp.Profile;

// Shared plumbing for the OEM A75 Ultra key-trigger sync (gen-2, VID 0x19F5).
// Used by KeyboardPerformanceView (manual Sync + Load from keyboard) and by
// ProfileManager (automatic sync for profiles the user has synced once).
// All keyboard I/O runs on a worker thread: the WebHID channel marshals to the
// UI thread internally, so doing it on the UI thread deadlocks.
public static class UltraSync
{
    public static IReadOnlyList<LayoutKey> FlatFor(KeyboardWithSpecs kb)
        => KeyboardLayout.VisualFlatFor(KeyboardLayoutResolver.Resolve(kb)) ?? KeyboardLayout.A75ProFlat;

    public static List<Gen2KeyTriggerSync.Desired> DesiredFor(ProfileItem item, IReadOnlyList<LayoutKey> flat)
    {
        var keys = item.Profile.Keys_Array;
        var hidMap = KeyboardLayout.BuildDefaultHidUsageMap(flat);
        var desired = new List<Gen2KeyTriggerSync.Desired>();
        for (int ki = 0; ki < hidMap.Length && ki < keys.Length; ki++)
        {
            if (hidMap[ki] == 0 || keys[ki] is not { } k) continue;
            // Rapid Trigger is on for a key when it has press/release distances,
            // the same rule the editor's "in Rapid Trigger" counter uses.
            bool rtOn = k.Downstroke > 0 || k.Upstroke > 0;
            desired.Add(new(ki, hidMap[ki], Cv(k.Action_Point),
                rtOn ? Cv(k.Downstroke) : null, rtOn ? Cv(k.Upstroke) : null, rtOn,
                Dz(k.TopDeadZone), Dz(k.BottomDeadZone)));
        }
        return desired;
    }

    private static int? Cv(decimal mm) => mm > 0 ? (int)Math.Round(mm * 100m) : null;
    private static int? Dz(decimal? mm) => mm is decimal v && v >= 0 ? (int)Math.Round(v * 100m) : null;

    public static Task<T> OnKeyboard<T>(KeyboardWithSpecs kb, Func<HidStream, T> work, T busy) => Task.Run(() =>
    {
        using var gate = DeviceWriteGate.TryAcquire(kb.Keyboard.DevicePath, TimeSpan.FromSeconds(10));
        if (gate is null) return busy;
        using var stream = kb.Keyboard.Open();
        return work(stream);
    });

    // The keyboard's default key map never changes, so read it once per device.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, KeyMatrixSlotMap> _slotMaps = new();

    public static (KeyMatrixSlotMap? map, byte[]? region) Read(HidStream s, byte profileIndex)
    {
        var path = s.Device.DevicePath ?? "";
        if (!_slotMaps.TryGetValue(path, out var map))
        {
            map = Gen2KeyTriggerSync.ReadSlotMap(s);
            if (map is not null) _slotMaps[path] = map;
        }
        var region = map is null ? null : Gen2KeyTriggerSync.ReadRegion(s, profileIndex);
        return (map, region);
    }

    // Silent plan + apply for automatic sync. No dialogs.
    public static Task<Gen2KeyTriggerSync.ApplyResult> PushSilentAsync(KeyboardWithSpecs kb, ProfileItem item)
    {
        var desired = DesiredFor(item, FlatFor(kb));
        byte profileIndex = kb.Specs.ActiveProfileIndex;
        return OnKeyboard(kb, s =>
        {
            var (map, region) = Read(s, profileIndex);
            if (map is null || region is null)
                return new Gen2KeyTriggerSync.ApplyResult(false, 0, 0, "Couldn't read the keyboard, so nothing was synced.");
            var plan = Gen2KeyTriggerSync.Plan(region, map.TryGetSlotForHid, desired);
            DebugLogger.Log($"UltraSync auto: profile '{item.Name}' — {plan.Count} key(s) differ");
            return Gen2KeyTriggerSync.Apply(s, profileIndex, plan);
        }, new Gen2KeyTriggerSync.ApplyResult(false, 0, 0, "The keyboard is busy. Nothing was written."));
    }
}
