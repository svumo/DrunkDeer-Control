using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Driver;
using HidSharp;
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

// Interactive testbed for the keyboard view rebuild.
//   Phase A: static render of A75 Pro layout.
//   Phase B: single-key click selection + live ActuationDrawer.
//   Phase C: Sync to Keyboard pushes per-key AP/DS/US to firmware.
//   Phase D (this rev): multi-select via KeyboardCanvasViewModel.
//     - Click: replace selection
//     - Ctrl+click: toggle
//     - Shift+click: range from anchor (row-major)
//     - Ctrl+A: select all
//     - Esc / background-click: clear
//     Drawer adapts to single vs multi (showing "N keys selected" and
//     "Mixed" markers when values differ across the selection). Drawer
//     edits apply uniformly to every selected cap.
//
// Launched via the --keyboard-debug CLI flag.
public partial class KeyboardDebugWindow : Window
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

    // True while ApplyDrawerEdit is running so SyncDrawerFromSelection
    // doesn't re-clobber the drawer with stale state during edits.
    private bool _suppressDrawerSync;

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

    public KeyboardDebugWindow() : this(null) { }

    public KeyboardDebugWindow(KeyboardManager? keyboardManager)
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
        var modelLabel = _activeModel?.DisplayName ?? "A75 Pro (default)";
        Title = $"Keyboard Debug — {modelLabel}";
        var fw = keyboardManager?.KeyboardWithSpecs?.Specs.FirmwareVersion;
        var fwText = !string.IsNullOrEmpty(fw) ? $" · firmware v{fw}" : "";
        HeaderTitle.Text = keyboardManager?.KeyboardWithSpecs is not null
            ? $"DrunkDeer {modelLabel}{fwText} · click any key to edit"
            : $"DrunkDeer {modelLabel} · no keyboard connected (changes won't sync)";

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
        QuickSelectBar.PillClicked += OnQuickSelectPill;
        PresetBar.PresetClicked += OnPresetClicked;
        UpdateStatusText();
        UpdatePresetCounts();
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

    // (Ap, Ds, Us) tuple applied per matched key. For each preset, define
    // the natural target set; the parent dispatcher intersects with the
    // active selection if there is one.
    private static readonly Dictionary<string, (double Ap, double Ds, double Us)> FpsPreset = new()
    {
        ["w"]     = (0.5, 0.4, 0.4),
        ["a"]     = (0.5, 0.4, 0.4),
        ["s"]     = (0.5, 0.4, 0.4),
        ["d"]     = (0.5, 0.4, 0.4),
        ["space"] = (1.0, 0.0, 0.0),
    };

    private void OnPresetClicked(object? sender, PresetClickedEventArgs e)
    {
        Dictionary<string, (double Ap, double Ds, double Us)> apply;
        switch (e.Preset)
        {
            case "fps":
                apply = new Dictionary<string, (double, double, double)>(FpsPreset);
                break;
            case "typing":
                // All letter keys deep, no RT. Iterate the layout so this works
                // on every model.
                apply = _activeLayoutFlat
                    .Where(k => k.Code.Length == 1 && k.Code[0] >= 'a' && k.Code[0] <= 'z')
                    .ToDictionary(k => k.Code, _ => (Ap: 3.0, Ds: 0.0, Us: 0.0));
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
        StatusText.Text = $"Applied preset '{e.Preset}' to {touched} key{(touched == 1 ? "" : "s")}.";
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

        // UI-enforced conflict from the official driver: Turbo and Keystroke
        // Tracking are mutually exclusive. Mirror it here so the firmware
        // never sees an invalid combination on Sync.
        if (e.Mode == "turbo" && e.Value && _modeSettings.KeystrokeTrackingEnabled)
        {
            _modeSettings.KeystrokeTrackingEnabled = false;
            ModeStrip.KeystrokeTrackingEnabled = false;
        }
        else if (e.Mode == "keystroke" && e.Value && _modeSettings.TurboEnabled)
        {
            _modeSettings.TurboEnabled = false;
            ModeStrip.TurboEnabled = false;
        }
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
        SyncDrawerFromSelection();
        StatusText.Text = "All keys reset to default. Click Sync to Keyboard to push.";
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

        if (e.Shift)      _vm.SelectRange(e.Code);
        else if (e.Ctrl)  _vm.ToggleSelection(e.Code);
        else              _vm.Select(e.Code);
    }

    private void OnSelectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Flip IsSelected on every cap to match the VM. Cheap (≤ 91 caps).
        foreach (var (code, cap) in _caps)
            cap.IsSelected = _vm.SelectedKeys.Contains(code);

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

        var selectedCaps = _vm.SelectedKeys
            .Select(c => _caps.TryGetValue(c, out var cap) ? cap : null)
            .Where(c => c is not null)
            .Cast<KeyCap>()
            .ToList();
        if (selectedCaps.Count == 0) return;

        var first = selectedCaps[0];
        double? ap = selectedCaps.All(c => NearlyEqual(c.ActuationPoint, first.ActuationPoint)) ? first.ActuationPoint : null;
        double? ds = selectedCaps.All(c => NearlyEqual(c.Downstroke,     first.Downstroke))     ? first.Downstroke     : null;
        double? us = selectedCaps.All(c => NearlyEqual(c.Upstroke,       first.Upstroke))       ? first.Upstroke       : null;

        string title;
        string subtitle;
        if (selectedCaps.Count == 1)
        {
            var lk = _activeLayoutFlat.FirstOrDefault(k => k.Code == first.Code);
            title = $"Key · {lk?.Label ?? first.Code}";
            subtitle = first.Code;
        }
        else
        {
            title = $"{selectedCaps.Count} keys selected";
            // Compact chip list; truncate after a handful so the drawer header
            // doesn't span half the window.
            var codes = selectedCaps.Take(8).Select(c => c.Code);
            subtitle = string.Join(" · ", codes);
            if (selectedCaps.Count > 8) subtitle += $" +{selectedCaps.Count - 8}";
        }

        Drawer.SetState(title, subtitle, ap, ds, us);
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
        var rect = MarqueeRectInCanvas();
        foreach (var (code, cap) in _caps)
        {
            if (!cap.IsLoaded || cap.ActualWidth == 0)
            {
                cap.IsMarqueePreview = false;
                continue;
            }
            var capRect = cap.TransformToVisual(CanvasBorder)
                             .TransformBounds(new Rect(0, 0, cap.ActualWidth, cap.ActualHeight));
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
        foreach (var (code, cap) in _caps)
        {
            if (!cap.IsLoaded || cap.ActualWidth == 0) continue;
            var capRect = cap.TransformToVisual(CanvasBorder)
                             .TransformBounds(new Rect(0, 0, cap.ActualWidth, cap.ActualHeight));
            if (rect.IntersectsWith(capRect)) intersected.Add(code);
            cap.IsMarqueePreview = false;
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
        if (_syncing) return;
        if (_keyboardManager is null)
        {
            StatusText.Text = "Sync unavailable — KeyboardManager not injected (running standalone debug build).";
            return;
        }

        if (_keyboardManager.KeyboardWithSpecs is not { } keyboard)
        {
            StatusText.Text = "No keyboard connected. Plug in your A75 Pro and try again.";
            return;
        }

        _syncing = true;
        SyncButton.IsEnabled = false;
        StatusText.Text = "Syncing…";

        try
        {
            var keysArray = new KeySetting[126];
            for (int i = 0; i < 126; i++) keysArray[i] = new KeySetting { Action_Point = 2.0m };

            foreach (var lk in _activeLayoutFlat)
            {
                if (!_caps.TryGetValue(lk.Code, out var cap)) continue;
                if (lk.KeyIndex < 0 || lk.KeyIndex >= 126) continue;
                keysArray[lk.KeyIndex] = new KeySetting
                {
                    KeyName = lk.ProfileKeyName,
                    Action_Point = (decimal)cap.ActuationPoint,
                    Downstroke   = (decimal)cap.Downstroke,
                    Upstroke     = (decimal)cap.Upstroke,
                };
            }

            var profile = new DriverProfile { Keys_Array = keysArray, Settings = _modeSettings };

            // 9 per-key AP/DS/US packets + 1 common-switch packet carrying
            // Turbo / RT / LW+RDT / RTMatch from ModeStrip, plus the three
            // outlier global-toggle packets (Keystroke Tracking, Last Win
            // Replace, Auto-Match Mode) which live outside Common Switch.
            var packets = new[]
            {
                profile.BuildPacketKeyPoint(0, Packets.KeyPointType.ActuationPoint),
                profile.BuildPacketKeyPoint(1, Packets.KeyPointType.ActuationPoint),
                profile.BuildPacketKeyPoint(2, Packets.KeyPointType.ActuationPoint),
                profile.BuildPacketKeyPoint(0, Packets.KeyPointType.Downstroke),
                profile.BuildPacketKeyPoint(1, Packets.KeyPointType.Downstroke),
                profile.BuildPacketKeyPoint(2, Packets.KeyPointType.Downstroke),
                profile.BuildPacketKeyPoint(0, Packets.KeyPointType.Upstroke),
                profile.BuildPacketKeyPoint(1, Packets.KeyPointType.Upstroke),
                profile.BuildPacketKeyPoint(2, Packets.KeyPointType.Upstroke),
                Packets.BuildCommonSwitchPacket(_modeSettings),
                Packets.BuildKeystrokeTrackingPacket(_modeSettings.KeystrokeTrackingEnabled),
                Packets.BuildLastWinReplacePacket(_modeSettings.LastWinReplaceEnabled),
                Packets.BuildAutoMatchModePacket(_modeSettings.AutoMatchMode),
            };

            DebugLogger.Log($"KeyboardDebugWindow.Sync: writing {packets.Length} packets to PID=0x{keyboard.Keyboard.ProductID:x4} (Turbo={_modeSettings.TurboEnabled} RT={_modeSettings.RapidTriggerEnabled} LW={_modeSettings.LastWinEnabled} RDT={_modeSettings.ReleaseDualTriggerEnabled} RTMatch={_modeSettings.RTMatchEnabled} KeystrokeTracking={_modeSettings.KeystrokeTrackingEnabled} LWReplace={_modeSettings.LastWinReplaceEnabled} AutoMatch={_modeSettings.AutoMatchMode})");

            var ok = await Task.Run(() =>
            {
                try
                {
                    using HidStream stream = keyboard.Keyboard.Open();
                    return stream.WritePacket(packets);
                }
                catch (Exception ex)
                {
                    DebugLogger.Log($"KeyboardDebugWindow.Sync: exception — {ex.GetType().Name}: {ex.Message}");
                    return false;
                }
            }).ConfigureAwait(true);

            StatusText.Text = ok
                ? $"Synced {packets.Length} packets to {keyboard.Specs.KeyboardType?.ToString() ?? "keyboard"}."
                : "Sync partially failed — check debug.log for details.";
        }
        finally
        {
            _syncing = false;
            SyncButton.IsEnabled = true;
        }
    }

    private static bool NearlyEqual(double a, double b) => Math.Abs(a - b) < 0.001;
}
