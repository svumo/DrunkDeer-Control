using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Driver;
using HidSharp;
using WpfApp.ViewModels;
using Orientation = System.Windows.Controls.Orientation;
using StackPanel = System.Windows.Controls.StackPanel;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Key = System.Windows.Input.Key;
using KeyboardKey = System.Windows.Input.Keyboard;
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

    // True while ApplyDrawerEdit is running so SyncDrawerFromSelection
    // doesn't re-clobber the drawer with stale state during edits.
    private bool _suppressDrawerSync;

    public KeyboardDebugWindow() : this(null) { }

    public KeyboardDebugWindow(KeyboardManager? keyboardManager)
    {
        _keyboardManager = keyboardManager;
        _activeModel = KeyboardLayoutResolver.Resolve(keyboardManager?.KeyboardWithSpecs);
        _activeLayout = KeyboardLayout.VisualFor(_activeModel) ?? KeyboardLayout.A75Pro;
        _activeLayoutFlat = KeyboardLayout.VisualFlatFor(_activeModel) ?? KeyboardLayout.A75ProFlat;

        InitializeComponent();
        Title = $"Keyboard Debug — {_activeModel?.DisplayName ?? "A75 Pro (default)"}";
        BuildRows();
        Drawer.ActuationChanged += OnDrawerActuationChanged;
        Drawer.ClearSelection();
        PreviewKeyDown += OnPreviewKeyDown;
        _vm.SelectedKeys.CollectionChanged += OnSelectionChanged;
        UpdateStatusText();
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

    private void Canvas_BackgroundClick(object sender, MouseButtonEventArgs e)
    {
        if (e.Source == sender) _vm.ClearSelection();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _vm.ClearSelection();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.A && (KeyboardKey.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            // Select all visible caps (not the empty firmware slots).
            _vm.ClearSelection();
            foreach (var code in _caps.Keys) _vm.AddToSelection(code);
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

            var profile = new DriverProfile { Keys_Array = keysArray };

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
            };

            DebugLogger.Log($"KeyboardDebugWindow.Sync: writing {packets.Length} per-key packets to PID=0x{keyboard.Keyboard.ProductID:x4}");

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
