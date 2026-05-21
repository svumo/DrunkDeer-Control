using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Driver;
using HidSharp;
using Microsoft.Extensions.DependencyInjection;
using WpfApp.Profile;
using WpfApp.ViewModels;
using Orientation = System.Windows.Controls.Orientation;
using StackPanel = System.Windows.Controls.StackPanel;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Key = System.Windows.Input.Key;
using KeyboardKey = System.Windows.Input.Keyboard;
using Mouse = System.Windows.Input.Mouse;
using CaptureMode = System.Windows.Input.CaptureMode;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using ModifierKeys = System.Windows.Input.ModifierKeys;
// 'Profile' as an unqualified name collides with the WpfApp.Profile namespace.
using DriverProfile = Driver.Profile;

namespace WpfApp.Components.KeyboardView;

// The interactive Performance view — the right side of MainWindow. Started
// life as a standalone --keyboard-debug Window; promoted to a UserControl so
// it could be embedded in the main shell next to the profile sidebar. Still
// reachable as a standalone Window via --keyboard-debug (App.xaml.cs wraps
// this control in a host Window for that flag).
//   Phase A: static render of A75 Pro layout.
//   Phase B: single-key click selection + live ActuationDrawer.
//   Phase C: Sync to Keyboard pushes per-key AP/DS/US to firmware.
//   Phase D: multi-select via KeyboardCanvasViewModel.
//     - Click: replace selection
//     - Ctrl+click: toggle
//     - Shift+click: range from anchor (row-major)
//     - Ctrl+A: select all
//     - Esc / background-click: clear
//     Drawer adapts to single vs multi (showing "N keys selected" and
//     "Mixed" markers when values differ across the selection). Drawer
//     edits apply uniformly to every selected cap.
public partial class KeyboardPerformanceView : System.Windows.Controls.UserControl
{
    private const double UnitWidth = 44.0;
    private const double KeyGap = 4.0;
    private const double NavGutter = 12.0;

    // Mock initial actuation values so the debug session opens with visible
    // heat states. Live edits via the drawer overwrite these in the per-cap
    // properties — not persisted anywhere.
    private static readonly Dictionary<string, (double Ap, double Ds, double Us)> Demo = new()
    {
        ["w"]     = (0.5, 0.0, 0.0),
        ["a"]     = (0.5, 0.0, 0.0),
        ["s"]     = (0.5, 0.0, 0.0),
        ["d"]     = (0.5, 0.0, 0.0),
        ["e"]     = (0.2, 0.0, 0.0),
        ["space"] = (1.5, 0.0, 0.0),
        ["b"]     = (3.3, 0.0, 0.0),
        ["f"]     = (0.5, 0.5, 0.5),
    };

    private readonly Dictionary<string, KeyCap> _caps = new();
    private readonly KeyboardCanvasViewModel _vm = new();
    private readonly KeyboardManager? _keyboardManager;
    // Bound by Attach(...). Null when the view is hosted in standalone-debug
    // mode (no profile system available) — every persistence-related call
    // is null-guarded so the debug window still works for visual checks.
    private ProfileManager? _profileManager;
    // Set to true while LoadFromActiveProfile is overwriting _caps /
    // _modeSettings / _remaps / _lwPairs from a freshly-selected profile.
    // Write-back is skipped during this window so we don't immediately echo
    // the just-loaded values back into the (same) profile and mark it dirty.
    private bool _loadingFromProfile;
    private bool _syncing;

    // Whichever DrunkDeer model we resolved at launch — drives the visual
    // layout and the slot map used for Sync. Falls back to A75 Pro when no
    // keyboard is connected or its model is unrecognized.
    private readonly KeyboardModel? _activeModel;
    private readonly IReadOnlyList<IReadOnlyList<LayoutKey>> _activeLayout;
    private readonly IReadOnlyList<LayoutKey> _activeLayoutFlat;

    // Global mode toggles. Wired from ModeStrip — every flip mutates this in
    // place and Sync rebuilds the common-switch packet from it. Defaults to
    // all-off, matching the existing app's effective pre-ModeStrip behavior.
    private readonly ProfileSettings _modeSettings = new();

    // ---- Drag-marquee state (Phase E) -------------------------------------
    private enum MarqueeMode { Replace, Add, Subtract }
    private bool _isDragging;
    private Point _dragStart;
    private MarqueeMode _dragMode;
    // Per-drag cache of cap rects in CanvasBorder coordinates. TransformToVisual
    // + TransformBounds is a non-trivial visual-tree walk; running it 91 times
    // per mouse-move tick is wasteful when cap positions are stable for the
    // life of the drag. Built on MouseDown, consumed on every Move + Up,
    // cleared on EndDrag / LostMouseCapture.
    private (KeyCap Cap, string Code, Rect Rect)[]? _dragRects;

    // True while ApplyDrawerEdit is running so SyncDrawerFromSelection
    // doesn't re-clobber the drawer with stale state during edits.
    private bool _suppressDrawerSync;

    // Debounced auto-sync. Any state mutation calls ScheduleAutoSync(),
    // which resets a 400ms timer. When the timer fires the same sync as
    // the manual button runs. Tuned so a slider drag flushes shortly after
    // the user stops moving without spamming the firmware mid-drag.
    private readonly System.Windows.Threading.DispatcherTimer _autoSyncTimer =
        new() { Interval = TimeSpan.FromMilliseconds(400) };

    // Phase H — per-slot HID usage code overrides. _remaps[slotIndex] == 0
    // means "leave at factory default". Non-zero values are HID usage codes
    // and get emitted on Sync via Packets.BuildRemapPackets.
    private readonly byte[] _remaps = new byte[126];

    // Last Win pairs (user-defined). Stored as unordered pairs `(A, B)` where
    // A < B, persisted in Settings.LastWinPairsJson. The wire format expects
    // both directions, so DoSyncAsync emits (A→B) and (B→A) for each.
    private readonly List<(byte A, byte B)> _lwPairs = new();
    private enum LwPickState { None, AwaitFirst, AwaitSecond }
    private LwPickState _lwPickState = LwPickState.None;
    private byte? _lwFirstPick;

    // Release Dual-Trigger pairs (user-defined). ORDERED — first slot emits
    // its HID code on press, second slot emits on release. Persisted in
    // Settings.ReleaseDualTriggerPairsJson. Unlike LW these are NOT
    // canonicalised via Math.Min/Max — (E,T) and (T,E) are different pairs.
    private readonly List<(byte Press, byte Release)> _rdtPairs = new();
    private enum RdtPickState { None, AwaitFirst, AwaitSecond }
    private RdtPickState _rdtPickState = RdtPickState.None;
    private byte? _rdtFirstPick;

    // Pre-pair per-key snapshot. Captured the FIRST time a slot becomes a
    // press/release in any pair (we don't overwrite if a later pair uses the
    // same slot). Used to revert AP/DS/US when:
    //   • The pair is removed (chip × button) — restores both slots.
    //   • RDT toggle goes OFF with pairs still in the list — restores every
    //     slot referenced by any current pair, then clears the snapshot.
    // Without this, AP=0.5mm (the gaming default applied on pair commit)
    // and US=1.5mm (firmware requirement for release slot) persist on the
    // physical key even when the pair is no longer active, which makes the
    // affected keys feel unusually shallow in normal typing.
    // In-session only — snapshots aren't persisted across app restarts.
    private readonly Dictionary<byte, (double Ap, double Ds, double Us)> _rdtPreState = new();

    // Keystroke-tracking live-depth pipeline. Long-lived HID stream that
    // sits outside the short-lived `using var stream = keyboard.Open()`
    // used by DoSyncAsync. We don't share/coordinate with that stream — on
    // DrunkDeer's vendor HID interface each Open() returns an independent
    // file handle, and the firmware tolerates concurrent reads/writes
    // because tracking input reports arrive unsolicited (no transaction
    // pairing). If a future firmware changes this, the simplest fix is to
    // teardown the listener in DoSyncAsync's prelude and restart it after.
    // For now: open once on toggle-on, dispose on toggle-off, hands-off.
    private HidStream? _trackingStream;
    private HidStreamListener? _trackingListener;
    private KeyDepthBroker? _trackingBroker;
    // Reverse-lookup: slot index → KeyCap. Built lazily the first time we
    // turn tracking on; invalidated implicitly when caps change (only at
    // construction time, so "lazily, once" is fine).
    private KeyCap?[]? _capBySlot;

    public KeyboardPerformanceView() : this(null) { }

    public KeyboardPerformanceView(KeyboardManager? keyboardManager)
    {
        _keyboardManager = keyboardManager;
        _activeModel = KeyboardLayoutResolver.Resolve(keyboardManager?.KeyboardWithSpecs);
        _activeLayout = KeyboardLayout.VisualFor(_activeModel) ?? KeyboardLayout.A75Pro;
        _activeLayoutFlat = KeyboardLayout.VisualFlatFor(_activeModel) ?? KeyboardLayout.A75ProFlat;

        // Seed _modeSettings from the keyboard's spec response so the ModeStrip
        // reflects the firmware's actual current state on connect, not just
        // defaults. Phase G+ — see Driver/KeyboardSpecs.cs for what gets parsed.
        if (keyboardManager?.KeyboardWithSpecs is { } kb)
        {
            _modeSettings.TurboEnabled              = kb.Specs.TurboValue == 1;
            _modeSettings.RapidTriggerEnabled       = kb.Specs.RapidTrigger == 1;
            _modeSettings.ReleaseDualTriggerEnabled = kb.Specs.RapidTriggerPlus == 1; // byte[18] = rtdvalue
            _modeSettings.LastWinEnabled            = kb.Specs.LastWinValue == 1;
            _modeSettings.RTMatchEnabled            = kb.Specs.RTMatch == true;
            _modeSettings.LastWinReplaceEnabled     = kb.Specs.LastWinReplace == true;
            _modeSettings.AutoMatchMode             = kb.Specs.AutoMatchMode ?? 255;
            // Keystroke Tracking is UI-only state — the keyboard doesn't
            // report it back, so it stays at the ProfileSettings default.
        }

        InitializeComponent();
        RefreshHeaderAndBanner();

        // The header + banner are derived from the connected keyboard's
        // identity, so they have to refresh whenever the connection changes
        // (USB unplug, model swap). Subscribe before the keyboard can change
        // out from under us and unsubscribe on Unloaded so a recycled view
        // doesn't double-fire.
        if (keyboardManager is not null)
        {
            keyboardManager.ConnectedKeyboardChanged += OnConnectedKeyboardChanged;
            Unloaded += (_, _) => keyboardManager.ConnectedKeyboardChanged -= OnConnectedKeyboardChanged;
        }

        // Push seeded settings into the ModeStrip BEFORE wiring its events so
        // the initial sync doesn't echo right back into _modeSettings.
        ModeStrip.RapidTriggerEnabled       = _modeSettings.RapidTriggerEnabled;
        ModeStrip.ReleaseDualTriggerEnabled = _modeSettings.ReleaseDualTriggerEnabled;
        ModeStrip.LastWinEnabled            = _modeSettings.LastWinEnabled;
        ModeStrip.TurboEnabled              = _modeSettings.TurboEnabled;
        ModeStrip.KeystrokeTrackingEnabled  = _modeSettings.KeystrokeTrackingEnabled;

        BuildRows();
        Drawer.ActuationChanged += OnDrawerActuationChanged;
        Drawer.ClearSelection();
        PreviewKeyDown += OnPreviewKeyDown;
        _vm.SelectedKeys.CollectionChanged += OnSelectionChanged;
        ModeStrip.PrimaryToggleChanged += OnModeStripToggle;
        ModeStrip.SecondaryToggleChanged += OnModeStripToggle;
        ModeStrip.ResetAllClicked += OnResetAllKeys;
        ModeStrip.ResetAllRemapsClicked += OnResetAllRemaps;
        QuickSelectBar.PillClicked += OnQuickSelectPill;
        PresetBar.PresetClicked += OnPresetClicked;
        _autoSyncTimer.Tick += OnAutoSyncTick;
        Drawer.RebindRequested += OnDrawerRebindRequested;
        Drawer.RestoreRequested += OnDrawerRestoreRequested;
        Drawer.CaptureStarted += (_, _) => RemapCaptureStarted?.Invoke(this, EventArgs.Empty);
        Drawer.CaptureEnded += (_, _) => RemapCaptureEnded?.Invoke(this, EventArgs.Empty);
        LwPairsBar.AddRequested += OnLwAddPairRequested;
        LwPairsBar.RemoveRequested += OnLwRemovePairRequested;
        RdtPairsBar.AddRequested += OnRdtAddPairRequested;
        RdtPairsBar.RemoveRequested += OnRdtRemovePairRequested;
        RdtPairsBar.EditRequested += OnRdtEditPairRequested;
        RdtDrawer.ApValueChanged += OnRdtDrawerApChanged;
        RdtDrawer.DsValueChanged += OnRdtDrawerDsChanged;
        RdtDrawer.UsValueChanged += OnRdtDrawerUsChanged;
        RdtDrawer.CloseRequested += (_, _) => HideRdtDrawer();
        // UserControl doesn't have OnClosed; Unloaded handles teardown.
        Unloaded += (_, _) => StopKeystrokeTracking();
        LoadLwPairsFromSettings();
        LoadRdtPairsFromSettings();
        RefreshLwPairsBar();
        RefreshRdtPairsBar();
        UpdateLwPairsBarVisibility();
        UpdateRdtPairsBarVisibility();
        UpdateStatusText();
        UpdatePresetCounts();

        // Fire-and-forget firmware-version check. Worker source lives at
        // telemetry-worker/ in this repo. Runs ≤ once per 24h (Settings.
        // LastFirmwareCheck gates it). Surfaces an in-app banner if the
        // connected keyboard's firmware lags what DrunkDeer publishes.
        // Errors are swallowed inside FirmwareUpdateChecker — a failed
        // fetch just means no banner.
        var settings = TryResolveSettings();
        if (settings is not null)
        {
            var specs = keyboardManager?.KeyboardWithSpecs?.Specs;
            int? pid = keyboardManager?.KeyboardWithSpecs?.Keyboard.ProductID;
            _ = FirmwareUpdateChecker.CheckIfDueAsync(settings, specs, pid)
                .ContinueWith(t =>
                {
                    if (t.IsFaulted || t.Result is null) return;
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (t.Result is { UpdateAvailable: true } r)
                            ShowFirmwareBanner(r);
                    }));
                }, TaskScheduler.Default);
        }
    }

    // ---- Profile integration (the source-of-truth bridge) -----------------
    //
    // The view is constructed without a ProfileManager so it stays usable in
    // --keyboard-debug standalone mode. The main window calls Attach() right
    // after instantiation; from that point on the view treats the active
    // ProfileItem as the source of truth:
    //   • Every edit handler calls ScheduleAutoSync() which calls
    //     WriteBackToActiveProfile() — the in-memory ProfileItem is updated
    //     synchronously, then ScheduleSave debounces the disk write.
    //   • ProfileManager.CurrentProfileChanged invokes LoadFromActiveProfile,
    //     which rehydrates _caps / _modeSettings / _remaps / _lwPairs from
    //     the new profile and refreshes the ModeStrip + LW bar.
    //   • DoSyncAsync delegates to ProfileManager.PushCurrentProfileAsync so
    //     manual Sync and auto-sync ticks write the EXACT same byte stream
    //     as a profile-switch push (Packets.BuildFullProfilePackets).
    public void Attach(ProfileManager profileManager)
    {
        _profileManager = profileManager;
        profileManager.CurrentProfileChanged += OnProfileManagerProfileChanged;
        Unloaded += (_, _) => profileManager.CurrentProfileChanged -= OnProfileManagerProfileChanged;

        // Rehydrate immediately if a profile is already active (DiscoverProfiles
        // ran in OnSourceInitialized, before Window_Loaded constructs the view).
        if (profileManager.CurrentIndex >= 0 && profileManager.CurrentIndex < profileManager.Profiles.Count)
        {
            LoadFromActiveProfile(profileManager.Profiles[profileManager.CurrentIndex]);

            // Startup-sync bridge. ProfileManager's CurrentProfileChanged
            // event fires during DiscoverProfiles (before the view exists),
            // which triggers a push of whatever profile ProfileManager
            // initially selected. By the time Attach runs here, the active
            // profile may have changed (e.g. when a profile has IsDefault=true
            // and overrides the LastProfileUsedName fallback) — so the
            // keyboard ends up with one profile's state while the view shows
            // another. Trigger a sync of the *currently active* profile to
            // bridge that gap. ScheduleAutoSync's 400ms debounce coalesces
            // this with any normal-flow push that fires moments later.
            if (_keyboardManager?.KeyboardWithSpecs is not null)
            {
                ScheduleAutoSync();
            }
        }
    }

    private void OnProfileManagerProfileChanged(int index, ProfileItem item)
    {
        // CurrentProfileChanged may fire on a background thread (hotkey path).
        // Marshal to the UI thread before touching DPs.
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => LoadFromActiveProfile(item)));
            return;
        }
        LoadFromActiveProfile(item);
    }

    private void LoadFromActiveProfile(ProfileItem item)
    {
        _loadingFromProfile = true;
        try
        {
            // (1) Per-key AP/DS/US. Imported profiles always have a 126-slot
            // Keys_Array; legacy profiles missing entries fall back to AP=2.0
            // / DS=0 / US=0 (the firmware's factory default).
            var keys = item.Profile.Keys_Array;
            foreach (var lk in _activeLayoutFlat)
            {
                if (!_caps.TryGetValue(lk.Code, out var cap)) continue;
                if (lk.KeyIndex < 0 || lk.KeyIndex >= 126) continue;
                if (lk.KeyIndex < keys.Length && keys[lk.KeyIndex] is { } ks)
                {
                    cap.ActuationPoint = (double)ks.Action_Point;
                    cap.Downstroke = (double)ks.Downstroke;
                    cap.Upstroke = (double)ks.Upstroke;
                }
                else
                {
                    cap.ActuationPoint = 2.0;
                    cap.Downstroke = 0.0;
                    cap.Upstroke = 0.0;
                }
            }

            // (2) Mode settings — copy the profile's stored toggles into our
            // working struct and the ModeStrip (silent — see ModeStrip's
            // SetToggleSilently). Keystroke tracking is special: enabling it
            // requires opening the long-lived HID stream, so we apply the
            // state via Start/Stop rather than just flipping the bit.
            var s = item.Profile.Settings;
            _modeSettings.RapidTriggerEnabled       = s.RapidTriggerEnabled;
            _modeSettings.TurboEnabled              = s.TurboEnabled;
            _modeSettings.LastWinEnabled            = s.LastWinEnabled;
            _modeSettings.ReleaseDualTriggerEnabled = s.ReleaseDualTriggerEnabled;
            _modeSettings.RTMatchEnabled            = s.RTMatchEnabled;
            _modeSettings.LastWinReplaceEnabled     = s.LastWinReplaceEnabled;
            _modeSettings.AutoMatchMode             = s.AutoMatchMode;

            // Migration: pre-fix builds allowed both LW and RDT to be enabled
            // simultaneously. The official driver enforces mutual exclusion
            // and the wire-format machinery is shared, so loading both bits
            // set is invalid. Force RDT off; LW was the only one actually
            // wired with pair-config UI before this commit, so keeping LW
            // preserves the user's working state.
            if (_modeSettings.LastWinEnabled && _modeSettings.ReleaseDualTriggerEnabled)
            {
                DebugLogger.Log("KeyboardPerformanceView: migration — both LW and RDT were enabled in profile; forcing RDT off (LW kept).");
                _modeSettings.ReleaseDualTriggerEnabled = false;
            }

            ModeStrip.RapidTriggerEnabled       = _modeSettings.RapidTriggerEnabled;
            ModeStrip.ReleaseDualTriggerEnabled = _modeSettings.ReleaseDualTriggerEnabled;
            ModeStrip.LastWinEnabled            = _modeSettings.LastWinEnabled;
            ModeStrip.TurboEnabled              = _modeSettings.TurboEnabled;

            // Keystroke tracking — apply only if it's actually changing.
            if (s.KeystrokeTrackingEnabled != _modeSettings.KeystrokeTrackingEnabled)
            {
                _modeSettings.KeystrokeTrackingEnabled = s.KeystrokeTrackingEnabled;
                ModeStrip.KeystrokeTrackingEnabled = s.KeystrokeTrackingEnabled;
                if (s.KeystrokeTrackingEnabled) StartKeystrokeTracking();
                else                            StopKeystrokeTracking();
            }

            // (3) Remap overrides — clear all caps' remap labels, then re-apply
            // from the profile. RemapProfile is null on profiles that have
            // never had a remap edit; treat as "all defaults".
            Array.Clear(_remaps);
            foreach (var cap in _caps.Values) cap.RemappedLabel = null;
            var perSlot = item.RemapProfile?.PerSlotHidUsage ?? [];
            if (perSlot.Length > 0)
            {
                for (int slot = 0; slot < Math.Min(perSlot.Length, 126); slot++)
                {
                    if (perSlot[slot] == 0) continue;
                    _remaps[slot] = perSlot[slot];
                    var lk = _activeLayoutFlat.FirstOrDefault(k => k.KeyIndex == slot);
                    if (lk is not null && _caps.TryGetValue(lk.Code, out var cap))
                        cap.RemappedLabel = RemapDrawer.HidUsageLabel(perSlot[slot]);
                }
            }

            // (4) LW pairs.
            _lwPairs.Clear();
            var pairs = item.RemapProfile?.LwPairs ?? [];
            foreach (var p in pairs)
            {
                if (p is not { Length: 2 }) continue;
                if (p[0] == p[1]) continue;
                var a = Math.Min(p[0], p[1]);
                var b = Math.Max(p[0], p[1]);
                if (!_lwPairs.Any(x => x.A == a && x.B == b))
                    _lwPairs.Add((a, b));
            }
            RefreshLwPairsBar();
            UpdateLwPairsBarVisibility();

            // (4b) RDT pairs. Preserve press/release order — first byte is
            // press-emit, second is release-emit; do NOT canonicalise.
            _rdtPairs.Clear();
            var rdtPairs = item.RemapProfile?.RdtPairs ?? [];
            foreach (var p in rdtPairs)
            {
                if (p is not { Length: 2 }) continue;
                if (p[0] == p[1]) continue;
                if (!_rdtPairs.Any(x => x.Press == p[0] && x.Release == p[1]))
                    _rdtPairs.Add((p[0], p[1]));
            }
            RefreshRdtPairsBar();
            UpdateRdtPairsBarVisibility();

            // (5) Reset transient UI state — selection, drawer, in-flight
            // LW/RDT pick. Stale selection from another profile is confusing.
            _vm.ClearSelection();
            CancelLwPick();
            CancelRdtPick();
            HideRdtDrawer();
            UpdateStatusText();
            UpdatePresetCounts();

            DebugLogger.Log($"KeyboardPerformanceView.LoadFromActiveProfile: profile='{item.Name}' caps={_caps.Count} remaps={perSlot.Count(b => b != 0)} lwPairs={_lwPairs.Count} rdtPairs={_rdtPairs.Count} keysHash={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(item.Profile.Keys_Array):X8} settingsHash={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(item.Profile.Settings):X8}");
        }
        finally
        {
            _loadingFromProfile = false;
        }
    }

    // Synchronously copies the view's working state into the active ProfileItem
    // and asks ProfileManager to debounce a disk save. Called from every edit
    // path via ScheduleAutoSync — by the time the 400ms auto-sync timer ticks
    // (or the user clicks Sync), the ProfileItem is already up-to-date, so
    // PushCurrentProfile reads what the user actually wants on the keyboard.
    private void WriteBackToActiveProfile()
    {
        if (_loadingFromProfile) return;
        if (_profileManager is null) return;
        if (_profileManager.CurrentIndex < 0 || _profileManager.CurrentIndex >= _profileManager.Profiles.Count) return;
        var item = _profileManager.Profiles[_profileManager.CurrentIndex];
        // Per-profile-leak diagnostic — keep this; cheap, and turns the
        // next "edits bleed across profiles" report into a one-glance log
        // read (matching keysHash between profiles = shared array).
        DebugLogger.Log($"WriteBack: profile='{item.Name}' (#{_profileManager.CurrentIndex}) keysHash={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(item.Profile.Keys_Array):X8} settingsHash={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(item.Profile.Settings):X8}");

        // (1) Per-key AP/DS/US. Always materialize 126 slots so BuildPacketKeyPoint
        // never indexes out of bounds. Mutate in place when possible so we
        // don't allocate a fresh KeySetting array on every slider tick.
        var keysArray = item.Profile.Keys_Array;
        if (keysArray.Length != 126)
        {
            var widened = new KeySetting[126];
            for (int i = 0; i < 126; i++)
                widened[i] = i < keysArray.Length && keysArray[i] is { } existing
                    ? existing
                    : new KeySetting { Action_Point = 2.0m };
            item.Profile.Keys_Array = widened;
            keysArray = widened;
        }
        foreach (var lk in _activeLayoutFlat)
        {
            if (lk.KeyIndex < 0 || lk.KeyIndex >= 126) continue;
            if (!_caps.TryGetValue(lk.Code, out var cap)) continue;
            keysArray[lk.KeyIndex] = new KeySetting
            {
                KeyName = lk.ProfileKeyName,
                Action_Point = (decimal)cap.ActuationPoint,
                Downstroke = (decimal)cap.Downstroke,
                Upstroke = (decimal)cap.Upstroke,
            };
        }

        // (2) Mode settings — copy the working struct's eight fields onto the
        // profile's settings record. ProfileSettings is a mutable record, so
        // direct property assignment is fine.
        var s = item.Profile.Settings;
        s.RapidTriggerEnabled       = _modeSettings.RapidTriggerEnabled;
        s.TurboEnabled              = _modeSettings.TurboEnabled;
        s.LastWinEnabled            = _modeSettings.LastWinEnabled;
        s.ReleaseDualTriggerEnabled = _modeSettings.ReleaseDualTriggerEnabled;
        s.RTMatchEnabled            = _modeSettings.RTMatchEnabled;
        s.KeystrokeTrackingEnabled  = _modeSettings.KeystrokeTrackingEnabled;
        s.LastWinReplaceEnabled     = _modeSettings.LastWinReplaceEnabled;
        s.AutoMatchMode             = _modeSettings.AutoMatchMode;

        // (3) Remap + LW pairs. Create a RemapProfile lazily so legacy
        // profiles (no remaps ever) don't get a useless empty RemapProfile
        // serialized into them.
        bool hasRemaps = false;
        for (int i = 0; i < _remaps.Length; i++) if (_remaps[i] != 0) { hasRemaps = true; break; }
        bool hasLwPairs = _lwPairs.Count > 0;
        if (hasRemaps || hasLwPairs || item.RemapProfile is not null)
        {
            item.RemapProfile ??= new RemapProfile();
            item.RemapProfile.PerSlotHidUsage = (byte[])_remaps.Clone();
            item.RemapProfile.LwPairs = _lwPairs.Select(p => new[] { p.A, p.B }).ToArray();
            item.RemapProfile.RdtPairs = _rdtPairs.Select(p => new[] { p.Press, p.Release }).ToArray();
        }

        _profileManager.ScheduleSave(item);
    }

    // ---- Firmware-update banner -------------------------------------------

    private FirmwareCheckResult? _firmwareResult;

    private void ShowFirmwareBanner(FirmwareCheckResult result)
    {
        _firmwareResult = result;
        var modelLabel = _activeModel?.DisplayName ?? "your keyboard";
        FirmwareBannerText.Text =
            $"Firmware v{result.LatestVersion} available for {modelLabel} " +
            $"(you're on v{result.CurrentVersion}). Click to open the downloads page.";
        FirmwareBanner.Visibility = Visibility.Visible;
        DebugLogger.Log($"KeyboardPerformanceView: firmware banner shown — {result.CurrentVersion} → {result.LatestVersion}");
    }

    private void FirmwareBanner_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // The dismiss button sets Handled=true so this only fires when the
        // user clicks the banner body itself.
        if (e.Handled) return;
        OpenFirmwareDownloadsPage();
    }

    // Models we've verified packet behavior on a physical unit for. Anything
    // outside this set still gets the editor rendered against its actual
    // visual layout (the render pipeline is fully layout-driven) but earns a
    // "beta" banner so the user knows we haven't confirmed the wire format
    // commits properly on their hardware.
    private static readonly HashSet<string> HardwareVerifiedPrefixes = new()
    {
        "ddeerA75ProProfile",
        "ddeerA75UltraProfile",
        "ddeerG65Profile",
    };

    // Fires whenever the connected keyboard changes (plug, unplug, swap).
    // Marshals to the UI thread because KeyboardManager raises the event
    // from whichever thread HidSharp's DeviceList.Changed delivered on,
    // which is typically a background thread on Windows.
    private void OnConnectedKeyboardChanged(KeyboardWithSpecs? _)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(RefreshHeaderAndBanner));
            return;
        }
        RefreshHeaderAndBanner();
    }

    // Single source of truth for HeaderTitle.Text + ModelStatusBanner state.
    // Reads the current keyboard from _keyboardManager (not _activeModel,
    // because that was resolved at construction and is stale after a swap).
    private void RefreshHeaderAndBanner()
    {
        var current = _keyboardManager?.KeyboardWithSpecs;
        // Re-resolve so a different model plugged in mid-session picks up
        // the right layout label. _activeLayout/_activeLayoutFlat are still
        // the original layout from ctor — re-rendering those is out of
        // scope for this fix; the header at least won't lie.
        var model = KeyboardLayoutResolver.Resolve(current);
        var modelLabel = model?.DisplayName ?? _activeModel?.DisplayName ?? "A75 Pro (default)";
        var fw = current?.Specs.FirmwareVersion;
        var fwText = !string.IsNullOrEmpty(fw) ? $" · firmware v{fw}" : "";
        HeaderTitle.Text = current is not null
            ? $"DrunkDeer {modelLabel}{fwText}"
            : $"DrunkDeer {modelLabel} · no keyboard connected (changes won't sync)";
        EvaluateModelBanner(model);
    }

    private bool _modelBannerDismissed;

    // `resolvedModel` is the model freshly resolved against the *currently
    // connected* keyboard (see RefreshHeaderAndBanner). We must not use
    // _activeModel here: it's resolved once at construction and goes stale
    // after a hot-plug, which produced a false "Unrecognized" banner when the
    // app started before the keyboard was plugged in. Fall back to
    // _activeModel only when re-resolution yielded nothing.
    private void EvaluateModelBanner(KeyboardModel? resolvedModel)
    {
        if (_modelBannerDismissed) return;

        if (_keyboardManager?.KeyboardWithSpecs is not { } kb)
        {
            ModelStatusBanner.Visibility = Visibility.Collapsed;
            return;
        }

        var model = resolvedModel ?? _activeModel;

        if (model is null)
        {
            // Connected but unrecognized — A75 Pro layout used as best-effort.
            var pidHex = $"0x{kb.Keyboard.ProductID:x4}";
            var typeCodeText = kb.Specs.KeyboardType?.ToString() ?? "unknown";
            var fw = kb.Specs.FirmwareVersion;
            var fwText = string.IsNullOrEmpty(fw) ? "unknown" : $"v{fw}";
            ModelStatusBannerText.Text =
                $"⚠ Unrecognized DrunkDeer model (PID {pidHex} · TypeCode {typeCodeText} · firmware {fwText}). " +
                $"Showing the A75 Pro layout as a best-effort guess — Sync may misbehave. Click to report.";
            ModelStatusBanner.Background = (System.Windows.Media.Brush)FindResource("DdWarnSoft");
            ModelStatusBanner.BorderBrush = (System.Windows.Media.Brush)FindResource("DdWarn");
            ModelStatusBanner.Visibility = Visibility.Visible;
            return;
        }

        if (!HardwareVerifiedPrefixes.Contains(model.ProfilePrefix))
        {
            ModelStatusBannerText.Text =
                $"Detected {model.DisplayName}. The editor is in beta for this model — " +
                $"please report any issues so we can verify packet behavior on hardware.";
            ModelStatusBanner.Background = (System.Windows.Media.Brush)FindResource("DdInfoSoft");
            ModelStatusBanner.BorderBrush = (System.Windows.Media.Brush)FindResource("DdInfo");
            ModelStatusBanner.Visibility = Visibility.Visible;
            return;
        }

        ModelStatusBanner.Visibility = Visibility.Collapsed;
    }

    private void ModelStatusBanner_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled) return;
        OpenModelReportIssue();
    }

    private void ModelStatusBannerDismiss_Click(object sender, RoutedEventArgs e)
    {
        _modelBannerDismissed = true;
        ModelStatusBanner.Visibility = Visibility.Collapsed;
        if (e is RoutedEventArgs re) re.Handled = true;
    }

    private void OpenModelReportIssue()
    {
        try
        {
            var kb = _keyboardManager?.KeyboardWithSpecs;
            var pidHex = kb is not null ? $"0x{kb.Value.Keyboard.ProductID:x4}" : "unknown";
            var typeCode = kb?.Specs.KeyboardType?.ToString() ?? "unknown";
            var fw = kb?.Specs.FirmwareVersion;
            var fwText = string.IsNullOrEmpty(fw) ? "unknown" : fw;
            var modelName = _activeModel?.DisplayName ?? "Unrecognized";
            var appVer = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
            var title = Uri.EscapeDataString($"[hardware report] {modelName} · PID {pidHex}");
            var body = Uri.EscapeDataString(
                $"### Device info\n\n" +
                $"- Model (as detected): {modelName}\n" +
                $"- USB PID: {pidHex}\n" +
                $"- TypeCode: {typeCode}\n" +
                $"- Firmware: {fwText}\n" +
                $"- App version: {appVer}\n\n" +
                $"### What works / doesn't\n\n" +
                $"<!-- describe what you've tried — does Sync to Keyboard apply? does remap commit? does Last Win pair? -->\n\n" +
                $"### Logs\n\n" +
                $"<!-- attach debug.log from the Open Log Folder button if helpful -->\n");
            var url = $"https://github.com/svumo/DrunkDeer-Control/issues/new?title={title}&body={body}";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Couldn't open browser: {ex.Message}";
        }
    }

    private void FirmwareBannerDismiss_Click(object sender, RoutedEventArgs e)
    {
        FirmwareBanner.Visibility = Visibility.Collapsed;
        // Mark the click so the parent MouseLeftButtonUp on the Border
        // doesn't ALSO open the browser — WPF event routing would otherwise
        // fire both handlers for a single click on the × button.
        if (e is System.Windows.RoutedEventArgs re) re.Handled = true;
    }

    private void OpenFirmwareDownloadsPage()
    {
        try
        {
            var url = _firmwareResult?.DownloadsUrl ?? FirmwareUpdateChecker.DownloadsUrl;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Couldn't open browser: {ex.Message}";
        }
    }

    // Pull Settings out of the DI container. Returns null when the window
    // is constructed outside the running app (e.g. XAML designer, unit test)
    // — in which case we skip the firmware check silently.
    private static Settings? TryResolveSettings()
    {
        try
        {
            return WpfApp.App.ServiceProvider.GetService<Settings>();
        }
        catch
        {
            return null;
        }
    }

    // Remap is now performed inline from the actuation drawer's code pill
    // (click the chip → press a key → bound). No separate Remap tab or
    // RemapDrawerCtrl exist anymore — that was a Phase H scaffold that
    // duplicated the canvas. The OnDrawerRebindRequested / Restore
    // handlers further down do the actual mutation + sync.

    private void OnResetAllRemaps(object? sender, EventArgs e)
    {
        Array.Clear(_remaps);
        foreach (var cap in _caps.Values) cap.RemappedLabel = null;
        // Clearing remaps without clearing the LW/RDT pair lists leaves
        // the firmware with pair-table entries pointing at default HID
        // codes — pairs would still "fire" but with the wrong release
        // (or none if the release-slot US got reset elsewhere). A "reset
        // all remaps" button is intuitively a clean-slate operation, so
        // wipe pairs too.
        ClearAllPairsForReset();
        SyncDrawerFromSelection();
        StatusText.Text = "All remaps and pairs cleared — auto-syncing…";
        ScheduleAutoSync();
    }

    // Clears LW + RDT pair lists, snapshots, and dependent UI state.
    // Used by the reset-all-keys and reset-all-remaps buttons so neither
    // leaves orphaned firmware-side pair table entries pointing at keys
    // whose AP/DS/US has just been wiped. Does NOT toggle off the LW/RDT
    // mode bits — the user keeps their master-switch state; only the
    // pair config is reset.
    private void ClearAllPairsForReset()
    {
        bool hadAny = _rdtPairs.Count > 0 || _lwPairs.Count > 0 || _rdtPreState.Count > 0;
        if (!hadAny) return;

        _rdtPairs.Clear();
        _lwPairs.Clear();
        _rdtPreState.Clear();
        PersistRdtPairs();
        PersistLwPairs();
        RefreshRdtPairsBar();
        RefreshLwPairsBar();
        CancelRdtPick();
        CancelLwPick();
        HideRdtDrawer();
        DebugLogger.Log("KeyboardPerformanceView.ClearAllPairsForReset: cleared all LW/RDT pairs and snapshots on reset");
    }

    // Raised on every user edit (per-key slider drag, mode toggle, remap,
    // LW pair change, preset apply, reset). The parent uses this to mark
    // the currently-selected profile as Active — editing implies "I'm
    // working in this profile now," so the green dot and top pill should
    // follow without the user having to click Activate again.
    public event EventHandler? EditOccurred;

    // Bubbled from the drawer's rebind-capture lifecycle. The host suspends
    // its global RegisterHotKey hooks while capture is active, otherwise keys
    // like `]` (when bound as a profile direct-switch hotkey somewhere) get
    // eaten by the OS before WPF's PreviewKeyDown can see them.
    public event EventHandler? RemapCaptureStarted;
    public event EventHandler? RemapCaptureEnded;

    // Restart the debounce. If a sync is already mid-flight we still re-arm
    // so the next batch picks up any post-flight changes.
    private void ScheduleAutoSync()
    {
        // Fire EditOccurred FIRST so MainWindow.OnKeyboardEdit can flip the
        // active profile to whatever the user has selected in the sidebar.
        // WriteBackToActiveProfile below uses ProfileManager.CurrentIndex, so
        // if the user selected profile A but was previously editing on B's
        // active firmware, the write must land on A.
        EditOccurred?.Invoke(this, EventArgs.Empty);

        // Mirror the view's working state onto the (now-correct) active
        // ProfileItem and schedule a debounced disk save. By the time the
        // auto-sync timer ticks, PushCurrentProfile is reading the canonical
        // copy — not view-only state that the user expected to persist.
        WriteBackToActiveProfile();

        if (_keyboardManager?.KeyboardWithSpecs is null) return;
        _autoSyncTimer.Stop();
        _autoSyncTimer.Start();
    }

    private async void OnAutoSyncTick(object? sender, EventArgs e)
    {
        _autoSyncTimer.Stop();
        await DoSyncAsync(manual: false).ConfigureAwait(true);
    }

    // ---- Quick-select pills (Phase F) --------------------------------------

    private void OnQuickSelectPill(object? sender, QuickSelectEventArgs e)
    {
        var codes = ResolveQuickSelectGroup(e.Group);
        if (codes.Count == 0) return;

        switch (e.Mode)
        {
            case QuickSelectMode.Replace:  _vm.SelectedKeys.ReplaceAll(codes); break;
            case QuickSelectMode.Add:      _vm.SelectedKeys.UnionWith(codes);   break;
            case QuickSelectMode.Subtract: _vm.SelectedKeys.ExceptWith(codes); break;
        }
    }

    // Group name → list of key codes within the currently-active layout.
    // Layout-agnostic so the same pill bar works on every model.
    private IReadOnlyList<string> ResolveQuickSelectGroup(string group) =>
        group switch
        {
            "wasd"     => new[] { "w", "a", "s", "d" }.Where(_caps.ContainsKey).ToArray(),
            "all"      => _caps.Keys.ToArray(),
            "letters"  => _activeLayoutFlat
                            .Where(k => k.Code.Length == 1 && k.Code[0] >= 'a' && k.Code[0] <= 'z')
                            .Select(k => k.Code).ToArray(),
            "numerals" => _activeLayoutFlat
                            .Where(k => k.Code.Length == 1 && k.Code[0] >= '0' && k.Code[0] <= '9')
                            .Select(k => k.Code).ToArray(),
            "mods"     => _activeLayoutFlat
                            .Where(k => k.Type == "mod")
                            .Select(k => k.Code).ToArray(),
            "frow"     => _activeLayoutFlat
                            .Where(k => k.Code.Length >= 2 && k.Code[0] == 'f'
                                        && int.TryParse(k.Code.AsSpan(1), out var n) && n is >= 1 and <= 12)
                            .Select(k => k.Code).ToArray(),
            "arrows"   => _activeLayoutFlat
                            .Where(k => k.Column == "arrow")
                            .Select(k => k.Code).ToArray(),
            _ => Array.Empty<string>(),
        };

    // ---- Presets (Phase F) -------------------------------------------------

    // Preset files live at WpfApp/Resources/Presets/*.json as embedded WPF
    // resources (see WpfApp.csproj). They use DrunkDeer's web-driver export
    // format — same `keyname` strings as KeyboardLayout's `ProfileKeyName`.
    // Edit the JSON, rebuild, and the buttons pick up the new values.
    private static readonly Dictionary<string, IReadOnlyDictionary<string, (double Ap, double Ds, double Us)>>
        _presetJsonCache = new(StringComparer.OrdinalIgnoreCase);

    // Parse a preset JSON once, keyed by ProfileKeyName (layout-independent).
    private static IReadOnlyDictionary<string, (double Ap, double Ds, double Us)> ReadPresetJson(string fileName)
    {
        if (_presetJsonCache.TryGetValue(fileName, out var cached)) return cached;

        var byProfileName = new Dictionary<string, (double Ap, double Ds, double Us)>();
        try
        {
            var uri = new Uri($"/Resources/Presets/{fileName}", UriKind.Relative);
            var info = System.Windows.Application.GetResourceStream(uri);
            if (info is not null)
            {
                using var doc = System.Text.Json.JsonDocument.Parse(info.Stream);
                if (doc.RootElement.TryGetProperty("keys_array", out var arr))
                {
                    foreach (var entry in arr.EnumerateArray())
                    {
                        var name = entry.TryGetProperty("keyname", out var nm) ? nm.GetString() : null;
                        if (string.IsNullOrEmpty(name)) continue;
                        double ap = entry.TryGetProperty("action_point", out var a) ? a.GetDouble() : 2.0;
                        double ds = entry.TryGetProperty("downstroke",   out var d) ? d.GetDouble() : 0.0;
                        double us = entry.TryGetProperty("upstroke",     out var u) ? u.GetDouble() : 0.0;
                        // Skip default-valued entries — the preset only "owns"
                        // the keys it tunes, so non-targeted keys stay at the
                        // user's current values when the preset is applied.
                        if (Math.Abs(ap - 2.0) < 0.001 && ds == 0.0 && us == 0.0) continue;
                        byProfileName[name] = (ap, ds, us);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"KeyboardPerformanceView: preset load failed for {fileName} — {ex.GetType().Name}: {ex.Message}");
        }

        return _presetJsonCache[fileName] = byProfileName;
    }

    // Re-key a parsed preset (ProfileKeyName → values) onto the current
    // layout's `Code` strings, which is what `_caps` uses. Layout-specific,
    // so this happens per call instead of being cached.
    private Dictionary<string, (double Ap, double Ds, double Us)> LoadPresetForLayout(string fileName)
    {
        var byProfileName = ReadPresetJson(fileName);
        var byCode = new Dictionary<string, (double Ap, double Ds, double Us)>();
        foreach (var lk in _activeLayoutFlat)
        {
            if (byProfileName.TryGetValue(lk.ProfileKeyName, out var v))
                byCode[lk.Code] = v;
        }
        return byCode;
    }

    private void OnPresetClicked(object? sender, PresetClickedEventArgs e)
    {
        Dictionary<string, (double Ap, double Ds, double Us)> apply;
        switch (e.Preset)
        {
            case "fps":
                apply = LoadPresetForLayout("FPS.json");
                break;
            case "valorant":
                apply = LoadPresetForLayout("Valorant.json");
                break;
            case "typing":
                apply = LoadPresetForLayout("Typing.json");
                break;
            case "reset":
                apply = _caps.Keys.ToDictionary(c => c, _ => (Ap: 2.0, Ds: 0.0, Us: 0.0));
                break;
            default:
                return;
        }

        var hasSelection = _vm.SelectedKeys.Count > 0;
        int touched = 0;
        foreach (var (code, values) in apply)
        {
            if (!_caps.TryGetValue(code, out var cap)) continue;
            if (hasSelection && !_vm.SelectedKeys.Contains(code)) continue;
            cap.ActuationPoint = values.Ap;
            cap.Downstroke = values.Ds;
            cap.Upstroke = values.Us;
            touched++;
        }

        SyncDrawerFromSelection();
        UpdatePresetCounts();
        StatusText.Text = $"Applied preset '{e.Preset}' to {touched} key{(touched == 1 ? "" : "s")} — auto-syncing…";
        ScheduleAutoSync();
    }

    private void UpdatePresetCounts()
    {
        int customized = 0;
        int rt = 0;
        foreach (var cap in _caps.Values)
        {
            if (!NearlyEqual(cap.ActuationPoint, 2.0) || cap.Downstroke > 0 || cap.Upstroke > 0)
                customized++;
            if (cap.Downstroke > 0 || cap.Upstroke > 0)
                rt++;
        }
        PresetBar.UpdateCounts(customized, rt);
    }

    private void OnModeStripToggle(object? sender, ModeToggleEventArgs e)
    {
        // RDT toggle-off side-effect: revert every slot in the pair list to
        // its pre-pair AP/DS/US, then clear the snapshot table. Pair list
        // itself is preserved so toggling back ON keeps the user's config.
        // Has to run BEFORE the _modeSettings flag flips so RestoreAllRdt-
        // PairSlots can still see the live pair list (it doesn't change
        // on toggle-off, but consistent ordering keeps the helper simple).
        if (e.Mode == "rdt" && !e.Value)
        {
            RestoreAllRdtPairSlots();
        }

        switch (e.Mode)
        {
            case "rt":        _modeSettings.RapidTriggerEnabled       = e.Value; break;
            case "rdt":       _modeSettings.ReleaseDualTriggerEnabled = e.Value; break;
            case "lw":        _modeSettings.LastWinEnabled            = e.Value; break;
            case "turbo":     _modeSettings.TurboEnabled              = e.Value; break;
            case "keystroke":
                _modeSettings.KeystrokeTrackingEnabled  = e.Value;
                if (e.Value) StartKeystrokeTracking();
                else         StopKeystrokeTracking();
                break;
        }

        // RDT toggle-on with existing pairs: re-apply the gaming defaults
        // (snapshot first so the next toggle-off can revert cleanly). New
        // pairs added afterwards will go through HandleRdtPick which has
        // its own snapshot path.
        if (e.Mode == "rdt" && e.Value && _rdtPairs.Count > 0)
        {
            ReapplyRdtPairKeyDefaults();
        }

        // UI-enforced conflict from the official driver: Turbo and Keystroke
        // Tracking are mutually exclusive. Mirror it here so the firmware
        // never sees an invalid combination on Sync.
        if (e.Mode == "turbo" && e.Value && _modeSettings.KeystrokeTrackingEnabled)
        {
            _modeSettings.KeystrokeTrackingEnabled = false;
            ModeStrip.KeystrokeTrackingEnabled = false;
            StopKeystrokeTracking();
        }
        else if (e.Mode == "keystroke" && e.Value && _modeSettings.TurboEnabled)
        {
            _modeSettings.TurboEnabled = false;
            ModeStrip.TurboEnabled = false;
        }

        // The official driver auto-enables global Rapid Trigger and disables
        // Turbo whenever LW (or RDT) is enabled — the firmware's LW deconflict
        // pipeline runs INSIDE the RT pipeline, and Turbo bypasses both. Mirror
        // that here so the user doesn't have to know.
        if ((e.Mode == "lw" || e.Mode == "rdt") && e.Value)
        {
            if (!_modeSettings.RapidTriggerEnabled)
            {
                _modeSettings.RapidTriggerEnabled = true;
                ModeStrip.RapidTriggerEnabled = true;
            }
            if (_modeSettings.TurboEnabled)
            {
                _modeSettings.TurboEnabled = false;
                ModeStrip.TurboEnabled = false;
            }
        }

        // LW and RDT are mutually exclusive in the official driver — they
        // share the same Type-4 remap entry machinery and one slot can carry
        // exactly one rdtEnabled flag. Toggling either one ON force-disables
        // its sibling. Skip when both are already in sync (avoids ping-pong
        // when this handler fires from a programmatic ModeStrip update).
        if (e.Mode == "lw" && e.Value && _modeSettings.ReleaseDualTriggerEnabled)
        {
            _modeSettings.ReleaseDualTriggerEnabled = false;
            ModeStrip.ReleaseDualTriggerEnabled = false;
            CancelRdtPick();
            HideRdtDrawer();
        }
        else if (e.Mode == "rdt" && e.Value && _modeSettings.LastWinEnabled)
        {
            _modeSettings.LastWinEnabled = false;
            ModeStrip.LastWinEnabled = false;
            CancelLwPick();
        }

        // Visible feedback — without this the global toggles look broken
        // (they don't change per-key visuals; they're firmware-wide flags
        // sent on Sync).
        var label = e.Mode switch
        {
            "rt" => "Global Rapid Trigger",
            "rdt" => "Release Dual-Trigger",
            "lw" => "Last Win",
            "turbo" => "Turbo Mode",
            "keystroke" => "Keystroke Tracking",
            _ => e.Mode,
        };
        // When Last Win is being enabled with no pairs configured, seed the
        // obvious defaults (A↔D, W↔S, ←↔→, ↑↔↓). The LW master flag alone
        // does nothing — the firmware needs a pair table to deconflict
        // against (protocol §8). Without this seed, flipping the toggle
        // ships pairCount=0 and the keyboard behaves identically with LW
        // on or off, which is exactly the "LW doesn't work" symptom users
        // hit. The user can still remove individual pairs in the bar
        // afterwards; we only seed when the list is empty.
        if (e.Mode == "lw" && e.Value && _lwPairs.Count == 0)
        {
            SeedDefaultLwPairs();
        }

        // Show/hide the LW + RDT pair editors whenever toggles change.
        // Mutual exclusion above means only one bar is ever visible at once.
        UpdateLwPairsBarVisibility();
        UpdateRdtPairsBarVisibility();

        // Always auto-sync — including keystroke-tracking changes. On
        // A75 Pro 0.09 the firmware drops its remap layer when it receives
        // the tracking-enable packet (and again when it receives the stop),
        // so a user with remaps would otherwise see their keymap revert
        // until they manually re-synced. The official driver hides this by
        // running tracking start/stop at the tail of its full save flow
        // (see docs/keyboard-protocol.md §11.1); we get the same effect by
        // following the toggle with a debounced sync that re-pushes the
        // 42-packet remap stream + RTPAuthority handshake.
        //
        // The 1 s debounce also gives the firmware time to settle into
        // tracking mode before the transient sync stream opens; the
        // listener's isSyncEcho filter (HidStreamListener.cs) already
        // discards every packet a parallel sync stream produces, so the
        // depth bars survive.
        StatusText.Text = $"{label} {(e.Value ? "ON" : "OFF")} — auto-syncing…";
        ScheduleAutoSync();
    }

    // ---- Keystroke tracking ----------------------------------------------
    //
    // Opens a long-lived HID stream on the connected keyboard and pipes its
    // unsolicited depth packets (0xB7 / 0xFD-06) into the KeyCaps' LiveDepth
    // DP via KeyDepthBroker. Gated on a connected keyboard — without one
    // there's nothing to listen to and we no-op.
    private void StartKeystrokeTracking()
    {
        if (_trackingListener != null) return; // already running
        if (_keyboardManager?.KeyboardWithSpecs is not { } kb)
        {
            DebugLogger.Log("KeyboardPerformanceView: keystroke tracking requested but no keyboard connected — skipping listener");
            return;
        }

        try
        {
            // Build slot->cap lookup once. _activeLayoutFlat orders entries
            // by KeyIndex implicitly (rows are visual but indices are the
            // firmware slot numbers). Caps live in _caps keyed by code.
            if (_capBySlot == null)
            {
                _capBySlot = new KeyCap?[126];
                foreach (var lk in _activeLayoutFlat)
                {
                    if (lk.KeyIndex < 0 || lk.KeyIndex >= _capBySlot.Length) continue;
                    if (_caps.TryGetValue(lk.Code, out var cap))
                        _capBySlot[lk.KeyIndex] = cap;
                }
            }

            _trackingStream = kb.Keyboard.Open();
            _trackingListener = new HidStreamListener(_trackingStream);
            _trackingBroker = new KeyDepthBroker(_trackingListener, Dispatcher);
            _trackingBroker.DepthApplied += OnDepthApplied;
            // Re-arm the firmware after each 3-chunk cycle. Firmware streams
            // exactly ONE round per enable-packet — without this the stream
            // dies after ~50ms. JS bundle's tracking loop does the same
            // (setTimeout 20ms inside the chunkIdx==2 handler → resend).
            _trackingListener.TrackingCycleComplete += OnTrackingCycleComplete;
            _trackingListener.Start();
            // Register sync-coordination hooks so each PushCurrentProfileAsync
            // pauses firmware streaming around its writes. Without this, 0xB7
            // chunks pollute the sync HidStream's read queue and exhaust
            // TryWritePacket's drain (see Driver/HidDeviceExtensions.cs:37).
            if (_profileManager is not null)
            {
                _profileManager.BeforeSyncAsync = PauseTrackingForSync;
                _profileManager.AfterSyncAsync = ResumeTrackingAfterSync;
            }
            // Kick off the first cycle. Critical: write through the listener's
            // own stream — firmware streams replies back to the HID handle that
            // requested them, and DoSyncAsync's transient `using var stream` is
            // closed by the time replies arrive.
            SendTrackingEnable(true);
            DebugLogger.Log("KeyboardPerformanceView: keystroke tracking listener started");
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"KeyboardPerformanceView: failed to start keystroke listener — {ex.GetType().Name}: {ex.Message}");
            StopKeystrokeTracking();
        }
    }

    private void StopKeystrokeTracking()
    {
        var broker = _trackingBroker;
        var listener = _trackingListener;
        var stream = _trackingStream;

        // Tell the firmware to stop streaming BEFORE we tear down our reader.
        // Otherwise the next OnTrackingCycleComplete-driven re-arm could fire
        // a tracking-start packet at a dead stream and we'd see noisy errors.
        if (stream != null)
        {
            try { stream.WritePacketNoAck(Packets.BuildKeystrokeTrackingPacket(false)); }
            catch (Exception ex) { DebugLogger.Log($"KeyboardPerformanceView: tracking disable write failed — {ex.Message}"); }
        }

        // Unregister sync-coordination hooks first — otherwise PushCurrentProfile
        // could still call into a half-torn-down listener.
        if (_profileManager is not null)
        {
            _profileManager.BeforeSyncAsync = null;
            _profileManager.AfterSyncAsync = null;
        }

        _trackingBroker = null;
        _trackingListener = null;
        _trackingStream = null;

        if (listener != null) listener.TrackingCycleComplete -= OnTrackingCycleComplete;
        if (broker != null) broker.DepthApplied -= OnDepthApplied;
        broker?.Dispose();
        // Fire-and-forget the async stop; we don't need to await it on the UI
        // thread and blocking here would freeze the toggle response.
        if (listener != null)
        {
            _ = Task.Run(async () =>
            {
                try { await listener.StopAsync().ConfigureAwait(false); }
                catch (Exception ex) { DebugLogger.Log($"KeyboardPerformanceView: listener stop error — {ex.Message}"); }
                finally
                {
                    try { listener.Dispose(); } catch { }
                    try { stream?.Dispose(); } catch { }
                }
            });
        }
        else
        {
            try { stream?.Dispose(); } catch { }
        }

        // Reset every cap's bar so the canvas snaps clean immediately.
        if (_capBySlot != null)
        {
            foreach (var cap in _capBySlot)
            {
                if (cap != null) cap.LiveDepth = 0.0;
            }
        }
        DebugLogger.Log("KeyboardPerformanceView: keystroke tracking listener stopped");
    }

    // Re-arms the firmware after a complete 3-chunk tracking cycle. The
    // 20ms delay mirrors the official JS bundle's setTimeout — without it
    // the firmware can drop the packet (we're racing the final chunk's
    // input report). Fire-and-forget; if the stream's gone (window closed,
    // disconnect mid-cycle), we silently no-op.
    private void OnTrackingCycleComplete(object? sender, EventArgs e)
    {
        _ = Task.Delay(20).ContinueWith(_ =>
        {
            if (_trackingListener == null) return; // tracking turned off mid-flight
            SendTrackingEnable(true);
        }, TaskScheduler.Default);
    }

    // Called by ProfileManager.PushCurrentProfileAsync via BeforeSyncAsync.
    // Stops the firmware emitting 0xB7 depth chunks for the duration of the
    // sync write batch — Windows delivers every input report to every open
    // HID handle, so without this our sync HidStream's read queue fills with
    // depth chunks and TryWritePacket's 16-attempt drain gets overrun.
    private async Task PauseTrackingForSync()
    {
        var listener = _trackingListener;
        var stream = _trackingStream;
        if (listener is null || stream is null) return;
        try
        {
            listener.Pause();
            stream.WritePacketNoAck(Packets.BuildKeystrokeTrackingPacket(false));
            // Let any chunk already mid-flight settle so the sync stream's
            // first read drains it cleanly rather than racing the open.
            await Task.Delay(30).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"KeyboardPerformanceView: PauseTrackingForSync — {ex.GetType().Name}: {ex.Message}");
        }
    }

    private Task ResumeTrackingAfterSync()
    {
        var listener = _trackingListener;
        var stream = _trackingStream;
        if (listener is null || stream is null) return Task.CompletedTask;
        try
        {
            stream.WritePacketNoAck(Packets.BuildKeystrokeTrackingPacket(true));
            listener.Resume();
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"KeyboardPerformanceView: ResumeTrackingAfterSync — {ex.GetType().Name}: {ex.Message}");
        }
        return Task.CompletedTask;
    }

    // Writes the keystroke-tracking on/off packet through the listener's
    // long-lived HID stream. Doing this through DoSyncAsync's short-lived
    // stream is what broke us before: firmware streams replies back to the
    // HID handle that requested, and that handle was already closed by the
    // time depth packets arrived.
    private void SendTrackingEnable(bool on)
    {
        var stream = _trackingStream;
        if (stream == null) return;
        try
        {
            var packet = Packets.BuildKeystrokeTrackingPacket(on);
            stream.WritePacketNoAck(packet);
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"KeyboardPerformanceView: tracking enable write failed — {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void OnDepthApplied(object? sender, KeyDepthEventArgs e)
    {
        // Already on the UI thread (broker marshals via Dispatcher.BeginInvoke).
        var arr = _capBySlot;
        if (arr == null) return;
        if (e.SlotIndex < 0 || e.SlotIndex >= arr.Length) return;
        var cap = arr[e.SlotIndex];
        if (cap != null) cap.LiveDepth = e.DepthMm;
    }

    private void OnResetAllKeys(object? sender, EventArgs e)
    {
        // Reset every visible cap to AP=2.0, no RT. Sync still has to be
        // clicked explicitly to push to firmware.
        foreach (var cap in _caps.Values)
        {
            cap.ActuationPoint = 2.0;
            cap.Downstroke = 0.0;
            cap.Upstroke = 0.0;
        }
        // Wipe pairs too — they'd otherwise reference release slots whose
        // US has just been zeroed, leaving the firmware with valid pair
        // table entries but no release-target US to fire them on. Cleaner
        // to make "reset all keys" a true clean-slate.
        ClearAllPairsForReset();
        SyncDrawerFromSelection();
        UpdatePresetCounts();
        StatusText.Text = "All keys and pairs reset to default — auto-syncing…";
        ScheduleAutoSync();
    }

    private void BuildRows()
    {
        for (int rowIndex = 0; rowIndex < _activeLayout.Count; rowIndex++)
        {
            var row = _activeLayout[rowIndex];
            var rowPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, rowIndex == 0 ? 0 : KeyGap, 0, 0),
            };

            string? prevColumn = null;
            for (int i = 0; i < row.Count; i++)
            {
                var lk = row[i];

                double leftMargin = i == 0 ? 0 : KeyGap;
                if (lk.Column == "nav" && prevColumn != "nav")
                    leftMargin += NavGutter;

                var cap = new KeyCap
                {
                    Code = lk.Code,
                    Label = lk.Label,
                    SubLabel = lk.Sub,
                    UnitWidth = UnitWidth,
                    WidthMultiplier = lk.Width,
                    KeyType = lk.Type,
                    Uncertain = lk.Uncertain,
                    Margin = new Thickness(leftMargin, 0, 0, 0),
                };

                if (Demo.TryGetValue(lk.Code, out var demo))
                {
                    cap.ActuationPoint = demo.Ap;
                    cap.Downstroke = demo.Ds;
                    cap.Upstroke = demo.Us;
                }

                cap.CapClick += OnCapClick;
                _caps[lk.Code] = cap;
                rowPanel.Children.Add(cap);
                prevColumn = lk.Column;
            }

            RowsHost.Children.Add(rowPanel);
        }
    }

    // ---- Selection ----------------------------------------------------------

    private void OnCapClick(object? sender, KeyCapClickEventArgs e)
    {
        if (!_caps.ContainsKey(e.Code)) return;

        // LW / RDT pair-pick takes precedence over normal selection. Hijack
        // the click; don't change selection — the user is mid-pick, they
        // don't want their actuation drawer state thrown around. LW and RDT
        // pick states are mutually exclusive (toggle exclusion guarantees
        // it) so the order of these branches doesn't matter.
        if (_lwPickState != LwPickState.None)
        {
            HandleLwPick(e.Code);
            return;
        }
        if (_rdtPickState != RdtPickState.None)
        {
            HandleRdtPick(e.Code);
            return;
        }

        // If the RDT pair drawer is open and the user clicks any key on
        // the canvas, switch back to the regular per-key actuation drawer
        // for the new selection. Without this the pair drawer stays on
        // top of the actuation drawer and the user has to manually click
        // somewhere else to dismiss it.
        if (RdtDrawer.Visibility == Visibility.Visible)
        {
            HideRdtDrawer();
        }

        if (e.Shift)      _vm.SelectRange(e.Code);
        else if (e.Ctrl)  _vm.ToggleSelection(e.Code);
        else              _vm.Select(e.Code);
    }

    // ---- Last Win pair management -----------------------------------------

    // Seeds the canonical four-pair default that the old KeyboardDebugWindow
    // implementation used to auto-bundle on LW enable. Slot indices vary by
    // model (arrows live on different rows for A75/G65/Ultra), so resolve
    // them via the active layout flat at runtime rather than hard-coding.
    // Silently drops any pair whose key isn't present on the connected
    // model (e.g. a TKL without arrows).
    private void SeedDefaultLwPairs()
    {
        var defaults = new[]
        {
            ("a", "d"),
            ("w", "s"),
            ("left", "right"),
            ("up", "down"),
        };
        foreach (var (codeA, codeB) in defaults)
        {
            var lkA = _activeLayoutFlat.FirstOrDefault(k => k.Code == codeA);
            var lkB = _activeLayoutFlat.FirstOrDefault(k => k.Code == codeB);
            if (lkA is null || lkB is null) continue;
            if (lkA.KeyIndex < 0 || lkA.KeyIndex >= 126) continue;
            if (lkB.KeyIndex < 0 || lkB.KeyIndex >= 126) continue;
            byte a = (byte)Math.Min(lkA.KeyIndex, lkB.KeyIndex);
            byte b = (byte)Math.Max(lkA.KeyIndex, lkB.KeyIndex);
            if (a == b) continue;
            if (_lwPairs.Any(p => p.A == a && p.B == b)) continue;
            _lwPairs.Add((a, b));
        }
        if (_lwPairs.Count == 0) return;
        PersistLwPairs();
        RefreshLwPairsBar();
        WriteBackToActiveProfile();
        DebugLogger.Log($"KeyboardPerformanceView.SeedDefaultLwPairs: seeded {_lwPairs.Count} default pairs on LW enable");
    }

    private void LoadLwPairsFromSettings()
    {
        var settings = TryResolveSettings();
        var json = settings?.LastWinPairsJson;
        if (string.IsNullOrWhiteSpace(json) || json == "[]") return;
        try
        {
            var parsed = System.Text.Json.JsonSerializer.Deserialize<byte[][]>(json);
            if (parsed is null) return;
            foreach (var entry in parsed)
            {
                if (entry is { Length: 2 } && entry[0] != entry[1])
                {
                    var a = Math.Min(entry[0], entry[1]);
                    var b = Math.Max(entry[0], entry[1]);
                    if (!_lwPairs.Any(p => p.A == a && p.B == b))
                        _lwPairs.Add((a, b));
                }
            }
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"KeyboardPerformanceView: failed to load LW pairs from settings — {ex.Message}");
        }
    }

    private void PersistLwPairs()
    {
        var settings = TryResolveSettings();
        if (settings is null) return;
        try
        {
            var array = _lwPairs.Select(p => new[] { p.A, p.B }).ToArray();
            settings.LastWinPairsJson = System.Text.Json.JsonSerializer.Serialize(array);
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"KeyboardPerformanceView: failed to persist LW pairs — {ex.Message}");
        }
    }

    private string LabelForSlot(byte slot)
    {
        var lk = _activeLayoutFlat.FirstOrDefault(k => k.KeyIndex == slot);
        return lk?.Label ?? $"0x{slot:X2}";
    }

    private void RefreshLwPairsBar()
    {
        LwPairsBar.SetPairs(_lwPairs, LabelForSlot);
    }

    private void UpdateLwPairsBarVisibility()
    {
        // Only show while in Performance mode AND LW is enabled. In Remap
        // mode the bar would clutter the keymap-editing flow.
        LwPairsBar.Visibility = _modeSettings.LastWinEnabled
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (LwPairsBar.Visibility == Visibility.Collapsed) CancelLwPick();
    }

    private void OnLwAddPairRequested(object? sender, EventArgs e)
    {
        _lwPickState = LwPickState.AwaitFirst;
        _lwFirstPick = null;
        LwPairsBar.SetPickStatus("Click the first key…");
    }

    private void OnLwRemovePairRequested(object? sender, LwPairEventArgs e)
    {
        var a = Math.Min(e.SlotA, e.SlotB);
        var b = Math.Max(e.SlotA, e.SlotB);
        _lwPairs.RemoveAll(p => p.A == a && p.B == b);
        PersistLwPairs();
        RefreshLwPairsBar();
        ScheduleAutoSync();
    }

    private void HandleLwPick(string code)
    {
        var lk = _activeLayoutFlat.FirstOrDefault(k => k.Code == code);
        if (lk is null || lk.KeyIndex < 0 || lk.KeyIndex >= 126) return;
        var slot = (byte)lk.KeyIndex;

        if (_lwPickState == LwPickState.AwaitFirst)
        {
            _lwFirstPick = slot;
            _lwPickState = LwPickState.AwaitSecond;
            LwPairsBar.SetPickStatus($"First: {lk.Label}. Now click the second key…");
            return;
        }

        if (_lwPickState == LwPickState.AwaitSecond && _lwFirstPick is { } first)
        {
            if (slot == first)
            {
                LwPairsBar.SetPickStatus("Same key — click a different one…");
                return;
            }
            var a = Math.Min(first, slot);
            var b = Math.Max(first, slot);
            if (!_lwPairs.Any(p => p.A == a && p.B == b))
                _lwPairs.Add((a, b));
            CancelLwPick();
            PersistLwPairs();
            RefreshLwPairsBar();
            ScheduleAutoSync();
        }
    }

    private void CancelLwPick()
    {
        _lwPickState = LwPickState.None;
        _lwFirstPick = null;
        LwPairsBar.SetPickStatus(null);
    }

    // ---- Release Dual-Trigger pair handlers --------------------------------
    //
    // Mirrors the LW block above but with two semantic differences:
    //   1. Pairs are ORDERED (press, release); we don't canonicalise via
    //      Math.Min/Max.
    //   2. After committing a pair, the RDT drawer opens so the user can set
    //      per-pair AP/DS/US. The official driver does the same.

    private void LoadRdtPairsFromSettings()
    {
        var settings = TryResolveSettings();
        var json = settings?.ReleaseDualTriggerPairsJson;
        if (string.IsNullOrWhiteSpace(json) || json == "[]") return;
        try
        {
            var parsed = System.Text.Json.JsonSerializer.Deserialize<byte[][]>(json);
            if (parsed is null) return;
            foreach (var entry in parsed)
            {
                if (entry is { Length: 2 } && entry[0] != entry[1])
                {
                    // Preserve press/release order — first slot is press,
                    // second is release. Do NOT Math.Min/Max like LW does.
                    var press = entry[0];
                    var release = entry[1];
                    if (!_rdtPairs.Any(p => p.Press == press && p.Release == release))
                        _rdtPairs.Add((press, release));
                }
            }
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"KeyboardPerformanceView: failed to load RDT pairs from settings — {ex.Message}");
        }
    }

    private void PersistRdtPairs()
    {
        var settings = TryResolveSettings();
        if (settings is null) return;
        try
        {
            var array = _rdtPairs.Select(p => new[] { p.Press, p.Release }).ToArray();
            settings.ReleaseDualTriggerPairsJson = System.Text.Json.JsonSerializer.Serialize(array);
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"KeyboardPerformanceView: failed to persist RDT pairs — {ex.Message}");
        }
    }

    private void RefreshRdtPairsBar()
    {
        RdtPairsBar.SetPairs(_rdtPairs, LabelForSlot);
    }

    private void UpdateRdtPairsBarVisibility()
    {
        RdtPairsBar.Visibility = _modeSettings.ReleaseDualTriggerEnabled
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (RdtPairsBar.Visibility == Visibility.Collapsed)
        {
            CancelRdtPick();
            HideRdtDrawer();
        }
    }

    private void OnRdtAddPairRequested(object? sender, EventArgs e)
    {
        _rdtPickState = RdtPickState.AwaitFirst;
        _rdtFirstPick = null;
        RdtPairsBar.SetPickStatus("Click the press key (HID code on press)…");
    }

    private void OnRdtRemovePairRequested(object? sender, RdtPairEventArgs e)
    {
        _rdtPairs.RemoveAll(p => p.Press == e.PressSlot && p.Release == e.ReleaseSlot);
        PersistRdtPairs();
        RefreshRdtPairsBar();
        // If the drawer was editing the removed pair, hide it.
        if (RdtDrawer.PressSlot == e.PressSlot && RdtDrawer.ReleaseSlot == e.ReleaseSlot)
            HideRdtDrawer();

        // Restore both slots to their pre-pair AP/DS/US (gaming defaults
        // applied on commit get undone here). Only restore a slot if it
        // isn't still referenced by some OTHER pair — overlapping-pair
        // edge case where the user has multiple pairs sharing a key.
        RestoreSlotFromSnapshotIfUnused(e.PressSlot);
        RestoreSlotFromSnapshotIfUnused(e.ReleaseSlot);

        ScheduleAutoSync();
    }

    // Revert one slot's AP/DS/US to its pre-pair snapshot, if a snapshot
    // exists AND the slot isn't still referenced by another pair. Clears
    // the snapshot entry on success so a future pair-commit re-snapshots
    // fresh state (preserving the "first pair wins, undo restores original"
    // semantics for sequential pair lifecycles).
    private void RestoreSlotFromSnapshotIfUnused(byte slot)
    {
        if (_rdtPairs.Any(p => p.Press == slot || p.Release == slot)) return;
        if (!_rdtPreState.TryGetValue(slot, out var pre)) return;
        var lk = _activeLayoutFlat.FirstOrDefault(k => k.KeyIndex == slot);
        if (lk is null) { _rdtPreState.Remove(slot); return; }
        if (_caps.TryGetValue(lk.Code, out var cap))
        {
            cap.ActuationPoint = pre.Ap;
            cap.Downstroke = pre.Ds;
            cap.Upstroke = pre.Us;
        }
        _rdtPreState.Remove(slot);
    }

    // Mirror of RestoreAllRdtPairSlots — re-applies the gaming-optimal AP
    // default (0.5mm) and firmware-required US=1.5mm on every slot in the
    // current pair list. Snapshots the prior state first so a later
    // toggle-off can revert cleanly. Called from OnModeStripToggle when
    // RDT goes ON with non-empty _rdtPairs.
    private void ReapplyRdtPairKeyDefaults()
    {
        foreach (var (press, release) in _rdtPairs)
        {
            var pressLk = _activeLayoutFlat.FirstOrDefault(k => k.KeyIndex == press);
            var releaseLk = _activeLayoutFlat.FirstOrDefault(k => k.KeyIndex == release);
            KeyCap? pressCap = null, releaseCap = null;
            if (pressLk is not null) _caps.TryGetValue(pressLk.Code, out pressCap);
            if (releaseLk is not null) _caps.TryGetValue(releaseLk.Code, out releaseCap);

            if (pressCap is not null && !_rdtPreState.ContainsKey(press))
                _rdtPreState[press] = (pressCap.ActuationPoint, pressCap.Downstroke, pressCap.Upstroke);
            if (releaseCap is not null && !_rdtPreState.ContainsKey(release))
                _rdtPreState[release] = (releaseCap.ActuationPoint, releaseCap.Downstroke, releaseCap.Upstroke);

            if (pressCap is not null && pressCap.ActuationPoint > 1.0)
                pressCap.ActuationPoint = 0.5;
            if (releaseCap is not null && releaseCap.ActuationPoint > 1.0)
                releaseCap.ActuationPoint = 0.5;
            if (releaseCap is not null && releaseCap.Upstroke < 1.5)
                releaseCap.Upstroke = 1.5;
        }
    }

    // Restore every slot touched by any current RDT pair, then drop all
    // snapshots. Called when RDT is toggled OFF: the pair list is kept
    // (so toggling back ON still has the user's pair config), but the
    // per-key side-effects are undone so the keys feel normal again.
    private void RestoreAllRdtPairSlots()
    {
        // Snapshot the slot set first because RestoreSlotFromSnapshotIfUnused
        // checks _rdtPairs membership — we want every slot covered, not just
        // ones that drop out of the pair list as we go.
        var slots = new HashSet<byte>();
        foreach (var (press, release) in _rdtPairs)
        {
            slots.Add(press);
            slots.Add(release);
        }
        foreach (var slot in slots)
        {
            if (!_rdtPreState.TryGetValue(slot, out var pre)) continue;
            var lk = _activeLayoutFlat.FirstOrDefault(k => k.KeyIndex == slot);
            if (lk is null) continue;
            if (_caps.TryGetValue(lk.Code, out var cap))
            {
                cap.ActuationPoint = pre.Ap;
                cap.Downstroke = pre.Ds;
                cap.Upstroke = pre.Us;
            }
        }
        _rdtPreState.Clear();
    }

    private void OnRdtEditPairRequested(object? sender, RdtPairEventArgs e)
    {
        ShowRdtDrawerForPair(e.PressSlot, e.ReleaseSlot);
    }

    private void HandleRdtPick(string code)
    {
        var lk = _activeLayoutFlat.FirstOrDefault(k => k.Code == code);
        if (lk is null || lk.KeyIndex < 0 || lk.KeyIndex >= 126) return;
        var slot = (byte)lk.KeyIndex;

        if (_rdtPickState == RdtPickState.AwaitFirst)
        {
            // Each physical key can be the press-side of only one RDT pair
            // — the firmware can't apply two RDT mappings to the same slot.
            // Also disallow keys that are the release-side of another pair
            // (the firmware would lose the original release HID code).
            if (_rdtPairs.Any(p => p.Press == slot))
            {
                RdtPairsBar.SetPickStatus($"⚠ {lk.Label} is already the press key in another pair — pick a different key.", isError: true);
                return;
            }
            if (_rdtPairs.Any(p => p.Release == slot))
            {
                RdtPairsBar.SetPickStatus($"⚠ {lk.Label} is already a release key in another pair — pick a different key.", isError: true);
                return;
            }
            _rdtFirstPick = slot;
            _rdtPickState = RdtPickState.AwaitSecond;
            RdtPairsBar.SetPickStatus($"Press: {lk.Label}. Now click the release key (HID code on release)…");
            return;
        }

        if (_rdtPickState == RdtPickState.AwaitSecond && _rdtFirstPick is { } first)
        {
            if (slot == first)
            {
                RdtPairsBar.SetPickStatus("⚠ Same key — click a different one for the release output.", isError: true);
                return;
            }
            // Don't let the release slot reuse a key that's the press or
            // release of another pair — it'd collide with the firmware's
            // Type-4 entry / HID-code lookup at that slot.
            if (_rdtPairs.Any(p => p.Press == slot || p.Release == slot))
            {
                RdtPairsBar.SetPickStatus($"⚠ {lk.Label} is already used in another pair — pick a different release key.", isError: true);
                return;
            }
            // Preserve pick order: first = press-emit, second = release-emit.
            if (!_rdtPairs.Any(p => p.Press == first && p.Release == slot))
                _rdtPairs.Add((first, slot));

            // Snapshot pre-pair per-key state for both slots BEFORE we
            // modify anything — used by pair-remove and RDT-toggle-off to
            // restore "what it was before the pair touched it." First-pair-
            // wins: if the same slot later becomes part of another pair,
            // we keep the original pre-first-pair snapshot.
            var pressLk = _activeLayoutFlat.FirstOrDefault(k => k.KeyIndex == first);
            KeyCap? pressCap = null;
            if (pressLk is not null) _caps.TryGetValue(pressLk.Code, out pressCap);
            _caps.TryGetValue(lk.Code, out var releaseCap);
            if (pressCap is not null && !_rdtPreState.ContainsKey(first))
                _rdtPreState[first] = (pressCap.ActuationPoint, pressCap.Downstroke, pressCap.Upstroke);
            if (releaseCap is not null && !_rdtPreState.ContainsKey(slot))
                _rdtPreState[slot] = (releaseCap.ActuationPoint, releaseCap.Downstroke, releaseCap.Upstroke);

            // Force the release key's UpStroke to 1.5mm. The official driver
            // does this automatically on pair creation (verified via the
            // exported profile JSON: triggerKey.upstroke=1.5). Firmware
            // needs a non-zero US on the release slot to recognise it as
            // an RDT release target — without it the pair is silently
            // ignored.
            if (releaseCap is not null && releaseCap.Upstroke < 1.5)
            {
                releaseCap.Upstroke = 1.5;
            }

            // Gaming-optimal AP default — RDT pairs are almost always used
            // for fast-twitch combos (jump/crouch, peek/return). Factory
            // AP fires near bottom-out, which is sluggish; 0.5mm makes the
            // press fire the moment the cap moves. Only auto-tune when the
            // slot is still at a "high" AP — we treat anything > 1.0mm as
            // "presumably default, user hasn't deliberately tuned this for
            // gaming." Covers the known factory defaults: 2.0mm on newer
            // firmware (≥ 0.017), 3.3mm on older firmware (0.09). A user
            // who explicitly set AP to e.g. 0.8mm before pairing keeps it.
            if (pressCap is not null && pressCap.ActuationPoint > 1.0)
            {
                pressCap.ActuationPoint = 0.5;
            }
            if (releaseCap is not null && releaseCap.ActuationPoint > 1.0)
            {
                releaseCap.ActuationPoint = 0.5;
            }

            CancelRdtPick();
            PersistRdtPairs();
            RefreshRdtPairsBar();
            DebugLogger.Log($"KeyboardPerformanceView.HandleRdtPick: pair committed press={first} release={slot} totalPairs={_rdtPairs.Count}");
            ShowRdtDrawerForPair(first, slot);
            ScheduleAutoSync();
        }
    }

    private void CancelRdtPick()
    {
        _rdtPickState = RdtPickState.None;
        _rdtFirstPick = null;
        RdtPairsBar.SetPickStatus(null);
    }

    private void ShowRdtDrawerForPair(byte pressSlot, byte releaseSlot)
    {
        var pressLk = _activeLayoutFlat.FirstOrDefault(k => k.KeyIndex == pressSlot);
        var releaseLk = _activeLayoutFlat.FirstOrDefault(k => k.KeyIndex == releaseSlot);
        if (pressLk is null || releaseLk is null) return;

        // Per-pair AP/DS/US ride on the per-key Cap storage already used by
        // ActuationDrawer. The drawer's "shared AP" slider writes the same
        // value to both slots; DS writes to the press slot's Downstroke;
        // US writes to the release slot's Upstroke. This keeps the wire
        // path identical to the existing 9-packet per-key stream — no new
        // packet builder needed.
        // Cap lookup may miss for slots outside the visible layout (e.g.
        // legacy 126-slot mappings where some indexes don't have a visible
        // key). Skip when either side is absent — opening the drawer on an
        // unmapped pair would let the user edit invisible state.
        if (!_caps.TryGetValue(pressLk.Code, out var pressCap)) return;
        if (!_caps.TryGetValue(releaseLk.Code, out var releaseCap)) return;

        // Use the press key's AP as the shared starting value. If the two
        // diverged from earlier per-key edits, the user's first drag will
        // realign them.
        double sharedAp = pressCap.ActuationPoint > 0 ? pressCap.ActuationPoint : 2.0;
        double pressDs = pressCap.Downstroke;
        double releaseUs = releaseCap.Upstroke;

        RdtDrawer.SetPair(pressSlot, pressLk.Label, releaseSlot, releaseLk.Label,
            sharedAp, pressDs, releaseUs);
        // Hide the per-key actuation drawer while editing a pair — they
        // occupy the same slot in the layout and would visually overlap.
        Drawer.Visibility = Visibility.Collapsed;
        RdtDrawer.Visibility = Visibility.Visible;
    }

    private void HideRdtDrawer()
    {
        RdtDrawer.Visibility = Visibility.Collapsed;
        Drawer.Visibility = Visibility.Visible;
    }

    private void OnRdtDrawerApChanged(object? sender, double newAp)
    {
        ApplyRdtSliderEdit(RdtDrawer.PressSlot,   c => c.ActuationPoint = newAp);
        ApplyRdtSliderEdit(RdtDrawer.ReleaseSlot, c => c.ActuationPoint = newAp);
    }

    private void OnRdtDrawerDsChanged(object? sender, double newDs)
    {
        ApplyRdtSliderEdit(RdtDrawer.PressSlot, c => c.Downstroke = newDs);
    }

    private void OnRdtDrawerUsChanged(object? sender, double newUs)
    {
        ApplyRdtSliderEdit(RdtDrawer.ReleaseSlot, c => c.Upstroke = newUs);
    }

    // Apply a mutation to the Cap for `slot`, then schedule the same
    // write-back + auto-sync path the existing per-key drawer uses. The
    // KeyCap AP/DS/US DependencyProperty setters trigger UpdateVisuals
    // internally — no explicit refresh call needed.
    private void ApplyRdtSliderEdit(byte slot, Action<KeyCap> mutate)
    {
        var lk = _activeLayoutFlat.FirstOrDefault(k => k.KeyIndex == slot);
        if (lk is null) return;
        if (!_caps.TryGetValue(lk.Code, out var cap)) return;
        mutate(cap);
        UpdatePresetCounts();
        WriteBackToActiveProfile();
        ScheduleAutoSync();
    }

    private void OnSelectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // KeyCap's IsSelected state uses an expensive DropShadowEffect glow
        // (BlurRadius=18). Applying it to 90+ caps at once on Ctrl+A is a
        // software-blur stampede that makes the canvas crawl. Drop the glow
        // for multi-select; the accent border + tint still read clearly.
        bool wantGlow = _vm.SelectedKeys.Count <= 1;
        bool glowModeChanged = KeyCap.EnableSelectionGlow != wantGlow;
        KeyCap.EnableSelectionGlow = wantGlow;

        foreach (var (code, cap) in _caps)
        {
            bool wasSelected = cap.IsSelected;
            bool nowSelected = _vm.SelectedKeys.Contains(code);
            cap.IsSelected = nowSelected;
            // If the glow toggle just flipped, a cap that stayed selected
            // won't re-run UpdateVisuals (its IsSelected didn't change), so
            // it'd be stuck with the old effect. Force-refresh in that case.
            if (glowModeChanged && wasSelected && nowSelected) cap.RefreshVisuals();
        }

        if (!_suppressDrawerSync) SyncDrawerFromSelection();
        UpdateStatusText();
    }

    private void SyncDrawerFromSelection()
    {
        if (_vm.SelectedKeys.Count == 0)
        {
            Drawer.ClearSelection();
            return;
        }

        // Single pass over the selection — gathers everything the drawer needs
        // (uniform AP/DS/US detection, customized + RT counts, and small-set
        // subtitle list). The previous five LINQ passes were ~5× the work for
        // no good reason.
        KeyCap? first = null;
        bool apUniform = true, dsUniform = true, usUniform = true;
        int customized = 0;
        int rtCount = 0;
        int count = 0;
        List<string>? smallSubtitleCodes = null; // populated only for ≤12 caps
        foreach (var code in _vm.SelectedKeys)
        {
            if (!_caps.TryGetValue(code, out var cap)) continue;
            count++;
            if (first is null)
            {
                first = cap;
                smallSubtitleCodes = new List<string>(13);
            }
            else
            {
                if (apUniform && !NearlyEqual(cap.ActuationPoint, first.ActuationPoint)) apUniform = false;
                if (dsUniform && !NearlyEqual(cap.Downstroke,     first.Downstroke))     dsUniform = false;
                if (usUniform && !NearlyEqual(cap.Upstroke,       first.Upstroke))       usUniform = false;
            }
            if (!NearlyEqual(cap.ActuationPoint, 2.0) || cap.Downstroke > 0 || cap.Upstroke > 0) customized++;
            if (cap.Downstroke > 0 || cap.Upstroke > 0) rtCount++;
            if (smallSubtitleCodes is not null && smallSubtitleCodes.Count < 13)
                smallSubtitleCodes.Add(cap.Code);
        }
        if (first is null) return;

        double? ap = apUniform ? first.ActuationPoint : null;
        double? ds = dsUniform ? first.Downstroke     : null;
        double? us = usUniform ? first.Upstroke       : null;

        string title;
        string subtitle;
        if (count == 1)
        {
            var lk = _activeLayoutFlat.FirstOrDefault(k => k.Code == first.Code);
            title = $"Key · {lk?.Label ?? first.Code}";
            subtitle = first.Code;
        }
        else
        {
            title = $"{count} keys selected";
            // For small multi-selects show every code; for large ones just
            // summarize counts by category so the chip stays scannable.
            subtitle = count <= 12 && smallSubtitleCodes is not null
                ? string.Join(" · ", smallSubtitleCodes)
                : $"{customized} customized · {rtCount} with RT";
        }

        Drawer.SetState(title, subtitle, ap, ds, us);

        // Feed the rebind state into the drawer so its code-pill can render
        // "w → 3" and show the restore × button when applicable.
        if (count == 1)
        {
            var lk = _activeLayoutFlat.FirstOrDefault(k => k.Code == first.Code);
            int slot = lk?.KeyIndex ?? -1;
            byte remapped = (slot >= 0 && slot < 126) ? _remaps[slot] : (byte)0;
            string defaultLabel = lk?.Label ?? first.Code;
            string? remappedLabel = remapped != 0 ? RemapDrawer.HidUsageLabel(remapped) : null;
            Drawer.SetBinding(first.Code, defaultLabel, remapped, remappedLabel);
        }
        else
        {
            Drawer.SetBinding(null, "", 0, null);
        }
    }

    // The drawer captured a key — apply it as a remap on the currently-
    // selected slot. Mirrors the old RemapDrawer flow but works from the
    // Performance view's single right-hand drawer.
    private void OnDrawerRebindRequested(object? sender, DrawerRebindEventArgs e)
    {
        if (_vm.SelectedKeys.Count != 1) return;
        var code = _vm.SelectedKeys.First();
        var lk = _activeLayoutFlat.FirstOrDefault(k => k.Code == code);
        if (lk is null || lk.KeyIndex < 0 || lk.KeyIndex >= 126) return;

        // Re-mapping a key to its own default is a no-op: storing the same
        // HID usage as `_remaps[slot]` would mark the slot as remapped in
        // the UI ("caps → caps lock") but the wire format and runtime
        // behavior are identical to "no remap". Treat it as a Restore so
        // the user gets a clean "default" state instead of a redundant pill.
        var defaultUsage = KeyboardLayout.DefaultHidUsage(code);
        if (e.HidUsage == defaultUsage)
        {
            _remaps[lk.KeyIndex] = 0;
            if (_caps.TryGetValue(code, out var defCap))
                defCap.RemappedLabel = null;
            SyncDrawerFromSelection();
            ScheduleAutoSync();
            return;
        }

        _remaps[lk.KeyIndex] = e.HidUsage;
        if (_caps.TryGetValue(code, out var cap))
            cap.RemappedLabel = RemapDrawer.HidUsageLabel(e.HidUsage);
        SyncDrawerFromSelection();
        ScheduleAutoSync();
    }

    // × on the code pill — clear the remap.
    private void OnDrawerRestoreRequested(object? sender, EventArgs e)
    {
        if (_vm.SelectedKeys.Count != 1) return;
        var code = _vm.SelectedKeys.First();
        var lk = _activeLayoutFlat.FirstOrDefault(k => k.Code == code);
        if (lk is null || lk.KeyIndex < 0 || lk.KeyIndex >= 126) return;
        _remaps[lk.KeyIndex] = 0;
        if (_caps.TryGetValue(code, out var cap))
            cap.RemappedLabel = null;
        SyncDrawerFromSelection();
        ScheduleAutoSync();
    }

    private void OnDrawerActuationChanged(object? sender, ActuationChangedEventArgs e)
    {
        // Apply uniformly to every selected cap. Suppress the sync-back so
        // the drawer doesn't reset to "Mixed" mid-drag (every cap now has the
        // same uniform value, but the sync would re-read pre-drag state until
        // the next layout pass).
        _suppressDrawerSync = true;
        try
        {
            foreach (var code in _vm.SelectedKeys)
            {
                if (!_caps.TryGetValue(code, out var cap)) continue;
                cap.ActuationPoint = e.Ap;
                cap.Downstroke     = e.Ds;
                cap.Upstroke       = e.Us;
            }
        }
        finally
        {
            _suppressDrawerSync = false;
        }
        UpdatePresetCounts();
        ScheduleAutoSync();
    }

    private void UpdateStatusText()
    {
        var modelHint = _activeModel is not null
            ? $"{_activeModel.DisplayName} · "
            : "default layout · ";
        if (_vm.SelectedKeys.Count == 0)
        {
            StatusText.Text = modelHint + "Click a key to edit. Ctrl+click to add. Shift+click for range. Ctrl+A selects all. Esc clears.";
            return;
        }
        if (_vm.SelectedKeys.Count == 1)
        {
            var code = _vm.SelectedKeys.First();
            var lk = _activeLayoutFlat.FirstOrDefault(k => k.Code == code);
            StatusText.Text = lk is not null
                ? modelHint + $"Selected: {lk.Label} · slot {lk.KeyIndex} ({lk.ProfileKeyName})"
                : modelHint + $"Selected: {code}";
            return;
        }
        StatusText.Text = modelHint + $"{_vm.SelectedKeys.Count} keys selected";
    }

    // ---- Keyboard / canvas event hooks -------------------------------------

    // MouseDown on the canvas background (KeyCaps absorb their own clicks via
    // e.Handled, so we only fire here for empty space). Starts a drag-marquee.
    // No-modifier drag replaces selection (clears first). Ctrl-drag adds to it.
    // Alt-drag subtracts. Shift-drag is "replace without clearing" — useful for
    // power users who want to set a fresh selection without losing modifiers.
    private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var mods = KeyboardKey.Modifiers;
        _dragMode = (mods & ModifierKeys.Control) != 0
            ? MarqueeMode.Add
            : (mods & ModifierKeys.Alt) != 0
                ? MarqueeMode.Subtract
                : MarqueeMode.Replace;

        _dragStart = e.GetPosition(CanvasBorder);
        _isDragging = true;

        // Snapshot cap positions once at drag start — reused by every Move /
        // Up tick instead of re-walking the visual tree per cap per frame.
        var rects = new List<(KeyCap, string, Rect)>(_caps.Count);
        foreach (var (code, cap) in _caps)
        {
            if (!cap.IsLoaded || cap.ActualWidth == 0) continue;
            var r = cap.TransformToVisual(CanvasBorder)
                       .TransformBounds(new Rect(0, 0, cap.ActualWidth, cap.ActualHeight));
            rects.Add((cap, code, r));
        }
        _dragRects = rects.ToArray();

        // Plain replace-mode drag clears the existing selection up front so
        // the marquee result is the entire selection at commit time. Shift+
        // drag preserves the existing selection (intersects with marquee).
        if (_dragMode == MarqueeMode.Replace && (mods & ModifierKeys.Shift) == 0)
            _vm.ClearSelection();

        Mouse.Capture(CanvasBorder, CaptureMode.SubTree);
        UpdateMarqueeRect();
        MarqueeRect.Visibility = Visibility.Visible;
        e.Handled = true;
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging) return;
        UpdateMarqueeRect();
        UpdateMarqueePreviewState();
    }

    private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging) return;
        CommitMarquee();
        EndDrag();
        e.Handled = true;
    }

    private void Canvas_LostMouseCapture(object sender, MouseEventArgs e)
    {
        // Triggered on Esc-abort (we Mouse.Capture(null)) or focus loss.
        // Clear preview state without committing.
        if (!_isDragging) return;
        foreach (var cap in _caps.Values) cap.IsMarqueePreview = false;
        _isDragging = false;
        _dragRects = null;
        MarqueeRect.Visibility = Visibility.Collapsed;
        MarqueeRect.Width = 0;
        MarqueeRect.Height = 0;
    }

    private void UpdateMarqueeRect()
    {
        var current = Mouse.GetPosition(CanvasBorder);
        var x = Math.Min(_dragStart.X, current.X);
        var y = Math.Min(_dragStart.Y, current.Y);
        var w = Math.Abs(current.X - _dragStart.X);
        var h = Math.Abs(current.Y - _dragStart.Y);
        Canvas.SetLeft(MarqueeRect, x);
        Canvas.SetTop(MarqueeRect, y);
        MarqueeRect.Width = w;
        MarqueeRect.Height = h;
    }

    private void UpdateMarqueePreviewState()
    {
        if (_dragRects is null) return;
        var rect = MarqueeRectInCanvas();
        for (int i = 0; i < _dragRects.Length; i++)
        {
            var (cap, code, capRect) = _dragRects[i];
            var intersects = rect.IntersectsWith(capRect);
            cap.IsMarqueePreview = _dragMode switch
            {
                MarqueeMode.Replace  => intersects,
                MarqueeMode.Add      => intersects,
                MarqueeMode.Subtract => intersects && _vm.SelectedKeys.Contains(code),
                _ => false,
            };
        }
    }

    private void CommitMarquee()
    {
        var rect = MarqueeRectInCanvas();
        var intersected = new List<string>();
        if (_dragRects is not null)
        {
            for (int i = 0; i < _dragRects.Length; i++)
            {
                var (cap, code, capRect) = _dragRects[i];
                if (rect.IntersectsWith(capRect)) intersected.Add(code);
                cap.IsMarqueePreview = false;
            }
        }

        switch (_dragMode)
        {
            case MarqueeMode.Replace:
                _vm.SelectedKeys.ReplaceAll(intersected);
                break;
            case MarqueeMode.Add:
                _vm.SelectedKeys.UnionWith(intersected);
                break;
            case MarqueeMode.Subtract:
                _vm.SelectedKeys.ExceptWith(intersected);
                break;
        }
    }

    private void EndDrag()
    {
        _isDragging = false;
        _dragRects = null;
        MarqueeRect.Visibility = Visibility.Collapsed;
        MarqueeRect.Width = 0;
        MarqueeRect.Height = 0;
        if (Mouse.Captured == CanvasBorder) CanvasBorder.ReleaseMouseCapture();
    }

    private Rect MarqueeRectInCanvas() => new(
        Canvas.GetLeft(MarqueeRect),
        Canvas.GetTop(MarqueeRect),
        MarqueeRect.Width,
        MarqueeRect.Height);

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Remap-capture mode swallows the keypress before any of the canvas
        // shortcuts (Esc/Ctrl+A) run — otherwise we couldn't rebind to those.
        if (Drawer.TryHandleCaptureKey(e))
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            if (_isDragging)
            {
                // Abort drag without committing — LostMouseCapture handles the
                // cleanup once Mouse.Capture(null) lands.
                Mouse.Capture(null);
                e.Handled = true;
                return;
            }
            if (_lwPickState != LwPickState.None)
            {
                CancelLwPick();
                e.Handled = true;
                return;
            }
            if (_rdtPickState != RdtPickState.None)
            {
                CancelRdtPick();
                e.Handled = true;
                return;
            }
            if (RdtDrawer.Visibility == Visibility.Visible)
            {
                HideRdtDrawer();
                e.Handled = true;
                return;
            }
            _vm.ClearSelection();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.A && (KeyboardKey.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            // Select every visible cap. ReplaceAll fires a single CollectionChanged
            // (no event storm even with 90+ entries).
            _vm.SelectedKeys.ReplaceAll(_caps.Keys);
            e.Handled = true;
        }
    }

    // ---- Firmware sync (Phase C — unchanged) -------------------------------

    private async void SyncButton_Click(object sender, RoutedEventArgs e)
    {
        _autoSyncTimer.Stop();
        await DoSyncAsync(manual: true).ConfigureAwait(true);
    }

    private void DownloadsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://drunkdeer.com/pages/downloads",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Couldn't open browser: {ex.Message}";
        }
    }

    private void OpenLogButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = DebugLogger.LogPath;
            // /select, opens Explorer with the log file pre-selected so the
            // user can drag it directly out.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Couldn't open log folder: {ex.Message}";
        }
    }

    // Shared sync path used by both the manual button and the auto-sync
    // debounce tick. After the profile-aware refactor, this is a thin wrapper
    // around ProfileManager.PushCurrentProfileAsync — the view no longer
    // builds its own packet stream. ScheduleAutoSync has already mirrored the
    // working state onto the active ProfileItem, so the push reads the
    // canonical data and writes the exact same bytes a SwitchTo would.
    private async Task DoSyncAsync(bool manual)
    {
        if (_syncing) return;
        if (_profileManager is null)
        {
            if (manual) StatusText.Text = "Sync unavailable — ProfileManager not attached (running standalone debug build).";
            return;
        }
        if (_keyboardManager?.KeyboardWithSpecs is null)
        {
            if (manual) StatusText.Text = "No keyboard connected. Plug in your A75 Pro and try again.";
            return;
        }

        _syncing = true;
        SyncButton.IsEnabled = false;
        StatusText.Text = manual ? "Syncing…" : "Auto-syncing…";

        try
        {
            // Defensive: if a handler bypassed ScheduleAutoSync, write back now
            // before the push so the firmware doesn't see a stale profile.
            WriteBackToActiveProfile();

            // PushCurrentProfileAsync internally emits a 600ms commit
            // re-push when the bundle carries Type-4 pair entries (LW/RDT).
            // Don't double it here — that previously lived in this method
            // only, which left profile switches via SwitchTo without the
            // commit push and required a manual Sync click to activate RDT.
            var result = await _profileManager.PushCurrentProfileAsync().ConfigureAwait(true);

            StatusText.Text = result switch
            {
                { Ok: true } => $"Synced {result.PacketCount} packets to keyboard.",
                { Disconnected: true } => "Keyboard not reachable — plug it back in and try again.",
                { Superseded: true } => "Sync replaced by a newer change.",
                _ => "Sync partially failed — check debug.log for details.",
            };
        }
        finally
        {
            _syncing = false;
            SyncButton.IsEnabled = true;
        }
    }

    private static bool NearlyEqual(double a, double b) => Math.Abs(a - b) < 0.001;
}
