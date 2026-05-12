using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Effects;
using UserControl = System.Windows.Controls.UserControl;
using Brush = System.Windows.Media.Brush;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using Color = System.Windows.Media.Color;
using FontWeights = System.Windows.FontWeights;

namespace WpfApp.Components.KeyboardView;

// Single key in the keyboard canvas. Mirrors design-system/ui_kits/control_app/KeyCap.jsx
// but on a WPF UserControl. Width/height are recomputed whenever UnitWidth or
// WidthMultiplier change; tint state is recomputed whenever any visual input
// changes. Phase A renders these statically — Phase B+ adds click handling
// and selection state.
//
// State priority (visually): Selected > MarqueePreview > RapidTrigger > HeatLow / HeatHigh > Default.
// The whole component is driven by DependencyProperties so it can be data-bound
// to a future KeyboardCanvasViewModel without any refactor.
public partial class KeyCap : UserControl
{
    // Pixels of empty space between adjacent caps. Matches the JSX KEY_GAP.
    private const double KeyGap = 4.0;

    // Soft warm tint used when the key is in Rapid Trigger mode. Hardcoded
    // here (rather than a Dd* token) because RT is the only place this exact
    // coral appears. Pulled from the JSX reference (rgba(255,110,64,.32)).
    private static readonly Brush RtBorderBrush =
        new SolidColorBrush(Color.FromArgb(0x52, 0xFF, 0x6E, 0x40));

    public KeyCap()
    {
        InitializeComponent();
        UpdateSize();
        UpdateVisuals();
    }

    // ---- DependencyProperties -------------------------------------------------

    public static readonly DependencyProperty CodeProperty =
        DependencyProperty.Register(nameof(Code), typeof(string), typeof(KeyCap),
            new PropertyMetadata(string.Empty));
    public string Code
    {
        get => (string)GetValue(CodeProperty);
        set => SetValue(CodeProperty, value);
    }

    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(KeyCap),
            new PropertyMetadata(string.Empty, OnLabelChanged));
    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public static readonly DependencyProperty SubLabelProperty =
        DependencyProperty.Register(nameof(SubLabel), typeof(string), typeof(KeyCap),
            new PropertyMetadata(null, OnSubLabelChanged));
    public string? SubLabel
    {
        get => (string?)GetValue(SubLabelProperty);
        set => SetValue(SubLabelProperty, value);
    }

    public static readonly DependencyProperty UnitWidthProperty =
        DependencyProperty.Register(nameof(UnitWidth), typeof(double), typeof(KeyCap),
            new PropertyMetadata(36.0, OnSizeInputChanged));
    public double UnitWidth
    {
        get => (double)GetValue(UnitWidthProperty);
        set => SetValue(UnitWidthProperty, value);
    }

    public static readonly DependencyProperty WidthMultiplierProperty =
        DependencyProperty.Register(nameof(WidthMultiplier), typeof(double), typeof(KeyCap),
            new PropertyMetadata(1.0, OnSizeInputChanged));
    public double WidthMultiplier
    {
        get => (double)GetValue(WidthMultiplierProperty);
        set => SetValue(WidthMultiplierProperty, value);
    }

    public static readonly DependencyProperty ActuationPointProperty =
        DependencyProperty.Register(nameof(ActuationPoint), typeof(double), typeof(KeyCap),
            new PropertyMetadata(2.0, OnVisualInputChanged));
    public double ActuationPoint
    {
        get => (double)GetValue(ActuationPointProperty);
        set => SetValue(ActuationPointProperty, value);
    }

    public static readonly DependencyProperty DownstrokeProperty =
        DependencyProperty.Register(nameof(Downstroke), typeof(double), typeof(KeyCap),
            new PropertyMetadata(0.0, OnVisualInputChanged));
    public double Downstroke
    {
        get => (double)GetValue(DownstrokeProperty);
        set => SetValue(DownstrokeProperty, value);
    }

    public static readonly DependencyProperty UpstrokeProperty =
        DependencyProperty.Register(nameof(Upstroke), typeof(double), typeof(KeyCap),
            new PropertyMetadata(0.0, OnVisualInputChanged));
    public double Upstroke
    {
        get => (double)GetValue(UpstrokeProperty);
        set => SetValue(UpstrokeProperty, value);
    }

    public static readonly DependencyProperty IsSelectedProperty =
        DependencyProperty.Register(nameof(IsSelected), typeof(bool), typeof(KeyCap),
            new PropertyMetadata(false, OnVisualInputChanged));
    public bool IsSelected
    {
        get => (bool)GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    public static readonly DependencyProperty IsMarqueePreviewProperty =
        DependencyProperty.Register(nameof(IsMarqueePreview), typeof(bool), typeof(KeyCap),
            new PropertyMetadata(false, OnVisualInputChanged));
    public bool IsMarqueePreview
    {
        get => (bool)GetValue(IsMarqueePreviewProperty);
        set => SetValue(IsMarqueePreviewProperty, value);
    }

    // "mod" dims the label (Shift/Ctrl/Alt/Win/Fn/Menu).
    public static readonly DependencyProperty KeyTypeProperty =
        DependencyProperty.Register(nameof(KeyType), typeof(string), typeof(KeyCap),
            new PropertyMetadata(null, OnVisualInputChanged));
    public string? KeyType
    {
        get => (string?)GetValue(KeyTypeProperty);
        set => SetValue(KeyTypeProperty, value);
    }

    public static readonly DependencyProperty UncertainProperty =
        DependencyProperty.Register(nameof(Uncertain), typeof(bool), typeof(KeyCap),
            new PropertyMetadata(false, OnUncertainChanged));
    public bool Uncertain
    {
        get => (bool)GetValue(UncertainProperty);
        set => SetValue(UncertainProperty, value);
    }

    // Live keystroke-tracking depth in mm (0.0..4.0). 0 hides the bar. Set by
    // the keystroke-tracking broker on each incoming HID depth event.
    public static readonly DependencyProperty LiveDepthProperty =
        DependencyProperty.Register(nameof(LiveDepth), typeof(double), typeof(KeyCap),
            new PropertyMetadata(0.0, OnLiveDepthChanged));
    public double LiveDepth
    {
        get => (double)GetValue(LiveDepthProperty);
        set => SetValue(LiveDepthProperty, value);
    }

    // Maximum depth (mm) the live bar represents at full width. Matches the
    // switch travel ceiling used by the firmware's high-precision encoding
    // (Yg = 3.1 in the JS), padded slightly so an overrange value still
    // pegs at 100% rather than overflowing.
    private const double LiveDepthMaxMm = 4.0;

    public bool RapidTriggerActive => Downstroke > 0.0 || Upstroke > 0.0;

    // ---- Events ---------------------------------------------------------------

    public event EventHandler<KeyCapClickEventArgs>? CapClick;

    // ---- DP callbacks ---------------------------------------------------------

    private static void OnSizeInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is KeyCap c) c.UpdateSize();
    }

    private static void OnVisualInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is KeyCap c) c.UpdateVisuals();
    }

    private static void OnLabelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is KeyCap c && c.LabelText != null)
        {
            c.LabelText.Text = c.Label;
            c.UpdateVisuals();
        }
    }

    private static void OnSubLabelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is KeyCap c) c.UpdateVisuals();
    }

    private static void OnUncertainChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is KeyCap c && c.UncertainStripe != null)
            c.UncertainStripe.Visibility = c.Uncertain ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void OnLiveDepthChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is KeyCap c) c.UpdateLiveDepthBar();
    }

    private void UpdateLiveDepthBar()
    {
        if (DepthBar == null || DepthFill == null) return;
        var depth = LiveDepth;
        if (depth <= 0.0)
        {
            DepthBar.Visibility = Visibility.Collapsed;
            DepthFill.Width = 0;
            return;
        }
        DepthBar.Visibility = Visibility.Visible;
        // Track width = cap width minus the bar's left+right margin (3+3=6).
        double trackWidth = Math.Max(0, Width - 6.0);
        double fraction = Math.Min(1.0, depth / LiveDepthMaxMm);
        DepthFill.Width = trackWidth * fraction;
    }

    // ---- Visual state update --------------------------------------------------

    private void UpdateSize()
    {
        // width = base unit * multiplier + extra gap eaten by stretching.
        // Matches JSX: w = (k.w ?? 1) * unit + ((k.w ?? 1) - 1) * KEY_GAP.
        var w = WidthMultiplier * UnitWidth + (WidthMultiplier - 1.0) * KeyGap;
        Width = w;
        Height = UnitWidth;

        // Scale font sizes with the cap — keep them small enough to fit (cap'd
        // at 13/9.5 like the JSX). Skip if the template hasn't materialized yet.
        if (LabelText != null)
            LabelText.FontSize = Math.Min(13.0, UnitWidth * 0.32);
        if (TopText != null)
            TopText.FontSize = Math.Min(9.5, UnitWidth * 0.22);

        // Bar width is tied to the cap's current width — recompute when size
        // inputs change so a wide spacebar's bar still fills correctly.
        UpdateLiveDepthBar();
    }

    private void UpdateVisuals()
    {
        if (Root == null) return; // template not yet applied

        // Border + background + value text color, in priority order.
        Brush borderBrush;
        Brush background;
        Brush valueColor;
        Effect? effect = TryFindResource("DdShadow1") as Effect;

        if (IsSelected)
        {
            borderBrush = (Brush)FindResource("DdAccent");
            background = (Brush)FindResource("DdKeySelected");
            valueColor = (Brush)FindResource("DdFg1");
            effect = TryFindResource("DdGlowAccent") as Effect ?? effect;
        }
        else if (IsMarqueePreview)
        {
            borderBrush = (Brush)FindResource("DdAccentHi");
            background = (Brush)FindResource("DdKeyHover");
            valueColor = (Brush)FindResource("DdKeyTextDim");
        }
        else if (RapidTriggerActive)
        {
            borderBrush = RtBorderBrush;
            background = (Brush)FindResource("DdKeyBg");
            valueColor = (Brush)FindResource("DdKeyTextDim");
        }
        else if (ActuationPoint <= 1.0)
        {
            borderBrush = (Brush)FindResource("DdActuationLow");
            background = (Brush)FindResource("DdKeyBg");
            valueColor = (Brush)FindResource("DdActuationLow");
        }
        else if (ActuationPoint >= 2.8)
        {
            borderBrush = (Brush)FindResource("DdActuationHigh");
            background = (Brush)FindResource("DdKeyBg");
            valueColor = (Brush)FindResource("DdActuationHigh");
        }
        else
        {
            borderBrush = (Brush)FindResource("DdKeyBorder");
            background = (Brush)FindResource("DdKeyBg");
            valueColor = (Brush)FindResource("DdKeyTextDim");
        }

        Root.BorderBrush = borderBrush;
        Root.Background = background;
        Root.Effect = effect;

        // Dim modifier-key labels slightly — matches the JSX intent for non-letter keys.
        if (LabelText != null)
        {
            LabelText.Foreground = IsSelected
                ? (Brush)FindResource("DdFg1")
                : KeyType == "mod"
                    ? (Brush)FindResource("DdKeyTextDim")
                    : (Brush)FindResource("DdKeyText");
            LabelText.FontWeight = IsSelected ? FontWeights.SemiBold : FontWeights.Medium;
        }

        // TopText (top-center): shows either the customized AP value or the
        // shift-character — never both. AP takes priority when set away from
        // 2.0 (or when the cap is selected — gives live feedback). Falls back
        // to the SubLabel if present.
        if (TopText != null)
        {
            var apCustomized = IsSelected || Math.Abs(ActuationPoint - 2.0) > 0.001;
            if (apCustomized)
            {
                TopText.Text = ActuationPoint.ToString("0.0");
                TopText.FontFamily = (System.Windows.Media.FontFamily)FindResource("DdFontMono");
                TopText.FontWeight = FontWeights.Medium;
                TopText.Foreground = valueColor;
                TopText.Visibility = Visibility.Visible;
            }
            else if (!string.IsNullOrEmpty(SubLabel))
            {
                TopText.Text = SubLabel!;
                TopText.FontFamily = (System.Windows.Media.FontFamily)FindResource("DdFontSans");
                TopText.FontWeight = FontWeights.Normal;
                TopText.Foreground = (Brush)FindResource("DdKeyTextDim");
                TopText.Visibility = Visibility.Visible;
            }
            else
            {
                TopText.Visibility = Visibility.Collapsed;
            }
        }

        // RT badge: shown whenever this key has RT active, including the
        // selected case (so toggling RT in the drawer immediately surfaces on
        // the cap). When selected we flip the badge to white so it stays
        // readable against the purple-tinted selected background.
        if (RtBadge != null)
        {
            RtBadge.Visibility = RapidTriggerActive ? Visibility.Visible : Visibility.Collapsed;
            RtBadge.Foreground = IsSelected
                ? (Brush)FindResource("DdFg1")
                : (Brush)FindResource("DdAccentHi");
        }
    }

    // ---- Click forwarding -----------------------------------------------------

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var args = new KeyCapClickEventArgs(
            code: Code,
            ctrl: (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control,
            shift: (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift,
            alt: (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt);
        CapClick?.Invoke(this, args);
        e.Handled = true;
    }
}

public sealed class KeyCapClickEventArgs(string code, bool ctrl, bool shift, bool alt) : EventArgs
{
    public string Code { get; } = code;
    public bool Ctrl { get; } = ctrl;
    public bool Shift { get; } = shift;
    public bool Alt { get; } = alt;
}
