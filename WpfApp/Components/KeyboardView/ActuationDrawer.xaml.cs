using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls.Primitives;
using Brush = System.Windows.Media.Brush;
using Key = System.Windows.Input.Key;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using ModifierKeys = System.Windows.Input.ModifierKeys;
using UserControl = System.Windows.Controls.UserControl;

namespace WpfApp.Components.KeyboardView;

// Right-side drawer that edits one or more keys' actuation settings.
//
// Phase B established the single-key path; Phase D widens it to multi-key:
//   - Title reflects the count ("Key · W" vs "12 keys selected")
//   - Sliders accept a nullable value — null means "selected keys have mixed
//     values for this field, slider sits at average, label says 'Mixed'"
//   - First user touch on a slider commits a uniform value to every selected
//     key (no per-key memory in the multi-key path).
//   - RT toggle is tri-state when DS/US are mixed.
//
// Parent flow:
//   1. Selection changes in the canvas → parent calls SetState(...) to
//      populate the drawer.
//   2. User drags sliders / toggles RT → drawer fires ActuationChanged with the
//      new (AP, DS, US) tuple. Parent applies that uniformly to every
//      currently-selected KeyCap.
//   3. No Apply/Cancel buttons — edits are live. The "Sync to Keyboard" button
//      on the canvas pushes to firmware (Phase C).
public partial class ActuationDrawer : UserControl
{
    private const double DefaultAp = 2.0;
    private const double DefaultRtDs = 0.5;
    private const double DefaultRtUs = 0.5;

    private bool _suppressEvents; // guard against feedback loops when programmatically setting values
    private bool _hasSelection;

    // Remap capture state. We absorb the Remap tab's flow into this drawer:
    // clicking the code pill enters capture mode; the next key press becomes
    // the new binding. Esc cancels. Multi-select disables capture.
    private bool _capturing;
    private bool _singleSelect;
    private string? _selectedCode;     // the layout code of the currently-selected key
    private string _defaultLabel = ""; // label shown when no remap (e.g. "w")
    private byte _remappedHidUsage;    // 0 = no remap

    public ActuationDrawer()
    {
        InitializeComponent();
        // Binary toggle. Mixed-across-selection is communicated via the
        // "Mixed" label on DS/US, not by tri-state — tri-state's silent
        // null transition (Indeterminate event isn't wired) was confusing.
        RtToggle.IsThreeState = false;
    }

    /// <summary>
    /// Adjusts the AP / DS / US slider ranges to match the connected
    /// firmware's wire dialect. Called by the parent
    /// (KeyboardPerformanceView) whenever capabilities resolve. Defaults
    /// to OldHighPrec ranges (the conservative A75 Pro ceiling).
    ///
    /// Per the JS bundle the matrix is:
    ///   Legacy      (G65/G60 family):   AP 0.2–3.3 mm, DS/US 0.1–3.1 mm (0.1 mm step)
    ///   OldHighPrec (A75 Pro / Kun):    AP 0.2–2.0 mm, DS/US 0.01–2.0 mm (0.01 mm step)
    ///   NewHighPrec (A75 Ultra/Master): AP 0.2–3.3 mm, DS/US 0.01–2.0 mm (0.01 mm step)
    ///
    /// The Kun-switch RT precision (0.01 mm) is a 2026-05-26 fix —
    /// previously the DS/US slider min was hardcoded at 0.1 mm in XAML,
    /// which capped Kun firmware users at 10x coarser RT thresholds than
    /// the firmware actually supports.
    /// </summary>
    public void SetSliderRanges(double apMaxMm, double dsUsMaxMm, double dsUsMinMm = 0.01)
    {
        ApSlider.Maximum = apMaxMm;
        DsSlider.Maximum = dsUsMaxMm;
        UsSlider.Maximum = dsUsMaxMm;
        DsSlider.Minimum = dsUsMinMm;
        UsSlider.Minimum = dsUsMinMm;
        // Keyboard arrow-key SmallChange should match the slider's
        // resolution so users on Kun precision can nudge by 0.01 mm.
        DsSlider.SmallChange = dsUsMinMm;
        UsSlider.SmallChange = dsUsMinMm;
    }

    /// <summary>Fired whenever any slider or RT-toggle changes the values.
    /// Code is intentionally absent — the parent knows what's selected.</summary>
    public event EventHandler<ActuationChangedEventArgs>? ActuationChanged;

    /// <summary>Captured key — parent applies as a remap on the selected slot.</summary>
    public event EventHandler<DrawerRebindEventArgs>? RebindRequested;
    /// <summary>User clicked × — parent clears the remap for the selected slot.</summary>
    public event EventHandler? RestoreRequested;

    /// <summary>Tells the drawer about the currently-selected slot's remap
    /// state so the pill can render "w → 3" + restore button. Pass
    /// `selectedCode` = null when the selection is mixed (multi-select).</summary>
    public void SetBinding(string? selectedCode, string defaultLabel, byte remappedHidUsage, string? remappedLabel)
    {
        _singleSelect = selectedCode != null;
        _selectedCode = selectedCode;
        _defaultLabel = defaultLabel;
        _remappedHidUsage = remappedHidUsage;

        if (!_singleSelect)
        {
            // Multi-select: hide rebind affordances entirely. Remap is a
            // single-key operation.
            CodePill.Cursor = null;
            CodePill.ToolTip = null;
            RestoreButton.Visibility = Visibility.Collapsed;
            RemapHint.Visibility = Visibility.Collapsed;
            EndCapture();
            return;
        }

        CodePill.Cursor = System.Windows.Input.Cursors.Hand;
        CodePill.ToolTip = "Click to remap. Press any key (or Esc to cancel).";
        RemapHint.Visibility = Visibility.Visible;

        if (remappedHidUsage != 0 && remappedLabel != null)
        {
            CodeText.Text = $"{defaultLabel} → {remappedLabel}";
            RestoreButton.Visibility = Visibility.Visible;
            RemapHint.Text = "Click the chip to change. × to restore default.";
        }
        else
        {
            CodeText.Text = defaultLabel;
            RestoreButton.Visibility = Visibility.Collapsed;
            RemapHint.Text = "Click the chip to remap this key.";
        }
    }

    // Raised when the drawer enters / leaves rebind capture. The host needs
    // these to suspend global hotkeys (RegisterHotKey eats keystrokes before
    // WPF sees them — so a profile with `]` as its direct-switch hotkey makes
    // `]` unbindable in remap capture).
    public event EventHandler? CaptureStarted;
    public event EventHandler? CaptureEnded;

    private void BeginCapture()
    {
        if (!_singleSelect) return;
        _capturing = true;
        CodeText.Text = "Press any key…";
        CodeText.Foreground = (Brush)FindResource("DdAccent");
        RestoreButton.Visibility = Visibility.Collapsed;
        RemapHint.Text = "Esc to cancel.";
        Focus();
        System.Windows.Input.Keyboard.Focus(this);
        CaptureStarted?.Invoke(this, EventArgs.Empty);
    }

    private void EndCapture()
    {
        if (!_capturing) return;
        _capturing = false;
        CodeText.Foreground = (Brush)FindResource("DdFg2");
        // Re-render the binding label without firing events.
        if (_singleSelect && _selectedCode != null)
            CodeText.Text = _remappedHidUsage != 0
                ? $"{_defaultLabel} → 0x{_remappedHidUsage:X2}"
                : _defaultLabel;
        CaptureEnded?.Invoke(this, EventArgs.Empty);
    }

    // Parent forwards PreviewKeyDown here while the drawer is capturing.
    // Returns true if the event was consumed.
    public bool TryHandleCaptureKey(KeyEventArgs e)
    {
        if (!_capturing) return false;
        if (e.Key == Key.Escape)
        {
            EndCapture();
            return true;
        }
        if (IsPureModifier(e.Key)) return true; // wait for the real key

        var hid = WpfKeyToHidUsage(e.Key);
        if (hid == 0) { EndCapture(); return true; }

        _capturing = false;
        CodeText.Foreground = (Brush)FindResource("DdFg2");
        RebindRequested?.Invoke(this, new DrawerRebindEventArgs(hid));
        return true;
    }

    private void CodePill_Click(object sender, RoutedEventArgs e)
    {
        if (!_singleSelect) return;
        if (_capturing) EndCapture();
        else BeginCapture();
    }

    private void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        EndCapture();
        RestoreRequested?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsPureModifier(Key k) =>
        k is Key.LeftShift or Key.RightShift
          or Key.LeftCtrl or Key.RightCtrl
          or Key.LeftAlt or Key.RightAlt
          or Key.LWin or Key.RWin
          or Key.System;

    // Same mapping as the old RemapDrawer.
    private static byte WpfKeyToHidUsage(Key key) =>
        key switch
        {
            >= Key.A and <= Key.Z => (byte)(0x04 + (key - Key.A)),
            Key.D1 => 0x1E, Key.D2 => 0x1F, Key.D3 => 0x20, Key.D4 => 0x21, Key.D5 => 0x22,
            Key.D6 => 0x23, Key.D7 => 0x24, Key.D8 => 0x25, Key.D9 => 0x26, Key.D0 => 0x27,
            Key.Enter => 0x28, Key.Escape => 0x29, Key.Back => 0x2A, Key.Tab => 0x2B,
            Key.Space => 0x2C, Key.OemMinus => 0x2D, Key.OemPlus => 0x2E,
            Key.OemOpenBrackets => 0x2F, Key.Oem6 => 0x30, Key.Oem5 => 0x31,
            Key.OemSemicolon => 0x33, Key.OemQuotes => 0x34,
            Key.Oem3 => 0x35, Key.OemComma => 0x36, Key.OemPeriod => 0x37, Key.OemQuestion => 0x38,
            Key.CapsLock => 0x39,
            Key.F1 => 0x3A, Key.F2 => 0x3B, Key.F3 => 0x3C, Key.F4 => 0x3D, Key.F5 => 0x3E,
            Key.F6 => 0x3F, Key.F7 => 0x40, Key.F8 => 0x41, Key.F9 => 0x42, Key.F10 => 0x43,
            Key.F11 => 0x44, Key.F12 => 0x45,
            Key.PrintScreen => 0x46, Key.Scroll => 0x47, Key.Pause => 0x48,
            Key.Insert => 0x49, Key.Home => 0x4A, Key.PageUp => 0x4B,
            Key.Delete => 0x4C, Key.End => 0x4D, Key.PageDown => 0x4E,
            Key.Right => 0x4F, Key.Left => 0x50, Key.Down => 0x51, Key.Up => 0x52,
            Key.LeftCtrl => 0xE0, Key.LeftShift => 0xE1, Key.LeftAlt => 0xE2, Key.LWin => 0xE3,
            Key.RightCtrl => 0xE4, Key.RightShift => 0xE5, Key.RightAlt => 0xE6, Key.RWin => 0xE7,
            _ => 0,
        };

    /// <summary>Populate the drawer with the values for the current selection.
    /// Nullable values mean "mixed across the selected keys" — slider sits at
    /// average; first user touch commits a uniform value.</summary>
    public void SetState(string title, string subtitle, double? ap, double? ds, double? us)
    {
        _suppressEvents = true;
        try
        {
            _hasSelection = true;
            TitleText.Text = title;
            CodeText.Text = subtitle;
            EmptyHint.Visibility = Visibility.Collapsed;
            EditPanel.Visibility = Visibility.Visible;

            // AP slider
            ApSlider.Value = Clamp(ap ?? DefaultAp, ApSlider.Minimum, ApSlider.Maximum);
            ApValue.Text = ap is null ? "Mixed" : $"{ap.Value:0.0#} mm";
            UpdateNoiseFloorWarning(ApSlider.Value);

            // RT toggle state: on if both ds and us are uniformly > 0, off if
            // both uniformly 0. When mixed across selection we render the
            // toggle in the OFF position (so the next click is "enable for
            // all" — matches user expectation) but show "Mixed" on DS/US so
            // the difference is visible.
            bool mixed = ds is null || us is null;
            bool rtOn = !mixed && ((ds!.Value > 0.0) || (us!.Value > 0.0));
            RtToggle.IsChecked = rtOn;

            // When the selection is mixed we collapse DS/US so the user
            // commits to a uniform value by toggling RT on (which sets
            // panel-visible + emits defaults). Dragging a "Mixed" slider
            // is ambiguous; the explicit toggle is the better entry point.
            DsPanel.Visibility = rtOn ? Visibility.Visible : Visibility.Collapsed;
            UsPanel.Visibility = rtOn ? Visibility.Visible : Visibility.Collapsed;

            DsSlider.Value = Clamp(ds ?? DefaultRtDs, DsSlider.Minimum, DsSlider.Maximum);
            UsSlider.Value = Clamp(us ?? DefaultRtUs, UsSlider.Minimum, UsSlider.Maximum);
            DsValue.Text = ds is null ? "Mixed" : $"{ds.Value:0.0#} mm";
            UsValue.Text = us is null ? "Mixed" : $"{us.Value:0.0#} mm";
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    public void ClearSelection()
    {
        _hasSelection = false;
        TitleText.Text = "No key selected";
        EmptyHint.Visibility = Visibility.Visible;
        EditPanel.Visibility = Visibility.Collapsed;
    }

    private void ApSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        ApValue.Text = $"{ApSlider.Value:0.0#} mm";
        UpdateNoiseFloorWarning(ApSlider.Value);
        Emit();
    }

    // Hall-effect sensor rest drift sits around 0.1-0.3 mm depending on the
    // switch; anything below ~0.4 mm reliably crosses that floor and produces
    // phantom triggers. Surface a non-blocking warning so power users can
    // still set lower values but understand the trade-off.
    private const double NoiseFloorThreshold = 0.4;

    private void UpdateNoiseFloorWarning(double apMm)
    {
        if (ApNoiseFloorWarning is null) return;
        ApNoiseFloorWarning.Visibility = apMm < NoiseFloorThreshold
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void DsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        DsValue.Text = $"{DsSlider.Value:0.0#} mm";
        Emit();
    }

    private void UsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        UsValue.Text = $"{UsSlider.Value:0.0#} mm";
        Emit();
    }

    private void RtToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        // Binary toggle: IsChecked is true or false. (Tri-state was removed
        // because its silent null transition didn't fire Checked/Unchecked.)
        var on = (sender as ToggleButton)?.IsChecked == true;

        _suppressEvents = true;
        try
        {
            DsPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            UsPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

            if (on)
            {
                // No per-key cache restoration in multi-key mode — apply
                // defaults uniformly. (Single-key restore lived here in
                // Phase B; restoring it for the single-key fast path is a
                // Phase I polish item.)
                if (DsSlider.Value <= DsSlider.Minimum)
                {
                    DsSlider.Value = DefaultRtDs;
                    DsValue.Text = $"{DsSlider.Value:0.0#} mm";
                }
                if (UsSlider.Value <= UsSlider.Minimum)
                {
                    UsSlider.Value = DefaultRtUs;
                    UsValue.Text = $"{UsSlider.Value:0.0#} mm";
                }
            }
        }
        finally
        {
            _suppressEvents = false;
        }

        Emit();
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        _suppressEvents = true;
        try
        {
            ApSlider.Value = DefaultAp;
            ApValue.Text = $"{ApSlider.Value:0.0#} mm";
            UpdateNoiseFloorWarning(ApSlider.Value);
            RtToggle.IsChecked = false;
            DsPanel.Visibility = Visibility.Collapsed;
            UsPanel.Visibility = Visibility.Collapsed;
        }
        finally
        {
            _suppressEvents = false;
        }
        Emit();
    }

    private void Emit()
    {
        if (!_hasSelection) return;
        var rtOn = RtToggle.IsChecked == true;
        var ds = rtOn ? DsSlider.Value : 0.0;
        var us = rtOn ? UsSlider.Value : 0.0;
        ActuationChanged?.Invoke(this, new ActuationChangedEventArgs(ApSlider.Value, ds, us));
    }

    private static double Clamp(double v, double min, double max) =>
        v < min ? min : v > max ? max : v;
}

public sealed class ActuationChangedEventArgs(double ap, double ds, double us) : EventArgs
{
    public double Ap { get; } = ap;
    public double Ds { get; } = ds;
    public double Us { get; } = us;
}

public sealed class DrawerRebindEventArgs(byte hidUsage) : EventArgs
{
    public byte HidUsage { get; } = hidUsage;
}
