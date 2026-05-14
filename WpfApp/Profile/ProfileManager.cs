using Driver;
using HidSharp;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using WpfApp.Extensions;
using MessageBox = System.Windows.Forms.MessageBox;

namespace WpfApp.Profile;

public sealed class ProfileManager(KeyboardManager keyboardManager, Settings settings)
{
    public ObservableCollection<ProfileItem> Profiles { get; private set; } = [];
    public List<Tuple<ProfileItem, string>> ProfileFileNames { get; private set; } = [];
    private readonly JsonSerializerOptions options = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private readonly string profileDir = Path.Combine(Program.APP_DIR, "profiles");
    private readonly KeyboardManager keyboardManager = keyboardManager;
    private readonly Settings settings = settings;
    private int currentIndex = -1;
    public int CurrentIndex
    {
        get { return currentIndex; }
        set
        {
            if (!EqualityComparer<int>.Default.Equals(currentIndex, value))
            {
                currentIndex = value;
                CurrentProfileChanged?.Invoke(currentIndex, Profiles[currentIndex]);
            }
        }
    }

    public event Action<int, ProfileItem>? CurrentProfileChanged;
    public event Action<ProfileItem[]>? ProfileCollectionChanged;

    public void DiscoverProfiles()
    {
        var info = Directory.CreateDirectory(profileDir);
        var discoveredProfiles = info.EnumerateFiles().Where(f => Path.GetExtension(f.Name) == ".json").Select(f => FromJsonFile(f.FullName)).Where(p => p is not null).Select(p => p!).ToArray();
        foreach (var profile in discoveredProfiles)
        {
            profile.PropertyChanged += ProfileItemChanged;
            Profiles.Add(profile);
        }
        ProfileCollectionChanged?.Invoke(discoveredProfiles);
        ProfileFileNames = Profiles.Select(p => Tuple.Create(p, p.Name)).ToList();
        if (settings.LastProfileUsedName is { } s && !s.Equals(string.Empty))
        {
            var current = Profiles.FindIndex(p => p.Name.Equals(s));
            if (current >= 0 && current != CurrentIndex && current < Profiles.Count)
            {
                CurrentIndex = current;
            }
            else
            {
                current = Math.Max(Profiles.FindIndex(p => p.IsDefault), 0);
                if (current >= 0 && current != CurrentIndex && current < Profiles.Count)
                {
                    CurrentIndex = current;
                }
            }
        }
    }

    private ProfileItem? FromJsonFile(string path)
    {
        var text = File.ReadAllText(path);
        try
        {
            var profile = JsonSerializer.Deserialize<ProfileItem>(text, options);
            if (profile != null)
            {
                profile.Name = Path.GetFileNameWithoutExtension(path);
                profile.IsDirty = false;
            }
            return profile;
        }
        catch (JsonException) { Console.WriteLine("Failed to deserialize profile file at {0}", path); }
        return null;
    }

    public void ImportAndLinkRemaps(ProfileItem item, string path)
    {
        DebugLogger.Log($"ImportAndLinkRemaps: '{path}' linking to profile '{item.Name}'");
        try
        {
            var text = File.ReadAllText(path);
            var remaps = JsonSerializer.Deserialize<Driver.RemapProfile>(text, options);
            if (remaps is null) { DebugLogger.Log($"  deserialize returned null"); return; }
            item.RemapProfile = remaps;
            Save(item);
            DebugLogger.Log($"  ok");
        }
        catch (Exception e)
        {
            DebugLogger.Log($"  EXCEPTION {e}");
            MessageBox.Show(e.Message);
        }
    }

    public ProfileItem? ImportProfile(string path)
    {
        DebugLogger.Log($"ImportProfile: '{path}'");
        try
        {
            var text = File.ReadAllText(path);
            return ImportProfileFromJson(text, Path.GetFileNameWithoutExtension(path));
        }
        catch (Exception e)
        {
            DebugLogger.Log($"  EXCEPTION {e}");
            MessageBox.Show(e.Message);
            return null;
        }
    }

    // Shared path used by both file import and bundled-preset import. Auto-
    // renames on collision (`FPS` → `FPS (2)`) so users never get a silent
    // overwrite — they can rename to whatever they want afterwards.
    public ProfileItem? ImportProfileFromJson(string json, string baseName)
    {
        var profile = JsonSerializer.Deserialize<Driver.Profile>(json, options);
        if (profile is null) { DebugLogger.Log($"  deserialize returned null"); return null; }
        var profileItem = new ProfileItem
        {
            Name = GenerateUniqueName(baseName),
            Profile = profile,
            IsDirty = false
        };
        Save(profileItem);
        profileItem.PropertyChanged += ProfileItemChanged;
        Profiles.Add(profileItem);
        ProfileCollectionChanged?.Invoke([profileItem]);
        DebugLogger.Log($"  ok (Name='{profileItem.Name}', HasRTP={profile.RTP is not null}, KeyCount={profile.Keys_Array?.Length ?? 0})");
        return profileItem;
    }

    private string GenerateUniqueName(string baseName)
    {
        if (string.IsNullOrWhiteSpace(baseName)) baseName = "Profile";
        if (!Profiles.Any(p => p.Name.Equals(baseName, StringComparison.OrdinalIgnoreCase)))
            return baseName;
        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{baseName} ({i})";
            if (!Profiles.Any(p => p.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
                return candidate;
        }
        return baseName + " (" + Guid.NewGuid().ToString("N")[..6] + ")";
    }

    // Exports the profile in the same shape the DrunkDeer web driver writes.
    // We pass the Profile through directly (JsonExtensionData on Profile
    // re-emits any lighting/RGB block we captured on import), so a round-trip
    // through this app preserves anything we don't model.
    public void ExportProfile(ProfileItem item, string path)
    {
        DebugLogger.Log($"ExportProfile: '{item.Name}' -> '{path}'");
        try
        {
            var json = JsonSerializer.Serialize(item.Profile, options);
            File.WriteAllText(path, json);
        }
        catch (Exception e)
        {
            DebugLogger.Log($"  EXCEPTION {e}");
            MessageBox.Show(e.Message);
        }
    }

    private void Save(ProfileItem item)
    {
        var json = JsonSerializer.Serialize(item, options);
        var indexOld = ProfileFileNames.FindIndex(t => t.Item1 == item);
        if (indexOld >= 0)
        {
            if (!ProfileFileNames[indexOld].Item2.Equals(item.Name))
            {
                // Name changed — delete the old file and update the tracking entry
                File.Delete(Path.Combine(profileDir, ProfileFileNames[indexOld].Item2 + ".json"));
                ProfileFileNames[indexOld] = Tuple.Create(item, item.Name);
            }
        }
        else
        {
            ProfileFileNames.Add(Tuple.Create(item, item.Name));
        }
        File.WriteAllText(Path.Combine(profileDir, item.Name + ".json"), json);
    }

    public void ProfileItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is ProfileItem item && item.IsDirty)
        {
            if (nameof(ProfileItem.IsDefault).Equals(e.PropertyName) && item.IsDefault)
            {
                foreach (var profile in Profiles.Where(p => p != item))
                {
                    profile.IsDefault = false;
                }
            }
            foreach (var profile in Profiles.Where(p => p.IsDirty))
            {
                Save(profile);
                profile.IsDirty = false;
            }
        }
    }

    // Serializes HID writes so we never have two HidStream.Open() calls racing
    // on the same device. Cancellation is done via a sequence counter — older
    // pushes notice they have been superseded and skip themselves cheaply.
    private readonly SemaphoreSlim _pushLock = new(1, 1);
    private int _pushSeq;

    // Sync-coordination hooks. KeyboardPerformanceView registers here while
    // keystroke tracking is on so the firmware-side depth stream can be
    // paused around each profile push — otherwise 0xB7 chunks land in the
    // sync HidStream's read queue and exhaust TryWritePacket's drain.
    // Single-subscriber (set, not +=) — there's only ever one consumer.
    public Func<Task>? BeforeSyncAsync { get; set; }
    public Func<Task>? AfterSyncAsync { get; set; }

    public readonly record struct PushResult(bool Ok, bool Disconnected, bool Superseded, int PacketCount);

    // Fire-and-forget overload. Kept for callers that don't care about the
    // result (SwitchTo, ApplyCurrentProfile, hotkey handlers).
    public void PushCurrentProfile() => _ = PushCurrentProfileAsync();

    public async Task<PushResult> PushCurrentProfileAsync()
    {
        if (CurrentIndex < 0 || CurrentIndex >= Profiles.Count)
        {
            DebugLogger.Log($"PushCurrentProfile: skipped (CurrentIndex={CurrentIndex}, Profiles.Count={Profiles.Count})");
            return new PushResult(false, false, false, 0);
        }
        var current = Profiles[CurrentIndex];
        DebugLogger.Log($"PushCurrentProfile: profile='{current.Name}' (HasRTP={current.Profile.RTP is not null}, HasRemap={current.RemapProfile is not null})");
        settings.LastProfileUsedName = current.Name;

        if (keyboardManager.KeyboardWithSpecs is not { } keyboard)
        {
            DebugLogger.Log($"  no keyboard connected, push aborted");
            return new PushResult(false, true, false, 0);
        }

        // Build the full packet bundle for THIS profile against the connected
        // keyboard's visual layout. Fall back to A75 Pro's layout when the
        // model is unrecognized — the firmware-slot indices are the same; the
        // only thing the layout drives is the factory-default HID-usage map.
        var model = KeyboardLayoutResolver.Resolve(keyboard);
        var layoutFlat = KeyboardLayout.VisualFlatFor(model) ?? KeyboardLayout.A75ProFlat;
        var bundle = current.BuildFullProfilePackets(layoutFlat);
        DebugLogger.Log($"  built {bundle.Total} packets (remap={bundle.Remap.Length} rtpAuth={bundle.RtpAuthority.Length} acked={bundle.AckedBatch.Length} fire={bundle.FireForget.Length} hasClearRtpUpper={bundle.ClearRtpUpper is not null})");

        var mySeq = Interlocked.Increment(ref _pushSeq);

        return await Task.Run(async () =>
        {
            await _pushLock.WaitAsync().ConfigureAwait(false);
            bool beforeFired = false;
            try
            {
                if (Volatile.Read(ref _pushSeq) != mySeq)
                {
                    DebugLogger.Log($"PushCurrentProfile #{mySeq}: superseded before open, skipping");
                    return new PushResult(false, false, true, bundle.Total);
                }
                // Pause the keystroke-tracking listener (if any) BEFORE opening
                // our HID handle. The listener and this handle each receive
                // every input report Windows delivers, so unrelated 0xB7 depth
                // chunks would otherwise poison our drain loop.
                var before = BeforeSyncAsync;
                if (before is not null)
                {
                    beforeFired = true;
                    try { await before().ConfigureAwait(false); }
                    catch (Exception ex) { DebugLogger.Log($"PushCurrentProfile #{mySeq}: BeforeSyncAsync threw — {ex.Message}"); }
                }
                using HidStream stream = keyboard.Keyboard.Open();
                if (Volatile.Read(ref _pushSeq) != mySeq)
                {
                    DebugLogger.Log($"PushCurrentProfile #{mySeq}: superseded after open, skipping");
                    return new PushResult(false, false, true, bundle.Total);
                }
                var result = stream.WriteFullProfile(bundle);
                DebugLogger.Log($"PushCurrentProfile #{mySeq}: attempt 1 finished (ok={result.ok}, disconnected={result.disconnected})");
                if (!result.ok && !result.disconnected)
                {
                    await Task.Delay(200).ConfigureAwait(false);
                    if (Volatile.Read(ref _pushSeq) == mySeq)
                    {
                        var retry = stream.WriteFullProfile(bundle);
                        DebugLogger.Log($"PushCurrentProfile #{mySeq}: retry finished (ok={retry.ok})");
                        return new PushResult(retry.ok, retry.disconnected, false, bundle.Total);
                    }
                    else
                    {
                        DebugLogger.Log($"PushCurrentProfile #{mySeq}: superseded during retry wait, skipping");
                        return new PushResult(false, false, true, bundle.Total);
                    }
                }
                return new PushResult(result.ok, result.disconnected, false, bundle.Total);
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"PushCurrentProfile #{mySeq}: EXCEPTION {ex}");
                return new PushResult(false, false, false, bundle.Total);
            }
            finally
            {
                if (beforeFired)
                {
                    var after = AfterSyncAsync;
                    if (after is not null)
                    {
                        try { await after().ConfigureAwait(false); }
                        catch (Exception ex) { DebugLogger.Log($"PushCurrentProfile #{mySeq}: AfterSyncAsync threw — {ex.Message}"); }
                    }
                }
                _pushLock.Release();
            }
        }).ConfigureAwait(false);
    }

    // Debounced disk save for live edits in the keyboard view. The view calls
    // this after mutating ProfileItem nested state (Profile.Keys_Array,
    // Profile.Settings, RemapProfile.*) which won't trigger ProfileItem's own
    // PropertyChanged event. We use a timer-per-item so two profiles being
    // edited in quick succession don't overwrite each other's save schedule.
    private readonly Dictionary<ProfileItem, System.Threading.Timer> _saveTimers = new();
    private readonly object _saveTimersLock = new();
    private const int SaveDebounceMs = 500;

    public void ScheduleSave(ProfileItem item)
    {
        item.IsDirty = true;
        lock (_saveTimersLock)
        {
            if (_saveTimers.TryGetValue(item, out var existing))
            {
                existing.Change(SaveDebounceMs, Timeout.Infinite);
                return;
            }
            var timer = new System.Threading.Timer(_ =>
            {
                lock (_saveTimersLock)
                {
                    if (_saveTimers.Remove(item, out var t)) t.Dispose();
                }
                try
                {
                    if (item.IsDirty)
                    {
                        Save(item);
                        item.IsDirty = false;
                        DebugLogger.Log($"ScheduleSave: persisted '{item.Name}'");
                    }
                }
                catch (Exception ex)
                {
                    DebugLogger.Log($"ScheduleSave: EXCEPTION saving '{item.Name}' — {ex.GetType().Name}: {ex.Message}");
                }
            }, null, SaveDebounceMs, Timeout.Infinite);
            _saveTimers[item] = timer;
        }
    }

    public void RemoveProfileItems(params ProfileItem[] items)
    {
        if (items.Length == 0) return;

        foreach (var item in items)
        {
            // Drop any in-flight debounced save so the timer doesn't fire
            // after disk-delete and resurrect the JSON file.
            lock (_saveTimersLock)
            {
                if (_saveTimers.Remove(item, out var t)) t.Dispose();
            }
            item.IsDirty = false;

            Profiles.Remove(item);
            var profileFileNamesIndex = ProfileFileNames.FindIndex(p => p.Item1 == item);
            if (profileFileNamesIndex < 0 || profileFileNamesIndex >= ProfileFileNames.Count) return;
            ProfileFileNames.RemoveAt(profileFileNamesIndex);
            File.Delete(Path.Combine(profileDir, item.Name + ".json"));
        }
        ProfileCollectionChanged?.Invoke(items);
    }

    public bool IsSelected(ProfileItem profileItem)
    {
        return Profiles.IndexOf(profileItem) == CurrentIndex;
    }

    public void SwitchTo(ProfileItem profileItem)
    {
        var i = Profiles.IndexOf(profileItem);
        if (i >= 0 && i < Profiles.Count)
        {
            CurrentIndex = i;
            PushCurrentProfile();
        }
    }

    // Mark a profile as the current/active one WITHOUT pushing its stored
    // data to the keyboard. Use this when the user has just edited the
    // keyboard view directly — the firmware already reflects what they did,
    // so we just need the active-flag bookkeeping (green dot, top pill,
    // Activate-button state) to follow the profile they're working in.
    public void MarkActive(ProfileItem profileItem)
    {
        var i = Profiles.IndexOf(profileItem);
        if (i >= 0 && i < Profiles.Count && i != CurrentIndex)
        {
            CurrentIndex = i;
            settings.LastProfileUsedName = profileItem.Name;
        }
    }

    public void ApplyCurrentProfile()
    {
        PushCurrentProfile();
    }
}
