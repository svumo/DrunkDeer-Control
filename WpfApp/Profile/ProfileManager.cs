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

        // First run / empty profile directory: bootstrap a default profile so
        // the app is usable immediately. Without this the user lands on an
        // empty profile list, CurrentIndex stays -1, and every actuation / RT /
        // remap edit is silently discarded ("PushCurrentProfile: skipped") even
        // though the keyboard is detected fine.
        var bootstrapped = false;
        if (Profiles.Count == 0)
        {
            var def = CreateDefaultProfileItem();
            def.PropertyChanged += ProfileItemChanged;
            Profiles.Add(def);
            discoveredProfiles = [def];
            bootstrapped = true;
            DebugLogger.Log($"DiscoverProfiles: no profiles found — bootstrapped default profile '{def.Name}'");
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
        else if (bootstrapped)
        {
            // Activate the freshly-created default WITHOUT pushing it. Setting
            // CurrentIndex directly here fires CurrentProfileChanged, which
            // syncs the sidebar selection and makes MainWindow's "select
            // index 0" fallback a no-op — so we don't blast a flat 2.0 mm
            // profile over whatever the user already has on the keyboard.
            // The first explicit edit/sync is what pushes.
            CurrentIndex = 0;
        }
    }

    // Builds the first-run default profile. Empty Keys_Array is intentional:
    // both the push path (Packets.BuildFullProfilePackets) and the keyboard
    // view rehydrate widen a short array to 126 entries at AP 2.0 mm — the
    // same neutral default used everywhere else (ActuationDrawer.DefaultAp).
    private ProfileItem CreateDefaultProfileItem()
    {
        var item = new ProfileItem
        {
            Name = GenerateUniqueName("Default"),
            Profile = new Driver.Profile { Showname = "Default", Storagename = "Default" },
            IsDefault = true,
            IsDirty = false,
        };
        Save(item);
        return item;
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
                // Old profiles saved when the AP slider went to 3.8 mm carry
                // values outside the actually-writable wire range (0.2-2.0 mm).
                // Clamp on load so subsequent UI rehydrate + saves don't keep
                // round-tripping the out-of-range numbers.
                profile.Profile?.ClampActuationRange();
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

    // Whether the previous successful push carried Type-4 LW/RDT pair entries.
    // Used to decide whether the firmware needs a 600ms commit re-push to
    // flush a stale pair-table cache. The transition-out direction
    // (pair-bearing → no-pair) is also pair-table-sensitive, not just
    // transition-in: turning RDT off and back on without this would leave
    // the firmware briefly emitting the previous profile's release HID on
    // the new profile's press key (the "R→A flicker" symptom observed
    // 2026-05-21 on A75 Pro 0.023).
    private bool _lastPushHadPairs;

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
        // Resolve firmware capabilities for diagnostic labelling. The wire
        // format is uniform across supported firmwares — anything below
        // the per-model floor in FirmwareCapabilities.LatestKnownFirmware
        // gets the too-old modal in KeyboardPerformanceView and isn't
        // worth a per-version branch.
        var capabilities = FirmwareCapabilities.Resolve(
            keyboard.Specs.KeyboardType,
            keyboard.Specs.FirmwareVersionNumeric);
        var bundle = current.BuildFullProfilePackets(layoutFlat, capabilities);
        DebugLogger.Log($"  built {bundle.Total} packets (precision={capabilities.Precision} tier={capabilities.Tier}; remap={bundle.Remap.Length} rtpAuth={bundle.RtpAuthority.Length} acked={bundle.AckedBatch.Length} fire={bundle.FireForget.Length} hasClearRtpUpper={bundle.ClearRtpUpper is not null} hasClearRtp={bundle.ClearRtp is not null} hasLwPairs={bundle.LwPairs is not null} hasRdtPairs={bundle.RdtPairs is not null})");

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

                // ── OEM gen-2 sync path (beta.22) ──────────────────────────
                // For the OEM A75 Pro (VID 0x19F5) firmware ignores the gen-1
                // 0xB6 / 0xFD per-key writes (BuildPacketKeyPoint family).
                // It only speaks the 0x55 0xA1 WriteKeyTriggerChunk sequence,
                // proven 2026-05-25 by the user's USBPcap capture. See
                // docs/gen2-wire-format-confirmed.md.
                //
                // Detection: KeyboardManager.TryGen2WebHidDetection synthesizes
                // KeyboardSpecs { KeyboardType=750, FirmwareVersion="OEM 1.7" }
                // on a successful 0x55 0x04 ReadBaseBlock probe. We use the
                // "OEM " prefix on FirmwareVersion as the sentinel rather
                // than relying on transport plumbing — keeps the branch
                // contained to ProfileManager.
                bool isOemGen2 = keyboard.Specs.FirmwareVersion?.StartsWith("OEM") == true
                              && keyboard.Specs.KeyboardType == 750;
                if (isOemGen2)
                {
                    byte activeProfile = keyboard.Specs.ActiveProfileIndex;
                    DebugLogger.Log($"PushCurrentProfile #{mySeq}: OEM gen-2 sync path (KeyboardType={keyboard.Specs.KeyboardType} Firmware='{keyboard.Specs.FirmwareVersion}' ActiveProfileIndex={activeProfile})");
                    var oemResult = SyncOemGen2(stream, current.Profile, mySeq, activeProfile);
                    return new PushResult(oemResult.ok, oemResult.disconnected, false, oemResult.packetCount);
                }
                // ───────────────────────────────────────────────────────────

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
                // Commit re-push when *either* the new bundle carries
                // Type-4 pair entries (LW/RDT activation) OR the previous
                // push did (pair-table flush). Empirically (2026-05-21,
                // A75 Pro 0.023) the firmware ACKs the first push but
                // doesn't actually commit pair-table changes until a
                // second identical push lands ~600ms later.
                //
                // Both directions matter:
                //   • pair → no-pair: removing the last pair (or toggling
                //     RDT/LW off) without the re-push leaves the firmware
                //     emitting the old release HID until something else
                //     forces a second sync. User-visible as "I removed
                //     the pair but it still fires."
                //   • no-pair → pair: covered by the new-bundle gate.
                //   • pair → same pair (toggle off-on): the firmware's
                //     stale table activates briefly during the off-on
                //     transition. User-visible as the "R→A flicker for
                //     a second before correcting to R→T" symptom.
                //
                // PrePassClearRtpUpper is non-null iff hasAnyPairs in
                // BuildFullProfilePackets, so it's a clean has-pairs
                // signal. Lives here (not in DoSyncAsync) so SwitchTo /
                // hotkey-driven profile switches get the same commit
                // treatment as manual Sync clicks.
                bool currentHasPairs = bundle.HasAnyPairs;
                bool needsCommitRepush = currentHasPairs || _lastPushHadPairs;
                if (result.ok && needsCommitRepush)
                {
                    await Task.Delay(600).ConfigureAwait(false);
                    if (Volatile.Read(ref _pushSeq) == mySeq)
                    {
                        var commit = stream.WriteFullProfile(bundle);
                        DebugLogger.Log($"PushCurrentProfile #{mySeq}: commit re-push finished (ok={commit.ok}, reason={(currentHasPairs ? "new-has-pairs" : "previous-had-pairs")})");
                    }
                    else
                    {
                        DebugLogger.Log($"PushCurrentProfile #{mySeq}: superseded during commit wait, skipping re-push");
                    }
                }
                if (result.ok) _lastPushHadPairs = currentHasPairs;
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

    // ── OEM gen-2 sync ───────────────────────────────────────────────────
    //
    // Emits the 0x55 0xA1 WriteKeyTriggerChunk sequence (19 packets, AP +
    // RT press / release per key, 128 firmware slots = 1024 bytes), then
    // the mode-toggle outliers (CommonSwitch 0xB5, LW Replace 0xFC 0x0B,
    // AutoMatch 0xFD 0x0C, Keystroke Tracking 0xB6 0x03).
    //
    // Mode-toggle packets are SPECULATIVE on OEM firmware — we have hard
    // capture proof for 0x55 0xA1 only. The 0xB5 / 0xFC / 0xFD opcodes
    // are sent in case the firmware accepts them (matches what gen-1 does);
    // if not, only the actuation sync takes and we'll see "send ok but no
    // observable behaviour change" reports. The next capture isolating a
    // mode-toggle slider movement on the official site will tell us the
    // real wire format.
    //
    // All bytes go through the registered IGen2Channel (Gen2WebHidChannel
    // for OEM hardware) via HidDeviceExtensions.WritePacketNoAck — channel
    // routing already in place from beta.21.
    //
    // Returns (ok, disconnected, packetCount). `ok` is true if every send
    // returned true; one failure marks the whole batch as failed but does
    // NOT short-circuit — we want to see in the log which specific packets
    // failed.
    private (bool ok, bool disconnected, int packetCount) SyncOemGen2(HidStream stream, Driver.Profile profile, int mySeq, byte activeProfileIndex)
    {
        // Build the 128 × 8 = 1024-byte KeyTrigger region from per-key
        // AP / DS / US. Slot indices beyond Keys_Array.Length get
        // firmware-default values.
        int keyCount = Math.Min(profile.Keys_Array.Length, Driver.PacketsGen2.KEY_TRIGGER_SLOT_COUNT);
        DebugLogger.Log($"  OEM gen-2 sync #{mySeq}: encoding {keyCount} keys (region size {Driver.PacketsGen2.KEY_TRIGGER_REGION_SIZE} bytes)");
        var region = Driver.KeyTriggerEntry.EncodeAll(Driver.PacketsGen2.KEY_TRIGGER_SLOT_COUNT, i =>
        {
            if (i >= keyCount) return (60, Driver.KeyTriggerEntry.DEFAULT_RT_PRESS, Driver.KeyTriggerEntry.DEFAULT_RT_RELEASE);
            var k = profile.Keys_Array[i];
            int act = (int)Math.Round(k.Action_Point * 100m);
            int rtp = (int)Math.Round(k.Downstroke    * 100m);
            int rtr = (int)Math.Round(k.Upstroke      * 100m);
            return (act, rtp, rtr);
        });
        DebugLogger.LogVerbose($"  OEM gen-2 sync #{mySeq}: first 3 records = [{region[0]:x2} {region[1]:x2} {region[2]:x2} {region[3]:x2} {region[4]:x2} {region[5]:x2} {region[6]:x2} {region[7]:x2}] [{region[8]:x2} {region[9]:x2} {region[10]:x2} {region[11]:x2} {region[12]:x2} {region[13]:x2} {region[14]:x2} {region[15]:x2}] [{region[16]:x2} {region[17]:x2} {region[18]:x2} {region[19]:x2} {region[20]:x2} {region[21]:x2} {region[22]:x2} {region[23]:x2}]");

        var chunks = Driver.PacketsGen2.BuildWriteKeyTriggerChunkSequence(region, profileIndex: activeProfileIndex).ToList();
        DebugLogger.Log($"  OEM gen-2 sync #{mySeq}: built {chunks.Count} chunk packet(s) for KeyTrigger region (targeting profile {activeProfileIndex} = addr 0x{activeProfileIndex * Driver.PacketsGen2.KEY_TRIGGER_REGION_SIZE:x4})");

        int sent = 0;
        int failed = 0;
        for (int i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            bool isLast = chunk[7] == 0x01;
            ushort addr = (ushort)(chunk[5] | (chunk[6] << 8));
            byte len = chunk[4];
            byte cs = chunk[3];
            DebugLogger.LogVerbose($"  OEM gen-2 sync #{mySeq}: chunk {i + 1}/{chunks.Count} addr=0x{addr:x4} len={len} is_last={(isLast ? 1 : 0)} cs=0x{cs:x2}");
            if (stream.WritePacketNoAck(chunk))
            {
                sent++;
            }
            else
            {
                failed++;
                DebugLogger.Log($"  OEM gen-2 sync #{mySeq}: chunk {i + 1}/{chunks.Count} WRITE FAILED");
            }
            // 5ms spacing — matches the inter-packet gap observed in the user's
            // capture (frames 7..79 had 2..5ms gaps). Avoids slamming the
            // firmware's input queue.
            Thread.Sleep(5);
        }
        DebugLogger.Log($"  OEM gen-2 sync #{mySeq}: KeyTrigger region complete — {sent} ok / {failed} failed of {chunks.Count} chunk(s)");

        // ── Speculative gen-1 mode-toggle packets ────────────────────────
        // These use the gen-1 opcodes (0xB5 / 0xFC / 0xFD). The OEM firmware
        // either honours them (same as gen-1 firmware does) or silently
        // ignores them. Either way, the channel layer doesn't throw and we
        // log every send so the next user capture can confirm reception.
        var settings = profile.Settings;
        int modeOk = 0, modeFail = 0;
        var modePackets = new (string name, byte[] packet)[]
        {
            ("CommonSwitch (0xB5)",        Driver.Packets.BuildCommonSwitchPacket(settings)),
            ("LastWinReplace (0xFC 0x0B)", Driver.Packets.BuildLastWinReplacePacket(settings.LastWinReplaceEnabled)),
            ("AutoMatchMode (0xFD 0x0C)",  Driver.Packets.BuildAutoMatchModePacket(settings.AutoMatchMode)),
            ("KeystrokeTracking (0xB6 0x03)", Driver.Packets.BuildKeystrokeTrackingPacket(settings.KeystrokeTrackingEnabled)),
        };
        foreach (var (name, pkt) in modePackets)
        {
            DebugLogger.LogVerbose($"  OEM gen-2 sync #{mySeq}: sending {name} (Turbo={settings.TurboEnabled} RT={settings.RapidTriggerEnabled} LW={settings.LastWinEnabled} RDT={settings.ReleaseDualTriggerEnabled} RTMatch={settings.RTMatchEnabled} LWReplace={settings.LastWinReplaceEnabled} AutoMatch={settings.AutoMatchMode} Tracking={settings.KeystrokeTrackingEnabled})");
            if (stream.WritePacketNoAck(pkt)) modeOk++; else modeFail++;
            Thread.Sleep(5);
        }
        DebugLogger.Log($"  OEM gen-2 sync #{mySeq}: mode toggles complete — {modeOk} ok / {modeFail} failed of {modePackets.Length} packet(s) (speculative — firmware may silently ignore)");

        int total = chunks.Count + modePackets.Length;
        int totalFailed = failed + modeFail;
        DebugLogger.Log($"  OEM gen-2 sync #{mySeq}: DONE total={total} ok={sent + modeOk} failed={totalFailed}");
        return (totalFailed == 0, false, total);
    }
    // ─────────────────────────────────────────────────────────────────────

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
