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
    // Cached firmware slot map for the connected gen-2 OEM keyboard.
    // Populated on first sync after connect by reading the default
    // keymap (sub-cmd 0x07). Lets us route per-key writes (actuation,
    // LW pair RTP, LW commit) to the firmware's *internal* slot index
    // rather than our visual layout's KeyIndex — necessary because the
    // two numberings differ (e.g. on tester B's A75 Pro OEM: visual
    // A=KeyIndex 64 vs firmware slot 37). Re-read on each reconnect so
    // a hardware swap can't reuse a stale map. Read lock protects the
    // field; map itself is immutable once constructed.
    private Driver.KeyMatrixSlotMap? _gen2SlotMap;
    private readonly object _gen2SlotMapLock = new();
    // beta.40: track which firmware slots we wrote non-default user-keymap
    // entries to in the previous sync (LW pairs, RDT pairs, per-key
    // remaps). Next sync: any slot that was "armed" before but isn't now
    // gets a single-slot 0x09 write of its standard-HID default to clear
    // the stale entry. Matches the official driver's pattern (see JS
    // `e.updateUserKey(o,i,0,r)` + `setUserKey(...)` on advanced-key
    // removal) — bulk-write the entire 384-byte region in beta.38 was
    // a sledgehammer that put the firmware's SOCD state machine in a
    // confused state (per tester B's beta.38 debug log: pair table
    // correct, paired keys frozen).
    private readonly HashSet<int> _gen2ArmedSlots = new();
    private readonly object _gen2ArmedSlotsLock = new();
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
                    var oemResult = SyncOemGen2(stream, current, layoutFlat, mySeq, activeProfile);
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
    private (bool ok, bool disconnected, int packetCount) SyncOemGen2(HidStream stream, ProfileItem item, IReadOnlyList<Driver.LayoutKey> layoutFlat, int mySeq, byte activeProfileIndex)
    {
        var profile = item.Profile;

        // ── Firmware slot-map readback (0x55 0x07) ────────────────────────
        // Read the default keymap once per session so we can route per-key
        // writes (actuation + LW) to the correct *firmware* slot for each
        // HID code. Without this, beta.27..beta.34 wrote A's data at our
        // visual KeyIndex 64, but tester B's gen-2 OEM firmware has A at
        // slot 37 — so per-key changes landed on the wrong physical keys.
        // See KeyMatrixSlotMap.cs for the why and PacketsGen2.cs §"Default
        // keymap readback" for the wire format.
        Driver.KeyMatrixSlotMap? slotMap;
        lock (_gen2SlotMapLock) { slotMap = _gen2SlotMap; }
        if (slotMap is null)
        {
            slotMap = TryReadGen2SlotMap(stream, mySeq);
            if (slotMap is not null)
            {
                lock (_gen2SlotMapLock) { _gen2SlotMap = slotMap; }
            }
        }
        else
        {
            DebugLogger.LogVerbose($"  OEM gen-2 sync #{mySeq}: using cached slot map ({slotMap.StandardKeyCount} standard keys)");
        }

        // Build hid → KeyIndex reverse from our visual layout so we can
        // cross-check the slot map's HID assignments against where our
        // visual layout expects each key. Also used by the LW commit
        // builder to look up per-key AP from Profile.Keys_Array.
        var defaultHidMap = Driver.KeyboardLayout.BuildDefaultHidUsageMap(layoutFlat);
        var hidToKeyIndex = new Dictionary<byte, int>();
        for (int ki = 0; ki < defaultHidMap.Length; ki++)
        {
            byte h = defaultHidMap[ki];
            if (h != 0 && !hidToKeyIndex.ContainsKey(h)) hidToKeyIndex[h] = ki;
        }

        // Diagnostic: when we have a slot map, compare it to our visual
        // layout for the WASD home-row keys. If our beta.27 actuation
        // sync was writing to the wrong slot (e.g. A's data at slot 64
        // when the firmware has A at slot 37), this log will show the
        // mismatch unambiguously. No behaviour change in this beta — we
        // still write Profile.Keys_Array[i] at firmware slot i — but the
        // log tells us whether that's correct for the next beta.
        if (slotMap is not null)
        {
            void LogProbe(string name, byte hid, int expectedFwSlot)
            {
                int actualFwSlot = slotMap.TryGetSlotForHid(hid);
                int ourKeyIndex = hidToKeyIndex.TryGetValue(hid, out int ki) ? ki : -1;
                bool matchesExpected = actualFwSlot == expectedFwSlot;
                bool matchesKeyIndex = actualFwSlot == ourKeyIndex;
                DebugLogger.Log($"  OEM gen-2 sync #{mySeq}: PROBE {name} (HID 0x{hid:x2}) — fw slot map says slot {actualFwSlot}, our KeyIndex is {ourKeyIndex}, captured slot was {expectedFwSlot}. matchesCaptured={matchesExpected} matchesOurKeyIndex={matchesKeyIndex}");
            }
            // Captured slot numbers from tester B's usb3/5/6.pcapng:
            // A→37, D→39, S→38, W→26. If the slot map readback matches
            // these, the JS-derived address formulas are correct.
            LogProbe("A", 0x04, 37);
            LogProbe("D", 0x07, 39);
            LogProbe("S", 0x16, 38);
            LogProbe("W", 0x1A, 26);
            // Arrow probes: no capture-verified slot yet; just log whatever
            // the slot map says so we can verify against the K75 layout.
            int slotLeft  = slotMap.TryGetSlotForHid(0x50);
            int slotDown  = slotMap.TryGetSlotForHid(0x51);
            int slotRight = slotMap.TryGetSlotForHid(0x4F);
            int slotUp    = slotMap.TryGetSlotForHid(0x52);
            DebugLogger.Log($"  OEM gen-2 sync #{mySeq}: PROBE arrows — Left=slot{slotLeft} Down=slot{slotDown} Right=slot{slotRight} Up=slot{slotUp}");

            // Dump the full slot map at NORMAL log level so we get every
            // key's firmware slot in tester B's debug log without having
            // to enable verbose. Covers anything pair-based — LW, RDT,
            // any future feature — not just WASD. Format per slot:
            //   "slot 037: type=10 code1=00 code2=04 (A)"
            // type=10 = standard HID key; type=FF = empty; other types
            // (modifiers, media, macros) are noted by name too.
            for (int s = 0; s < slotMap.SlotCount; s++)
            {
                var (type, code1, code2) = slotMap.RawAtSlot(s);
                if (type == 0xFF) continue; // skip unused slots, keeps the log dense
                string label = DescribeKeyMatrixEntry(type, code1, code2);
                DebugLogger.Log($"  OEM gen-2 sync #{mySeq}: slot {s:D3}: type=0x{type:x2} code1=0x{code1:x2} code2=0x{code2:x2}  {label}");
            }
        }

        // Diagnostic: read the firmware's CURRENT KeyTrigger entries at
        // both the captured-slot positions (37/39/38/26 = A/D/S/W per
        // tester B's USBPcap) and the positions our beta.27/34 sync
        // wrote to (64/66/65/44 = A/D/S/W per our Profile.Keys_Array
        // KeyIndex). Compare the AP values across positions:
        //   - If captured slots hold the AP that USER configured, our
        //     beta.27 sync was secretly correct (or the firmware ignored
        //     our wrong-slot writes).
        //   - If our KeyIndex slots hold the user's AP and captured
        //     slots hold firmware defaults, we've been writing to the
        //     wrong slots and beta.36 needs the dynamic remap.
        //   - If neither holds a recognisable value, something else is
        //     going on and we need to widen the probe.
        TryProbeKeyTriggerEntries(stream, mySeq, activeProfileIndex, slotMap);

        // Build the 128 × 8 = 1024-byte KeyTrigger region from per-key
        // AP / DS / US.
        //
        // beta.37: when we have a slot map, iterate FIRMWARE SLOTS and
        // pull each slot's data from Profile.Keys_Array via HID lookup.
        // This fixes the beta.27..beta.34 shift where A's data (our
        // KeyIndex 64) was being written to slot 64 even though the
        // firmware has A at slot 37 — silently affecting whatever key
        // sat at slot 64 in the firmware instead of A. The probes
        // confirmed every probed slot held identical default data on a
        // fresh-reset keyboard, so user AP tweaks weren't surviving
        // across all 12 betas. This routes them to the right slot.
        //
        // Fallback: if the slot-map read failed, we revert to direct
        // KeyIndex iteration (beta.27 behaviour) so the sync doesn't
        // skip actuation entirely.
        // ── KeyTrigger full-region write — SKIPPED IN BETA.41 ──────────────
        //
        // Diagnostic: tester B's beta.40 log (debug 24) showed every wire
        // packet sent + ACKed correctly (KeyTrigger 19/19, pair table 5/5,
        // user-keymap 8/8, 0xA1 LW commits 8/8, zero skips) yet his
        // LW-paired keys remained frozen. Per the decision tree in
        // docs/handoff-beta40-shipped.md, suspect #1 is this region write.
        //
        // Why it's suspect: we write 19 chunks at addr 0x0000..0x03F0 with
        // key_mode=0 for EVERY slot before the LW-specific 0xA1 commits
        // (key_mode=1 for paired slots only). The official OEM driver's
        // usb5 capture does NOT do this on an LW-only toggle — it only
        // writes the region when the user actually moves AP/RT sliders.
        // Hypothesis: the all-zeros key_mode region write leaves the
        // firmware in a transitional state the per-pair 0xA1 commits
        // don't fully override.
        //
        // Test: ship this skipped, ask tester B to repeat the A↔D LW
        // smoke test. If pairs activate → confirmed cause; re-add the
        // region write GATED by "AP/RT actually changed since last sync"
        // to match official driver behaviour. If pairs still frozen →
        // move to suspect #2 (FuncBlock R-M-W, also unique to our flow
        // vs the official driver).
        //
        // Per-key AP/RT changes will not land on the firmware while this
        // is skipped. Acceptable for the diagnostic — the only confirmed-
        // verified gen-2 OEM action is per-key writes via this path, and
        // tester B isn't tweaking sliders for the LW test. Revert this
        // block exactly to restore the slider-sync path.
        DebugLogger.Log($"  OEM gen-2 sync #{mySeq}: KeyTrigger region SKIPPED (beta.41 diagnostic — testing LW activation without preceding key_mode=0 region write)");

        // Pull settings up-front: the FuncBlock LW master-bit flip below
        // and the LW pair table writes that follow both branch on
        // settings.LastWinEnabled, so we resolve it once.
        var settings = profile.Settings;

        // ── FuncBlock LW master-bit flip (gen-2 0x55 0x05 / 0x06) ────────
        // Beta.31 fix. usb3.pcapng captured the official driver toggling
        // LW master via a read-modify-write of the FuncBlock (frames 7499
        // → 21301): the only byte that changes is data offset 8 of the
        // first chunk, going 0x06 → 0x0E (bit 3 set). Without flipping
        // this bit, the firmware never consults the pair table at 0x0100,
        // which is why beta.28..30 wrote correct pair data but tester B
        // saw no SOCD behaviour in Notepad.
        //
        // We do a proper read-modify-write to preserve every other Func
        // setting (RGB mode, debounce, polling rate, etc.). If the read
        // fails (firmware silent on 0x55 0x05) we skip the write entirely
        // — writing a zero-filled FuncBlock would nuke unrelated config.
        int funcBlockOk = 0, funcBlockFail = 0;
        bool funcBlockApplied = false;
        try
        {
            var readPrimary = stream.WritePacket(Driver.PacketsGen2.BuildReadFuncBlock(
                Driver.PacketsGen2.FUNC_BLOCK_PRIMARY_LENGTH,
                Driver.PacketsGen2.FUNC_BLOCK_PRIMARY_ADDR));
            var readContinuation = stream.WritePacket(Driver.PacketsGen2.BuildReadFuncBlock(
                Driver.PacketsGen2.FUNC_BLOCK_CONTINUATION_LENGTH,
                Driver.PacketsGen2.FUNC_BLOCK_CONTINUATION_ADDR));
            // Both responses must be the expected 0x55-family shape and
            // long enough to hold the data we asked for. RESPONSE_DATA_OFFSET
            // (=8) header bytes precede the data.
            int needPrimary      = Driver.PacketsGen2.RESPONSE_DATA_OFFSET + Driver.PacketsGen2.FUNC_BLOCK_PRIMARY_LENGTH;
            int needContinuation = Driver.PacketsGen2.RESPONSE_DATA_OFFSET + Driver.PacketsGen2.FUNC_BLOCK_CONTINUATION_LENGTH;
            bool primaryOk = readPrimary != null
                && readPrimary.Length >= needPrimary
                && Driver.PacketsGen2.IsExtendedGatewayResponse(readPrimary, Driver.PacketsGen2.SUB_READ_FUNC_BLOCK);
            bool continuationOk = readContinuation != null
                && readContinuation.Length >= needContinuation
                && Driver.PacketsGen2.IsExtendedGatewayResponse(readContinuation, Driver.PacketsGen2.SUB_READ_FUNC_BLOCK);
            if (primaryOk && continuationOk)
            {
                var primary = new byte[Driver.PacketsGen2.FUNC_BLOCK_PRIMARY_LENGTH];
                Array.Copy(readPrimary!, Driver.PacketsGen2.RESPONSE_DATA_OFFSET, primary, 0, primary.Length);
                var continuation = new byte[Driver.PacketsGen2.FUNC_BLOCK_CONTINUATION_LENGTH];
                Array.Copy(readContinuation!, Driver.PacketsGen2.RESPONSE_DATA_OFFSET, continuation, 0, continuation.Length);

                byte beforeByte = primary[Driver.PacketsGen2.FUNC_BLOCK_LW_MASTER_BYTE_OFFSET];
                bool wantLwOn = settings.LastWinEnabled;
                if (wantLwOn)
                    primary[Driver.PacketsGen2.FUNC_BLOCK_LW_MASTER_BYTE_OFFSET] |= Driver.PacketsGen2.FUNC_BLOCK_LW_MASTER_BIT;
                else
                    primary[Driver.PacketsGen2.FUNC_BLOCK_LW_MASTER_BYTE_OFFSET] &= unchecked((byte)~Driver.PacketsGen2.FUNC_BLOCK_LW_MASTER_BIT);
                byte afterByte = primary[Driver.PacketsGen2.FUNC_BLOCK_LW_MASTER_BYTE_OFFSET];

                DebugLogger.Log($"  OEM gen-2 sync #{mySeq}: FuncBlock read OK — LW master byte 0x{beforeByte:x2} → 0x{afterByte:x2} (wantLwOn={wantLwOn})");

                var writePrimary = Driver.PacketsGen2.BuildWriteFuncBlockChunk(
                    primary, Driver.PacketsGen2.FUNC_BLOCK_PRIMARY_ADDR, isLast: false);
                var writeContinuation = Driver.PacketsGen2.BuildWriteFuncBlockChunk(
                    continuation, Driver.PacketsGen2.FUNC_BLOCK_CONTINUATION_ADDR, isLast: true);
                if (stream.WritePacketNoAck(writePrimary)) funcBlockOk++; else funcBlockFail++;
                Thread.Sleep(5);
                if (stream.WritePacketNoAck(writeContinuation)) funcBlockOk++; else funcBlockFail++;
                Thread.Sleep(5);
                funcBlockApplied = funcBlockFail == 0;
            }
            else
            {
                DebugLogger.Log($"  OEM gen-2 sync #{mySeq}: FuncBlock read failed (primaryOk={primaryOk} continuationOk={continuationOk}) — skipping master-bit flip");
            }
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"  OEM gen-2 sync #{mySeq}: FuncBlock read-modify-write EXCEPTION {ex.GetType().Name}: {ex.Message} — skipping master-bit flip");
        }
        DebugLogger.Log($"  OEM gen-2 sync #{mySeq}: FuncBlock master-bit flip {(funcBlockApplied ? "applied" : "skipped")} ({funcBlockOk} ok / {funcBlockFail} failed)");

        // ── Pair table (gen-2 0x55 0xA5) — shared between LW and RDT ─────
        // The pair table is a flat list of (mainKey, triggerKey) HID-code
        // entries. LW and RDT pairs go in the SAME table; the type byte
        // in each pair's user-keymap entry (0x55 0x09) is what tells the
        // firmware whether the pair is socd/LW (0x94) or oks/RDT (0x95).
        // Verified via usb7.pcapng (RDT D↔T fresh-reset save 2026-05-26):
        // pair table format is byte-identical to LW; only the 0x09
        // entry's type byte differs.
        var lwPairsRaw  = item.RemapProfile?.LwPairs  ?? [];
        var rdtPairsRaw = item.RemapProfile?.RdtPairs ?? [];
        var lwPairs  = new System.Collections.Generic.List<(byte HidA, byte HidB)>();
        var rdtPairs = new System.Collections.Generic.List<(byte HidA, byte HidB)>();
        static (byte HidA, byte HidB)? ResolvePair(byte[] pair, byte[] hidMap)
        {
            if (pair is not { Length: 2 }) return null;
            byte slotA = pair[0];
            byte slotB = pair[1];
            if (slotA >= 126 || slotB >= 126) return null;
            byte hidA = hidMap[slotA];
            byte hidB = hidMap[slotB];
            if (hidA == 0 || hidB == 0 || hidA == hidB) return null;
            return (hidA, hidB);
        }
        if (settings.LastWinEnabled)
        {
            foreach (var pair in lwPairsRaw)
                if (ResolvePair(pair, defaultHidMap) is { } resolved) lwPairs.Add(resolved);
        }
        if (settings.ReleaseDualTriggerEnabled)
        {
            foreach (var pair in rdtPairsRaw)
                if (ResolvePair(pair, defaultHidMap) is { } resolved) rdtPairs.Add(resolved);
        }

        // Combined pair table — LW pairs first, then RDT pairs.
        var allPairs = new System.Collections.Generic.List<(byte HidA, byte HidB)>(lwPairs.Count + rdtPairs.Count);
        allPairs.AddRange(lwPairs);
        allPairs.AddRange(rdtPairs);

        int lwOk = 0, lwFail = 0;
        // Always send the pair table — empty when both LW and RDT are off,
        // so the firmware clears its prior table. Mirrors the gen-1
        // BuildClearRtpPacket discipline of explicit clears on every sync.
        var lwChunks = Driver.PacketsGen2.BuildWriteLwPairsSequence(allPairs).ToList();
        DebugLogger.Log($"  OEM gen-2 sync #{mySeq}: pair table — {lwPairs.Count} LW + {rdtPairs.Count} RDT = {allPairs.Count} user pair(s) → {allPairs.Count * 2} firmware pair(s), {lwChunks.Count} chunk packet(s)");
        for (int i = 0; i < lwChunks.Count; i++)
        {
            var chunk = lwChunks[i];
            bool isLast = chunk[7] == 0x01;
            ushort addr = (ushort)(chunk[5] | (chunk[6] << 8));
            byte cs = chunk[3];
            DebugLogger.LogVerbose($"  OEM gen-2 sync #{mySeq}: LW chunk {i + 1}/{lwChunks.Count} addr=0x{addr:x4} is_last={(isLast ? 1 : 0)} cs=0x{cs:x2}");
            if (stream.WritePacketNoAck(chunk)) lwOk++; else lwFail++;
            Thread.Sleep(5);
        }
        // ── User-keymap region + LW commit writes ────────────────────────
        //
        // beta.40: TARGETED single-slot user-keymap writes (no more bulk).
        //
        // Beta.38 bulk-wrote the entire 384-byte user-keymap region every
        // sync, which fixed the "ghost pair" bug from beta.37 but
        // *introduced* a new bug: tester B's debug log (debug 21) showed
        // pair tables and user-keymap entries written byte-identical to
        // the official driver's captures, but the LW-paired keys froze
        // (didn't fire at all). Turning LW off restored normal typing.
        //
        // Comparing our writes to the official driver's usb5.pcapng:
        //   Official: 9 packets total (5×0xA5 + 2×0x09 + 2×0xA1)
        //   Us:       ~30+ packets (19×0xA1 KeyTrigger region +
        //              4×FuncBlock R-M-W + 5×0xA5 + 7×0x09 bulk +
        //              N×0xA1 LW commits)
        //
        // The extra writes — particularly the bulk 0x09 region write
        // and the KeyTrigger region write — appear to put the SOCD
        // state machine in a confused state where paired keys won't
        // fire. beta.40 drops the bulk 0x09 write and matches the
        // official driver's pattern: write each slot's user-keymap
        // entry individually, only for slots that actually change.
        //
        // Stale-entry handling (the original reason for bulk write):
        // track which slots we armed last sync. On each new sync, any
        // slot armed before but not now gets a single-slot 0x09 write
        // restoring its default `[type=0x10, code1=0x00, code2=HID]`.
        // Matches the JS `e.updateUserKey(o,i,0,r)` + `setUserKey(...)`
        // call on advanced-key removal.
        int userKeyOk = 0, userKeyFail = 0;
        int activateOk = 0, activateFail = 0;
        int lwSkippedNoSlot = 0;
        var lwArmedSlots = new List<(int slotA, int slotB, byte hidA, byte hidB)>();
        var newArmedSlots = new HashSet<int>();

        if (slotMap is not null)
        {
            // ── Step 1: compute new armed slots + emit the writes ──────
            // For each LW/RDT pair and each remap, emit ONE single-slot
            // 0x09 write and record the slot in newArmedSlots.
            byte pairIndex = 0;

            void EmitPair(byte hidA, byte hidB, byte entryType, string featureName, bool recordForCommit)
            {
                int slotA = slotMap.TryGetSlotForHid(hidA);
                int slotB = slotMap.TryGetSlotForHid(hidB);
                if (slotA < 0 || slotB < 0 || slotA > 255 || slotB > 255 ||
                    slotA >= Driver.PacketsGen2.KEY_MATRIX_SLOT_COUNT ||
                    slotB >= Driver.PacketsGen2.KEY_MATRIX_SLOT_COUNT)
                {
                    DebugLogger.Log($"  OEM gen-2 sync #{mySeq}: {featureName} pair HID 0x{hidA:x2}↔0x{hidB:x2} — slot lookup failed or out of range (slotA={slotA} slotB={slotB}); skipping");
                    lwSkippedNoSlot++;
                    pairIndex += 2;
                    return;
                }
                var entryA = Driver.PacketsGen2.BuildWriteUserKeyEntry(slotA, entryType, pairIndex, (byte)slotB);
                var entryB = Driver.PacketsGen2.BuildWriteUserKeyEntry(slotB, entryType, (byte)(pairIndex + 1), (byte)slotA);
                DebugLogger.Log($"  OEM gen-2 sync #{mySeq}: {featureName} pair HID 0x{hidA:x2}↔0x{hidB:x2} (slot {slotA}↔{slotB}) — 0x09 single-slot writes type=0x{entryType:x2} pair_idx={pairIndex}/{pairIndex + 1}");
                if (stream.WritePacketNoAck(entryA)) userKeyOk++; else userKeyFail++;
                Thread.Sleep(5);
                if (stream.WritePacketNoAck(entryB)) userKeyOk++; else userKeyFail++;
                Thread.Sleep(5);
                newArmedSlots.Add(slotA);
                newArmedSlots.Add(slotB);
                if (recordForCommit)
                    lwArmedSlots.Add((slotA, slotB, hidA, hidB));
                pairIndex += 2;
            }

            // LW pairs (need 0xA1 commits)
            foreach (var (hidA, hidB) in lwPairs)
                EmitPair(hidA, hidB, Driver.PacketsGen2.USER_KEY_ENTRY_TYPE_LW, "LW", recordForCommit: true);

            // RDT pairs (no 0xA1 commits — per usb7.pcapng)
            foreach (var (hidA, hidB) in rdtPairs)
                EmitPair(hidA, hidB, Driver.PacketsGen2.USER_KEY_ENTRY_TYPE_RDT, "RDT", recordForCommit: false);

            // Per-key remap. `PerSlotHidUsage[k]` is the target HID for
            // our visual KeyIndex k. Resolve through the slot map and
            // write a single-slot 0x09 entry with type=0x10 + new HID.
            var perSlotRemap = item.RemapProfile?.PerSlotHidUsage ?? [];
            int remapsApplied = 0;
            for (int ki = 0; ki < perSlotRemap.Length && ki < defaultHidMap.Length; ki++)
            {
                byte targetHid = perSlotRemap[ki];
                if (targetHid == 0) continue;
                byte originalHid = defaultHidMap[ki];
                if (originalHid == 0) continue;
                int fwSlot = slotMap.TryGetSlotForHid(originalHid);
                if (fwSlot < 0 || fwSlot >= Driver.PacketsGen2.KEY_MATRIX_SLOT_COUNT) continue;
                // Skip if this slot is already armed for LW/RDT — those
                // take precedence; firmware ignores remap on paired slots.
                if (newArmedSlots.Contains(fwSlot)) continue;
                var entry = Driver.PacketsGen2.BuildWriteUserKeyEntry(fwSlot, Driver.PacketsGen2.USER_KEY_ENTRY_TYPE_STANDARD, 0x00, targetHid);
                if (stream.WritePacketNoAck(entry)) userKeyOk++; else userKeyFail++;
                Thread.Sleep(5);
                newArmedSlots.Add(fwSlot);
                remapsApplied++;
                DebugLogger.LogVerbose($"  OEM gen-2 sync #{mySeq}: remap KeyIndex {ki} (orig HID 0x{originalHid:x2}) → HID 0x{targetHid:x2} at firmware slot {fwSlot}");
            }
            if (remapsApplied > 0)
                DebugLogger.Log($"  OEM gen-2 sync #{mySeq}: per-key remap — {remapsApplied} key(s) remapped via single-slot 0x09 writes");

            // ── Step 2: restore defaults at stale slots ────────────────
            // Slots armed in the previous sync but not in this one get
            // their factory default entry written back. Without this,
            // unpaired slots stay marked SOCD/RDT and the firmware locks
            // those keys (beta.37 bug).
            HashSet<int> staleSlots;
            lock (_gen2ArmedSlotsLock)
            {
                staleSlots = new HashSet<int>(_gen2ArmedSlots);
                staleSlots.ExceptWith(newArmedSlots);
            }
            int restoredCount = 0;
            foreach (int slot in staleSlots)
            {
                if (slot < 0 || slot >= Driver.PacketsGen2.KEY_MATRIX_SLOT_COUNT) continue;
                var (type, code1, code2) = slotMap.RawAtSlot(slot);
                // Write back EXACTLY what the firmware had at this slot
                // in the default keymap. Using the raw 3-byte tuple
                // means we preserve any firmware-special entries (media
                // keys, modifier flags) without our app needing to
                // understand them.
                var restore = Driver.PacketsGen2.BuildWriteUserKeyEntry(slot, type, code1, code2);
                if (stream.WritePacketNoAck(restore)) userKeyOk++; else userKeyFail++;
                Thread.Sleep(5);
                restoredCount++;
                DebugLogger.LogVerbose($"  OEM gen-2 sync #{mySeq}: restored slot {slot} to default [type=0x{type:x2} code1=0x{code1:x2} code2=0x{code2:x2}]");
            }
            if (restoredCount > 0)
                DebugLogger.Log($"  OEM gen-2 sync #{mySeq}: restored {restoredCount} stale slot(s) to default user-keymap entry");

            // Commit the new tracking set.
            lock (_gen2ArmedSlotsLock)
            {
                _gen2ArmedSlots.Clear();
                foreach (var s in newArmedSlots) _gen2ArmedSlots.Add(s);
            }

            // ── Step 3: per-pair 0xA1 LW-armed KeyTrigger commits ──────
            // LW pairs only — RDT doesn't get these per usb7.pcapng.
            foreach (var (slotA, slotB, hidA, hidB) in lwArmedSlots)
            {
                var act1 = BuildLwArmedKeyTriggerWrite(slotA, hidA, activeProfileIndex, hidToKeyIndex, profile);
                var act2 = BuildLwArmedKeyTriggerWrite(slotB, hidB, activeProfileIndex, hidToKeyIndex, profile);
                DebugLogger.LogVerbose($"  OEM gen-2 sync #{mySeq}: LW pair HID 0x{hidA:x2}↔0x{hidB:x2} — 0xA1 LW-armed commits at slot {slotA} / slot {slotB}");
                if (stream.WritePacketNoAck(act1)) activateOk++; else activateFail++;
                Thread.Sleep(5);
                if (stream.WritePacketNoAck(act2)) activateOk++; else activateFail++;
                Thread.Sleep(5);
            }
        }
        else if (allPairs.Count > 0)
        {
            DebugLogger.Log($"  OEM gen-2 sync #{mySeq}: {allPairs.Count} pair(s) configured but slot map missing — SKIPPING user-keymap writes to avoid corrupting state. Pair table cleared above.");
        }
        int lwTotal = lwChunks.Count + userKeyOk + userKeyFail + activateOk + activateFail;
        DebugLogger.Log($"  OEM gen-2 sync #{mySeq}: pair feature writes complete — pair table {lwOk}/{lwChunks.Count}, user-keymap 0x09 {userKeyOk}/{userKeyOk + userKeyFail}, 0xA1 LW commits {activateOk}/{activateOk + activateFail}, skipped {lwSkippedNoSlot} pair(s) (slot lookup failed)");

        // ── Speculative gen-1 mode-toggle packets ────────────────────────
        // These use the gen-1 opcodes (0xB5 / 0xFC / 0xFD). The OEM firmware
        // either honours them (same as gen-1 firmware does) or silently
        // ignores them. Either way, the channel layer doesn't throw and we
        // log every send so the next user capture can confirm reception.
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

        // KeyTrigger region write was skipped above (see beta.41 diagnostic block)
        // so its contribution to total + sent + failed is zero.
        int total = (funcBlockOk + funcBlockFail) + lwTotal + modePackets.Length;
        int totalFailed = funcBlockFail + lwFail + userKeyFail + activateFail + modeFail;
        DebugLogger.Log($"  OEM gen-2 sync #{mySeq}: DONE total={total} ok={funcBlockOk + lwOk + userKeyOk + activateOk + modeOk} failed={totalFailed} (KeyTrigger region skipped — beta.41 diagnostic)");
        return (totalFailed == 0, false, total);
    }

    // Maps a (type, code1, code2) slot entry to a human-readable label
    // so the slot-map dump in the debug log is scannable at a glance.
    // type 0x10 → standard HID usage code; we decode the common ones
    // (alphanumeric, F-row, arrows, modifiers, nav cluster, numpad).
    // Other types (0xF0 media, 0xFF empty, etc.) get a generic label.
    private static string DescribeKeyMatrixEntry(byte type, byte code1, byte code2)
    {
        if (type == 0x10)
        {
            // USB HID Usage IDs for Keyboard/Keypad page (0x07).
            string name = code2 switch
            {
                >= 0x04 and <= 0x1D => "ABCDEFGHIJKLMNOPQRSTUVWXYZ"[code2 - 0x04].ToString(),
                >= 0x1E and <= 0x26 => ((char)('1' + (code2 - 0x1E))).ToString(),
                0x27 => "0",
                0x28 => "Enter",
                0x29 => "Esc",
                0x2A => "Bksp",
                0x2B => "Tab",
                0x2C => "Space",
                0x2D => "-",
                0x2E => "=",
                0x2F => "[",
                0x30 => "]",
                0x31 => "\\",
                0x33 => ";",
                0x34 => "'",
                0x35 => "`",
                0x36 => ",",
                0x37 => ".",
                0x38 => "/",
                0x39 => "Caps",
                >= 0x3A and <= 0x45 => "F" + (code2 - 0x39).ToString(),
                0x46 => "PrintScreen",
                0x47 => "ScrollLock",
                0x48 => "Pause",
                0x49 => "Ins",
                0x4A => "Home",
                0x4B => "PgUp",
                0x4C => "Del",
                0x4D => "End",
                0x4E => "PgDn",
                0x4F => "Right",
                0x50 => "Left",
                0x51 => "Down",
                0x52 => "Up",
                0x53 => "NumLock",
                0x54 => "KP/",
                0x55 => "KP*",
                0x56 => "KP-",
                0x57 => "KP+",
                0x58 => "KPEnter",
                >= 0x59 and <= 0x61 => "KP" + ((code2 - 0x59 + 1).ToString()),
                0x62 => "KP0",
                0x63 => "KP.",
                0x65 => "App",
                0xE0 => "LCtrl",
                0xE1 => "LShift",
                0xE2 => "LAlt",
                0xE3 => "LWin",
                0xE4 => "RCtrl",
                0xE5 => "RShift",
                0xE6 => "RAlt",
                0xE7 => "RWin",
                _ => $"HID 0x{code2:x2}",
            };
            return $"[{name}]";
        }
        return type switch
        {
            0xF0 => $"[media code1=0x{code1:x2} code2=0x{code2:x2}]",
            0xFF => "[empty]",
            _ => $"[type 0x{type:x2}]",
        };
    }

    // Probe: read current per-key trigger entries at the slot positions
    // we care about. Decodes AP (the value the user would see in the
    // slider) for each, so we can compare across captured-vs-our slots.
    // No writes — pure diagnostic. Fails open: any read error skips
    // that slot rather than aborting the sync.
    private static void TryProbeKeyTriggerEntries(HidStream stream, int mySeq, byte profileIndex, Driver.KeyMatrixSlotMap? slotMap)
    {
        // Probe both the captured slots (A=37,D=39,S=38,W=26) and our
        // beta.27 KeyIndex positions (A=64,D=66,S=65,W=44). Also probe
        // whatever the live slot map says, if it differs.
        var probes = new List<(string name, int slot)>
        {
            ("A@captured(37)", 37),
            ("D@captured(39)", 39),
            ("S@captured(38)", 38),
            ("W@captured(26)", 26),
            ("A@ourKeyIndex(64)", 64),
            ("D@ourKeyIndex(66)", 66),
            ("S@ourKeyIndex(65)", 65),
            ("W@ourKeyIndex(44)", 44),
        };
        if (slotMap is not null)
        {
            foreach (var (name, hid) in new[] { ("A", (byte)0x04), ("D", (byte)0x07), ("S", (byte)0x16), ("W", (byte)0x1A) })
            {
                int liveSlot = slotMap.TryGetSlotForHid(hid);
                if (liveSlot >= 0 && !probes.Any(p => p.slot == liveSlot))
                    probes.Add(($"{name}@liveSlotMap({liveSlot})", liveSlot));
            }
        }

        foreach (var (name, slot) in probes)
        {
            try
            {
                int addr = profileIndex * Driver.PacketsGen2.KEY_TRIGGER_REGION_SIZE + slot * Driver.PacketsGen2.KEY_TRIGGER_RECORD_SIZE;
                if (addr < 0 || addr > 0xFFFF) continue;
                var req = Driver.PacketsGen2.BuildReadRequest(Driver.PacketsGen2.SUB_READ_KEY_TRIGGER, (byte)Driver.PacketsGen2.KEY_TRIGGER_RECORD_SIZE, (ushort)addr);
                var resp = stream.WritePacket(req);
                int need = Driver.PacketsGen2.RESPONSE_DATA_OFFSET + Driver.PacketsGen2.KEY_TRIGGER_RECORD_SIZE;
                if (resp is null || resp.Length < need || !Driver.PacketsGen2.IsExtendedGatewayResponse(resp, Driver.PacketsGen2.SUB_READ_KEY_TRIGGER))
                {
                    DebugLogger.Log($"  OEM gen-2 sync #{mySeq}: probe {name} (slot {slot}, addr 0x{addr:x4}) — READ FAILED");
                    continue;
                }
                // Decode the 8-byte entry
                int o = Driver.PacketsGen2.RESPONSE_DATA_OFFSET;
                byte b0 = resp[o], b1 = resp[o + 1], b2 = resp[o + 2], b3 = resp[o + 3];
                byte b4 = resp[o + 4], b5 = resp[o + 5], b6 = resp[o + 6], b7 = resp[o + 7];
                int actRaw = b2 | ((b3 & 0x01) << 8);
                int rtpRaw = b4 | ((b5 & 0x01) << 8);
                int rtrRaw = b6 | ((b7 & 0x01) << 8);
                int actDisp = actRaw + 1; // display value in 0.01 mm
                int rtpDisp = rtpRaw + 1;
                int rtrDisp = rtrRaw + 1;
                int keyMode = b1 & 0x0F;
                int priority = (b1 >> 4) & 0x0F;
                int pressDz = (b5 >> 1) & 0x7F;
                int releaseDz = (b7 >> 1) & 0x7F;
                DebugLogger.Log($"  OEM gen-2 sync #{mySeq}: probe {name} (slot {slot}, addr 0x{addr:x4}) → bytes={b0:x2} {b1:x2} {b2:x2} {b3:x2} {b4:x2} {b5:x2} {b6:x2} {b7:x2}  AP={actDisp / 100.0:F2}mm  rtPress={rtpDisp / 100.0:F2}mm  rtRelease={rtrDisp / 100.0:F2}mm  key_mode={keyMode}  pri={priority}  press_dz={pressDz}  release_dz={releaseDz}");
                Thread.Sleep(2);
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"  OEM gen-2 sync #{mySeq}: probe {name} (slot {slot}) EXCEPTION {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    // Builds the 0x55 0xA1 LW-commit packet for one main key. Preserves
    // the user's per-key AP from Profile.Keys_Array (looked up via the
    // hidToKeyIndex reverse map) and applies the OEM driver's "LW-armed"
    // overrides on the other fields:
    //   byte 0 = 0xA0  (switch_type=0, high nibble 0xA)
    //   byte 1 = 0x01  (key_mode=1, priority=0)
    //   bytes 2..3     user AP encoded LE9 (stored = displayValue − 1)
    //   bytes 4..5 = 0x09 0x00  (rt_press_raw=9 → 0.10 mm; press_dz_raw=0)
    //   bytes 6..7 = 0x09 0x1C  (rt_release_raw=9 → 0.10 mm; release_dz_raw=14)
    //
    // These constants come from tester B's usb5/usb6 captures of the
    // official driver activating LW. The rt_press/rt_release/dz overrides
    // arm the key for the firmware's SOCD transition machinery without
    // disturbing the user's actuation point.
    private static byte[] BuildLwArmedKeyTriggerWrite(int firmwareSlot, byte hid, byte profileIndex,
        Dictionary<byte, int> hidToKeyIndex, Driver.Profile profile)
    {
        // Look up user's current AP for this HID. Falls back to 200 (=2.00mm)
        // — the firmware default — if the key isn't in our visual layout.
        int actuation = 200;
        if (hidToKeyIndex.TryGetValue(hid, out int keyIndex) && keyIndex >= 0 && keyIndex < profile.Keys_Array.Length)
        {
            int ap = (int)Math.Round(profile.Keys_Array[keyIndex].Action_Point * 100m);
            if (ap > 0) actuation = ap;
        }
        if (actuation > 512) actuation = 512;
        int actStored = actuation - 1;

        Span<byte> entry = stackalloc byte[Driver.KeyTriggerEntry.BYTE_SIZE];
        entry[0] = 0xA0;
        entry[1] = 0x01; // LW-armed key_mode
        entry[2] = (byte)(actStored & 0xFF);
        entry[3] = (byte)((actStored >> 8) & 0x01);
        entry[4] = 0x09;
        entry[5] = 0x00;
        entry[6] = 0x09;
        entry[7] = 0x1C;

        return Driver.PacketsGen2.BuildWriteKeyTriggerSingleSlot(firmwareSlot, profileIndex, entry);
    }

    // Reads the firmware's default key matrix via 10 chunked 0x55 0x07
    // requests (profile=0, layer=0) and returns the parsed slot map.
    // Logs and returns null on any chunk error so the caller can fall
    // back gracefully.
    private static Driver.KeyMatrixSlotMap? TryReadGen2SlotMap(HidStream stream, int mySeq)
    {
        try
        {
            var matrix = new byte[Driver.PacketsGen2.KEY_MATRIX_REGION_SIZE];
            int offset = 0;
            int chunkIndex = 0;
            foreach (var request in Driver.PacketsGen2.BuildReadDefaultKeyMatrixSequence(profileIndex: 0, layer: 0))
            {
                chunkIndex++;
                byte expectedLength = request[4];
                var response = stream.WritePacket(request);
                int needed = Driver.PacketsGen2.RESPONSE_DATA_OFFSET + expectedLength;
                if (response is null || response.Length < needed
                    || !Driver.PacketsGen2.IsExtendedGatewayResponse(response, Driver.PacketsGen2.SUB_READ_DEFAULT_KEY_MATRIX))
                {
                    DebugLogger.Log($"  OEM gen-2 sync #{mySeq}: slot-map read chunk {chunkIndex} FAILED (response={(response is null ? "null" : $"{response.Length} bytes")}); falling back to direct KeyIndex");
                    return null;
                }
                Array.Copy(response, Driver.PacketsGen2.RESPONSE_DATA_OFFSET, matrix, offset, expectedLength);
                offset += expectedLength;
                Thread.Sleep(2);
            }
            var slotMap = Driver.KeyMatrixSlotMap.BuildFromMatrix(matrix);
            DebugLogger.Log($"  OEM gen-2 sync #{mySeq}: slot map loaded — {slotMap.StandardKeyCount} standard HID keys across {slotMap.SlotCount} firmware slots");
            // Sample a few well-known keys for the log so we can sanity-check
            // the mapping. A=0x04, S=0x16, D=0x07, W=0x1A.
            int slotA = slotMap.TryGetSlotForHid(0x04);
            int slotS = slotMap.TryGetSlotForHid(0x16);
            int slotD = slotMap.TryGetSlotForHid(0x07);
            int slotW = slotMap.TryGetSlotForHid(0x1A);
            DebugLogger.Log($"  OEM gen-2 sync #{mySeq}: slot map samples — A=slot{slotA} S=slot{slotS} D=slot{slotD} W=slot{slotW} (expected on A75 Pro OEM: 37/38/39/26)");
            return slotMap;
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"  OEM gen-2 sync #{mySeq}: slot-map read EXCEPTION {ex.GetType().Name}: {ex.Message} — falling back to direct KeyIndex");
            return null;
        }
    }

    // Called by KeyboardManager (or App) when the gen-2 keyboard
    // disconnects, so the next connect re-reads its slot map and
    // resets the armed-slots tracking. The slot map is a fingerprint of
    // the *connected* keyboard's firmware — reusing a stale map after a
    // swap to a different model would silently corrupt writes again.
    // The armed-slots set tracks which firmware slots we wrote
    // non-default user-keymap entries to last sync — resetting it on
    // disconnect avoids re-clearing slots on a different keyboard.
    public void InvalidateGen2SlotMap()
    {
        lock (_gen2SlotMapLock) { _gen2SlotMap = null; }
        lock (_gen2ArmedSlotsLock) { _gen2ArmedSlots.Clear(); }
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
