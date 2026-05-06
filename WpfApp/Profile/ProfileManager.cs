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

    public void ImportProfile(string path)
    {
        DebugLogger.Log($"ImportProfile: '{path}'");
        try
        {
            var text = File.ReadAllText(path);
            var profile = JsonSerializer.Deserialize<Driver.Profile>(text, options);
            if (profile is null) { DebugLogger.Log($"  deserialize returned null"); return; }
            var profileItem = new ProfileItem
            {
                Name = Path.GetFileNameWithoutExtension(path),
                Profile = profile,
                IsDirty = false
            };
            Save(profileItem);
            profileItem.PropertyChanged += ProfileItemChanged;
            Profiles.Add(profileItem);
            ProfileCollectionChanged?.Invoke([profileItem]);
            DebugLogger.Log($"  ok (Name='{profileItem.Name}', HasRTP={profile.RTP is not null}, KeyCount={profile.Keys_Array?.Length ?? 0})");
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

    public void PushCurrentProfile()
    {
        if (CurrentIndex < 0 || CurrentIndex >= Profiles.Count)
        {
            DebugLogger.Log($"PushCurrentProfile: skipped (CurrentIndex={CurrentIndex}, Profiles.Count={Profiles.Count})");
            return;
        }
        var current = Profiles[CurrentIndex];
        DebugLogger.Log($"PushCurrentProfile: profile='{current.Name}' (HasRTP={current.Profile.RTP is not null}, HasRemap={current.RemapProfile is not null})");

        var packets = current.BuildPackets();
        settings.LastProfileUsedName = current.Name;
        DebugLogger.Log($"  built {packets.Length} packets");

        if (keyboardManager.KeyboardWithSpecs is not { } keyboard)
        {
            DebugLogger.Log($"  no keyboard connected, push aborted");
            return;
        }

        var mySeq = Interlocked.Increment(ref _pushSeq);

        _ = Task.Run(async () =>
        {
            await _pushLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref _pushSeq) != mySeq)
                {
                    DebugLogger.Log($"PushCurrentProfile #{mySeq}: superseded before open, skipping");
                    return;
                }
                using HidStream stream = keyboard.Keyboard.Open();
                if (Volatile.Read(ref _pushSeq) != mySeq)
                {
                    DebugLogger.Log($"PushCurrentProfile #{mySeq}: superseded after open, skipping");
                    return;
                }
                var ok = stream.WritePacket(packets);
                DebugLogger.Log($"PushCurrentProfile #{mySeq}: attempt 1 finished (ok={ok})");
                if (!ok)
                {
                    await Task.Delay(200).ConfigureAwait(false);
                    if (Volatile.Read(ref _pushSeq) == mySeq)
                    {
                        var ok2 = stream.WritePacket(packets);
                        DebugLogger.Log($"PushCurrentProfile #{mySeq}: retry finished (ok={ok2})");
                    }
                    else
                    {
                        DebugLogger.Log($"PushCurrentProfile #{mySeq}: superseded during retry wait, skipping");
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"PushCurrentProfile #{mySeq}: EXCEPTION {ex}");
            }
            finally
            {
                _pushLock.Release();
            }
        });
    }

    public void QuickSwitchProfile()
    {
        var quickSwitchProfiles = Profiles.Where(p => p.SelectedForQuickSwitch).ToList();

        if (quickSwitchProfiles.Count == 0)
        {
            MessageBox.Show("No profiles are enabled for quick switching.\n\nPlease check 'Quick switch enabled' for at least one profile.", "Quick Switch", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Information);
            return;
        }

        if (quickSwitchProfiles.Count == 1)
        {
            SwitchTo(quickSwitchProfiles[0]);
            return;
        }

        var current = CurrentIndex >= 0 && CurrentIndex < Profiles.Count ? Profiles[CurrentIndex] : null;
        var currentIndex = current != null ? quickSwitchProfiles.IndexOf(current) : -1;
        var next = quickSwitchProfiles[(currentIndex + 1) % quickSwitchProfiles.Count];
        SwitchTo(next);
    }

    public void RemoveProfileItems(params ProfileItem[] items)
    {
        if (items.Length == 0) return;

        foreach (var item in items)
        {
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

    public void ApplyCurrentProfile()
    {
        PushCurrentProfile();
    }
}
