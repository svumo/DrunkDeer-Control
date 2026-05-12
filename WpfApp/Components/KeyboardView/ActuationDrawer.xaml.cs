using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls.Primitives;
using UserControl = System.Windows.Controls.UserControl;

namespace WpfApp.Components.KeyboardView;

// Right-side drawer that edits a single key's actuation settings. Phase B
// scope: single-key only (multi-key handling lands in Phase D).
//
// Parent flow:
//   1. User clicks a KeyCap → parent calls SetKey(...) to populate the drawer.
//   2. User drags sliders / toggles RT → drawer fires ActuationChanged with the
//      new (AP, DS, US) tuple. Parent updates the KeyCap's properties live.
//   3. No Apply/Cancel buttons — edits are live. The "Sync to Keyboard" button
//      on the canvas pushes to firmware (Phase C).
//
// RT model decision (see plans/keyboard-performance-and-remap.md item 2):
// "DS or US > 0" implies RT-on. Toggling RT off zeroes both DS and US on disk
// but we keep them cached in `_rtMemory` (per-keycode) so the user can toggle
// off and back on without losing their tuning.
public partial class ActuationDrawer : UserControl
{
    private const double DefaultAp = 2.0;
    private const double DefaultRtDs = 0.5;
    private const double DefaultRtUs = 0.5;

    // Per-keycode cache of last-known DS/US so toggling RT off doesn't forget
    // the user's tuning. Reset only when the app closes (intentional — keeps
    // the cap-toggle UX intuitive across selections within a session).
    private readonly Dictionary<string, (double Ds, double Us)> _rtMemory = new();

    private string _currentCode = string.Empty;
    private bool _suppressEvents; // guard against feedback loops when programmatically setting values

    public ActuationDrawer()
    {
        InitializeComponent();
    }

    /// <summary>Fired whenever any slider or RT-toggle changes the values.</summary>
    public event EventHandler<ActuationChangedEventArgs>? ActuationChanged;

    /// <summary>Populate the drawer with the values for a freshly-selected key.</summary>
    public void SetKey(string code, string label, double ap, double ds, double us)
    {
        _suppressEvents = true;
        try
        {
            _currentCode = code;
            TitleText.Text = $"Key · {label}";
            CodeText.Text = code;
            EmptyHint.Visibility = Visibility.Collapsed;
            EditPanel.Visibility = Visibility.Visible;

            ApSlider.Value = Clamp(ap, ApSlider.Minimum, ApSlider.Maximum);
            ApValue.Text = $"{ApSlider.Value:0.0} mm";

            var rtOn = ds > 0.0 || us > 0.0;
            RtToggle.IsChecked = rtOn;
            DsPanel.Visibility = rtOn ? Visibility.Visible : Visibility.Collapsed;
            UsPanel.Visibility = rtOn ? Visibility.Visible : Visibility.Collapsed;

            DsSlider.Value = rtOn ? Clamp(ds, DsSlider.Minimum, DsSlider.Maximum) : DsSlider.Minimum;
            UsSlider.Value = rtOn ? Clamp(us, UsSlider.Minimum, UsSlider.Maximum) : UsSlider.Minimum;
            DsValue.Text = $"{DsSlider.Value:0.0} mm";
            UsValue.Text = $"{UsSlider.Value:0.0} mm";

            // Seed the cache so toggle-off-then-on restores the current values.
            if (rtOn) _rtMemory[code] = (ds, us);
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    public void ClearSelection()
    {
        _currentCode = string.Empty;
        TitleText.Text = "No key selected";
        EmptyHint.Visibility = Visibility.Visible;
        EditPanel.Visibility = Visibility.Collapsed;
    }

    private void ApSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        ApValue.Text = $"{ApSlider.Value:0.0} mm";
        Emit();
    }

    private void DsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        DsValue.Text = $"{DsSlider.Value:0.0} mm";
        if (!string.IsNullOrEmpty(_currentCode) && RtToggle.IsChecked == true)
            _rtMemory[_currentCode] = (DsSlider.Value, UsSlider.Value);
        Emit();
    }

    private void UsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        UsValue.Text = $"{UsSlider.Value:0.0} mm";
        if (!string.IsNullOrEmpty(_currentCode) && RtToggle.IsChecked == true)
            _rtMemory[_currentCode] = (DsSlider.Value, UsSlider.Value);
        Emit();
    }

    private void RtToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        var on = (sender as ToggleButton)?.IsChecked == true;

        _suppressEvents = true;
        try
        {
            DsPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            UsPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

            if (on)
            {
                // Restore from cache if we have one, else defaults.
                var (ds, us) = _rtMemory.TryGetValue(_currentCode, out var cached)
                    ? cached
                    : (DefaultRtDs, DefaultRtUs);
                DsSlider.Value = Clamp(ds, DsSlider.Minimum, DsSlider.Maximum);
                UsSlider.Value = Clamp(us, UsSlider.Minimum, UsSlider.Maximum);
                DsValue.Text = $"{DsSlider.Value:0.0} mm";
                UsValue.Text = $"{UsSlider.Value:0.0} mm";
            }
            // When toggling off: leave slider Values alone (they snap back to min
            // visually but the cache holds the real values). The emitted DS/US
            // below will be 0/0, signalling RT-off to the key.
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
            ApValue.Text = $"{ApSlider.Value:0.0} mm";
            RtToggle.IsChecked = false;
            DsPanel.Visibility = Visibility.Collapsed;
            UsPanel.Visibility = Visibility.Collapsed;
            _rtMemory.Remove(_currentCode);
        }
        finally
        {
            _suppressEvents = false;
        }
        Emit();
    }

    private void Emit()
    {
        if (string.IsNullOrEmpty(_currentCode)) return;
        var rtOn = RtToggle.IsChecked == true;
        var ds = rtOn ? DsSlider.Value : 0.0;
        var us = rtOn ? UsSlider.Value : 0.0;
        ActuationChanged?.Invoke(this, new ActuationChangedEventArgs(_currentCode, ApSlider.Value, ds, us));
    }

    private static double Clamp(double v, double min, double max) =>
        v < min ? min : v > max ? max : v;
}

public sealed class ActuationChangedEventArgs(string code, double ap, double ds, double us) : EventArgs
{
    public string Code { get; } = code;
    public double Ap { get; } = ap;
    public double Ds { get; } = ds;
    public double Us { get; } = us;
}
