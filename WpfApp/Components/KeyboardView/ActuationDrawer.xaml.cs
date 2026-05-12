using System;
using System.Windows;
using System.Windows.Controls.Primitives;
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

    public ActuationDrawer()
    {
        InitializeComponent();
        // Three-state RT toggle so "mixed across selected keys" can render
        // distinctly from on/off.
        RtToggle.IsThreeState = true;
    }

    /// <summary>Fired whenever any slider or RT-toggle changes the values.
    /// Code is intentionally absent — the parent knows what's selected.</summary>
    public event EventHandler<ActuationChangedEventArgs>? ActuationChanged;

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
            ApValue.Text = ap is null ? "Mixed" : $"{ap.Value:0.0} mm";

            // RT toggle state: on if both ds and us are uniformly > 0, off if
            // both uniformly 0, indeterminate otherwise.
            bool? rtState = (ds, us) switch
            {
                (null, _) or (_, null) => null,
                ({ } d, { } u) when d > 0.0 || u > 0.0 => true,
                _ => false,
            };
            RtToggle.IsChecked = rtState;

            var dsUsVisible = rtState != false; // visible when on or mixed
            DsPanel.Visibility = dsUsVisible ? Visibility.Visible : Visibility.Collapsed;
            UsPanel.Visibility = dsUsVisible ? Visibility.Visible : Visibility.Collapsed;

            DsSlider.Value = Clamp(ds ?? DefaultRtDs, DsSlider.Minimum, DsSlider.Maximum);
            UsSlider.Value = Clamp(us ?? DefaultRtUs, UsSlider.Minimum, UsSlider.Maximum);
            DsValue.Text = ds is null ? "Mixed" : $"{ds.Value:0.0} mm";
            UsValue.Text = us is null ? "Mixed" : $"{us.Value:0.0} mm";
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
        ApValue.Text = $"{ApSlider.Value:0.0} mm";
        Emit();
    }

    private void DsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        DsValue.Text = $"{DsSlider.Value:0.0} mm";
        Emit();
    }

    private void UsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        UsValue.Text = $"{UsSlider.Value:0.0} mm";
        Emit();
    }

    private void RtToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        // IsChecked: true = enabling RT for all, false = disabling, null = mixed
        // We treat the user-initiated null state (very rare, requires explicit
        // setter) the same as true so the user can't get stuck in mixed.
        var on = (sender as ToggleButton)?.IsChecked ?? true;

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
                    DsValue.Text = $"{DsSlider.Value:0.0} mm";
                }
                if (UsSlider.Value <= UsSlider.Minimum)
                {
                    UsSlider.Value = DefaultRtUs;
                    UsValue.Text = $"{UsSlider.Value:0.0} mm";
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
            ApValue.Text = $"{ApSlider.Value:0.0} mm";
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
