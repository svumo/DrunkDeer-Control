using System;
using System.Windows;
using UserControl = System.Windows.Controls.UserControl;
using RoutedEventArgs = System.Windows.RoutedEventArgs;

namespace WpfApp.Components.KeyboardView;

// Mode strip at the top of the Performance tab. Three primary mode cards
// (Rapid Trigger / Release Dual-Trigger / Last Win) plus a secondary row
// with Turbo Mode, Keystroke Tracking and a Reset-all-keys button.
//
// Purely a presentational control: exposes DependencyProperties for the
// current on/off state of each toggle and fires events when the user
// flips them. Wiring into the profile/keyboard layer is the parent's job.
public partial class ModeStrip : UserControl
{
    // Guard so DP-driven IsChecked writes don't fire user-change events.
    private bool _suppressEvents;

    public ModeStrip()
    {
        InitializeComponent();
    }

    // ---- Primary toggle DPs --------------------------------------------------

    public static readonly DependencyProperty RapidTriggerEnabledProperty =
        DependencyProperty.Register(nameof(RapidTriggerEnabled), typeof(bool), typeof(ModeStrip),
            new PropertyMetadata(false, OnRapidTriggerEnabledChanged));
    public bool RapidTriggerEnabled
    {
        get => (bool)GetValue(RapidTriggerEnabledProperty);
        set => SetValue(RapidTriggerEnabledProperty, value);
    }

    public static readonly DependencyProperty ReleaseDualTriggerEnabledProperty =
        DependencyProperty.Register(nameof(ReleaseDualTriggerEnabled), typeof(bool), typeof(ModeStrip),
            new PropertyMetadata(false, OnReleaseDualTriggerEnabledChanged));
    public bool ReleaseDualTriggerEnabled
    {
        get => (bool)GetValue(ReleaseDualTriggerEnabledProperty);
        set => SetValue(ReleaseDualTriggerEnabledProperty, value);
    }

    public static readonly DependencyProperty LastWinEnabledProperty =
        DependencyProperty.Register(nameof(LastWinEnabled), typeof(bool), typeof(ModeStrip),
            new PropertyMetadata(false, OnLastWinEnabledChanged));
    public bool LastWinEnabled
    {
        get => (bool)GetValue(LastWinEnabledProperty);
        set => SetValue(LastWinEnabledProperty, value);
    }

    // ---- Secondary toggle DPs ------------------------------------------------

    public static readonly DependencyProperty TurboEnabledProperty =
        DependencyProperty.Register(nameof(TurboEnabled), typeof(bool), typeof(ModeStrip),
            new PropertyMetadata(false, OnTurboEnabledChanged));
    public bool TurboEnabled
    {
        get => (bool)GetValue(TurboEnabledProperty);
        set => SetValue(TurboEnabledProperty, value);
    }

    public static readonly DependencyProperty KeystrokeTrackingEnabledProperty =
        DependencyProperty.Register(nameof(KeystrokeTrackingEnabled), typeof(bool), typeof(ModeStrip),
            new PropertyMetadata(false, OnKeystrokeTrackingEnabledChanged));
    public bool KeystrokeTrackingEnabled
    {
        get => (bool)GetValue(KeystrokeTrackingEnabledProperty);
        set => SetValue(KeystrokeTrackingEnabledProperty, value);
    }

    // ---- Events --------------------------------------------------------------

    public event EventHandler<ModeToggleEventArgs>? PrimaryToggleChanged;
    public event EventHandler<ModeToggleEventArgs>? SecondaryToggleChanged;
    public event EventHandler? ResetAllClicked;
    public event EventHandler? ResetAllRemapsClicked;

    // ---- DP -> UI sync -------------------------------------------------------
    // These push the DP value into the underlying ToggleButton without
    // firing PrimaryToggleChanged/SecondaryToggleChanged. That way callers
    // can set the property in code without echoing back a "user changed
    // this" event.

    private static void OnRapidTriggerEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ModeStrip s) s.SetToggleSilently(s.RtToggle, (bool)e.NewValue);
    }

    private static void OnReleaseDualTriggerEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ModeStrip s) s.SetToggleSilently(s.RdtToggle, (bool)e.NewValue);
    }

    private static void OnLastWinEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ModeStrip s) s.SetToggleSilently(s.LwToggle, (bool)e.NewValue);
    }

    private static void OnTurboEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ModeStrip s) s.SetToggleSilently(s.TurboToggle, (bool)e.NewValue);
    }

    private static void OnKeystrokeTrackingEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ModeStrip s) s.SetToggleSilently(s.KeystrokeToggle, (bool)e.NewValue);
    }

    private void SetToggleSilently(System.Windows.Controls.Primitives.ToggleButton? toggle, bool value)
    {
        if (toggle == null) return;
        if (toggle.IsChecked == value) return;
        _suppressEvents = true;
        try { toggle.IsChecked = value; }
        finally { _suppressEvents = false; }
    }

    // ---- Toggle handlers -----------------------------------------------------
    // Each handler mirrors the new IsChecked back into the matching DP and
    // raises the corresponding event so parents can react.

    private void RtToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        var v = RtToggle.IsChecked == true;
        SetCurrentValue(RapidTriggerEnabledProperty, v);
        PrimaryToggleChanged?.Invoke(this, new ModeToggleEventArgs("rt", v));
    }

    private void RdtToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        var v = RdtToggle.IsChecked == true;
        SetCurrentValue(ReleaseDualTriggerEnabledProperty, v);
        PrimaryToggleChanged?.Invoke(this, new ModeToggleEventArgs("rdt", v));
    }

    private void LwToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        var v = LwToggle.IsChecked == true;
        SetCurrentValue(LastWinEnabledProperty, v);
        PrimaryToggleChanged?.Invoke(this, new ModeToggleEventArgs("lw", v));
    }

    private void TurboToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        var v = TurboToggle.IsChecked == true;
        SetCurrentValue(TurboEnabledProperty, v);
        SecondaryToggleChanged?.Invoke(this, new ModeToggleEventArgs("turbo", v));
    }

    private void KeystrokeToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        var v = KeystrokeToggle.IsChecked == true;
        SetCurrentValue(KeystrokeTrackingEnabledProperty, v);
        SecondaryToggleChanged?.Invoke(this, new ModeToggleEventArgs("keystroke", v));
    }

    private void ResetAllButton_Click(object sender, RoutedEventArgs e)
    {
        ResetAllClicked?.Invoke(this, EventArgs.Empty);
    }

    private void ResetAllRemapsButton_Click(object sender, RoutedEventArgs e)
    {
        ResetAllRemapsClicked?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class ModeToggleEventArgs(string mode, bool value) : EventArgs
{
    // One of: "rt", "rdt", "lw", "turbo", "keystroke".
    public string Mode { get; } = mode;
    public bool Value { get; } = value;
}
