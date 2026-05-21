using System;
using System.Windows;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;

namespace WpfApp.Components.KeyboardView;

// Per-pair AP/DS/US drawer for Release Dual-Trigger. Opens when the user
// commits a new RDT pair (after the second key pick) or clicks an existing
// chip. The drawer is a "thin" editor: it emits value-changed events with
// the new value; the parent (KeyboardPerformanceView) owns the writeback
// into `_caps[pressSlot]` / `_caps[releaseSlot]` and the persistence /
// auto-sync side-effects.
//
// AP is shared by the pair — the official driver exposes a single
// trigger-point slider for both pair members. We mirror that by writing
// the same AP value to both slots on ApValueChanged.
public partial class RdtPairDrawer : UserControl
{
    public RdtPairDrawer() { InitializeComponent(); }

    // Suppression flag so SetValues(...) does not echo programmatic slider
    // assignments back through the value-changed events. Mirrors the
    // pattern used by ActuationDrawer.
    private bool _suppress;

    public event EventHandler<double>? ApValueChanged;
    public event EventHandler<double>? DsValueChanged;
    public event EventHandler<double>? UsValueChanged;
    public event EventHandler? CloseRequested;

    // The press/release slot pair this drawer is currently editing. Stored
    // so the parent doesn't have to thread it through every event; the
    // parent reads these on each value-changed callback to know which slots
    // to write to.
    public byte PressSlot { get; private set; }
    public byte ReleaseSlot { get; private set; }

    public void SetPair(byte pressSlot, string pressLabel,
                        byte releaseSlot, string releaseLabel,
                        double ap, double ds, double us)
    {
        PressSlot = pressSlot;
        ReleaseSlot = releaseSlot;

        TitleText.Text = $"{pressLabel} → {releaseLabel}";
        DsKeyLabel.Text = $"{pressLabel} · on press";
        UsKeyLabel.Text = $"{releaseLabel} · on release";

        _suppress = true;
        try
        {
            ApSlider.Value = Clamp(ap, ApSlider.Minimum, ApSlider.Maximum);
            DsSlider.Value = Clamp(ds, DsSlider.Minimum, DsSlider.Maximum);
            UsSlider.Value = Clamp(us, UsSlider.Minimum, UsSlider.Maximum);
            ApValue.Text = $"{ApSlider.Value:F1} mm";
            DsValue.Text = $"{DsSlider.Value:F1} mm";
            UsValue.Text = $"{UsSlider.Value:F1} mm";
        }
        finally { _suppress = false; }
    }

    private void ApSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        ApValue.Text = $"{e.NewValue:F1} mm";
        if (_suppress) return;
        ApValueChanged?.Invoke(this, e.NewValue);
    }

    private void DsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        DsValue.Text = $"{e.NewValue:F1} mm";
        if (_suppress) return;
        DsValueChanged?.Invoke(this, e.NewValue);
    }

    private void UsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UsValue.Text = $"{e.NewValue:F1} mm";
        if (_suppress) return;
        UsValueChanged?.Invoke(this, e.NewValue);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private static double Clamp(double v, double lo, double hi)
        => v < lo ? lo : (v > hi ? hi : v);
}
