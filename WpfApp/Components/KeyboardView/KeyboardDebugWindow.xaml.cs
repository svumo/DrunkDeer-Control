using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using Driver;
using HidSharp;
using Orientation = System.Windows.Controls.Orientation;
using StackPanel = System.Windows.Controls.StackPanel;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Key = System.Windows.Input.Key;
// 'Profile' as an unqualified name collides with the WpfApp.Profile namespace.
using DriverProfile = Driver.Profile;

namespace WpfApp.Components.KeyboardView;

// Interactive testbed for the keyboard view rebuild. Phase A rendered the
// layout statically; Phase B (this rev) wires single-key click selection +
// live ActuationDrawer updates. Selection state is held on the window itself
// rather than in a separate VM — multi-select (Phase D) will need an
// ObservableSet-driven VM, but for single-select that's overkill.
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
    private KeyCap? _selectedCap;
    private readonly KeyboardManager? _keyboardManager;
    private bool _syncing;

    public KeyboardDebugWindow() : this(null) { }

    public KeyboardDebugWindow(KeyboardManager? keyboardManager)
    {
        _keyboardManager = keyboardManager;
        InitializeComponent();
        BuildRows();
        Drawer.ActuationChanged += OnDrawerActuationChanged;
        Drawer.ClearSelection();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void BuildRows()
    {
        for (int rowIndex = 0; rowIndex < KeyboardLayout.A75Pro.Count; rowIndex++)
        {
            var row = KeyboardLayout.A75Pro[rowIndex];
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

    private void OnCapClick(object? sender, KeyCapClickEventArgs e)
    {
        if (!_caps.TryGetValue(e.Code, out var cap)) return;
        SelectCap(cap);
    }

    private void SelectCap(KeyCap cap)
    {
        if (_selectedCap == cap) return;

        if (_selectedCap is not null) _selectedCap.IsSelected = false;
        _selectedCap = cap;
        cap.IsSelected = true;

        var layoutKey = KeyboardLayout.FindByCode(cap.Code);
        var label = layoutKey?.Label ?? cap.Code;
        Drawer.SetKey(cap.Code, label, cap.ActuationPoint, cap.Downstroke, cap.Upstroke);

        StatusText.Text = $"Selected: {label}" + (layoutKey?.Uncertain == true
            ? $" · slot {layoutKey.KeyIndex} ({layoutKey.ProfileKeyName}) is unverified"
            : layoutKey is not null ? $" · slot {layoutKey.KeyIndex} ({layoutKey.ProfileKeyName})" : string.Empty);
    }

    private void ClearSelection()
    {
        if (_selectedCap is null) return;
        _selectedCap.IsSelected = false;
        _selectedCap = null;
        Drawer.ClearSelection();
        StatusText.Text = "Click a key to edit.";
    }

    private void OnDrawerActuationChanged(object? sender, ActuationChangedEventArgs e)
    {
        if (_selectedCap is null || _selectedCap.Code != e.Code) return;
        _selectedCap.ActuationPoint = e.Ap;
        _selectedCap.Downstroke = e.Ds;
        _selectedCap.Upstroke = e.Us;
    }

    // Click on the canvas background (not a cap) clears selection — same
    // behavior we'll want for marquee-start in Phase E.
    private void Canvas_BackgroundClick(object sender, MouseButtonEventArgs e)
    {
        if (e.Source == sender) ClearSelection();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { ClearSelection(); e.Handled = true; }
    }

    // Sync the current cap state to the real keyboard. Phase C scope: per-key
    // AP/DS/US only (9 packets via BuildPacketKeyPoint). The common-switch
    // packet (Turbo/RT/LW/RDT/RTMatch) is built but uses default ProfileSettings
    // (all flags off) until ModeStrip integration lands in Phase G.
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
        var originalStatus = StatusText.Text;
        StatusText.Text = "Syncing…";

        try
        {
            // Build a transient Profile from current KeyCap state. Keys_Array
            // is the firmware's fixed 126-slot grid; we fill the slots we know
            // about from KeyboardLayout and leave the rest at default (AP=2.0).
            var keysArray = new KeySetting[126];
            for (int i = 0; i < 126; i++) keysArray[i] = new KeySetting { Action_Point = 2.0m };

            foreach (var lk in KeyboardLayout.A75ProFlat)
            {
                if (!_caps.TryGetValue(lk.Code, out var cap)) continue;
                if (lk.KeyIndex < 0 || lk.KeyIndex >= 126) continue;
                keysArray[lk.KeyIndex] = new KeySetting
                {
                    KeyName = lk.ProfileKeyName,
                    Action_Point = (decimal)cap.ActuationPoint,
                    Downstroke = (decimal)cap.Downstroke,
                    Upstroke = (decimal)cap.Upstroke,
                };
            }

            var profile = new DriverProfile { Keys_Array = keysArray };

            // 9 per-key packets — 3 each for AP, DS, US, covering all 126 slots
            // in groups of (59, 59, 8).
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
}
