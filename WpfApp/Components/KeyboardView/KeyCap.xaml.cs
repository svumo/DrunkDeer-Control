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

    // Cache the Dd* design-system brushes / effects up front. `FindResource`
    // walks the entire visual tree for each lookup; with ~91 caps each calling
    // it 6+ times on every UpdateVisuals, Ctrl+A and marquee select drag run
    // 500+ resource lookups per frame. Application-level resources only need
    // to be resolved once for the lifetime of the app.
    private static Brush? _brAccent, _brAccentHi, _brAccentSoft;
    private static Brush? _brKeySelected, _brKeyHover, _brKeyBg, _brKeyBorder;
    private static Brush? _brFg1, _brKeyText, _brKeyTextDim;
    private static Brush? _brActuationLow, _brActuationHigh;
    private static System.Windows.Media.FontFamily? _ffMono, _ffSans;
    private static Effect? _fxShadow1, _fxGlow;
    private static bool _resourcesCached;

    private static void EnsureResourceCache()
    {
        if (_resourcesCached) return;
        var app = System.Windows.Application.Current;
        if (app is null) return;
        Brush B(string key) => (Brush)(app.TryFindResource(key) ?? Brushes.Transparent);
        _brAccent         = B("DdAccent");
        _brAccentHi       = B("DdAccentHi");
        _brAccentSoft     = B("DdAccentSoft");
        _brKeySelected    = B("DdKeySelected");
        _brKeyHover       = B("DdKeyHover");
        _brKeyBg          = B("DdKeyBg");
        _brKeyBorder      = B("DdKeyBorder");
        _brFg1            = B("DdFg1");
        _brKeyText        = B("DdKeyText");
        _brKeyTextDim     = B("DdKeyTextDim");
        _brActuationLow   = B("DdActuationLow");
        _brActuationHigh  = B("DdActuationHigh");
        _ffMono           = app.TryFindResource("DdFontMono") as System.Windows.Media.FontFamily;
        _ffSans           = app.TryFindResource("DdFontSans") as System.Windows.Media.FontFamily;
        _fxShadow1        = app.TryFindResource("DdShadow1") as Effect;
        _fxGlow           = app.TryFindResource("DdGlowAccent") as Effect;
        _resourcesCached  = true;
    }

    // When false, IsSelected caps use the cheap DdShadow1 instead of the
    // BlurRadius=18 DdGlowAccent. Set by KeyboardPerformanceView whenever
    // more than one key is selected — 91 software-blurred caps was the
    // dominant cost on Ctrl+A and large marquee drags. Single-key selection
    // keeps the glow so the visual polish survives the normal case.
    public static bool EnableSelectionGlow { get; set; } = true;

    // Forces a visuals refresh from outside (after EnableSelectionGlow
    // flips, a previously-selected cap whose IsSelected didn't change
    // still needs to drop or re-acquire the glow).
    internal void RefreshVisuals() => UpdateVisuals();

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

    // Non-null when the slot is remapped — replaces the cap's printed label
    // with the rebound key's label (e.g. "3" instead of "1") and triggers
    // a distinct visual tint so the user can see at a glance which keys
    // are no longer their factory binding.
    public static readonly DependencyProperty RemappedLabelProperty =
        DependencyProperty.Register(nameof(RemappedLabel), typeof(string), typeof(KeyCap),
            new PropertyMetadata(null, OnRemappedLabelChanged));
    public string? RemappedLabel
    {
        get => (string?)GetValue(RemappedLabelProperty);
        set => SetValue(RemappedLabelProperty, value);
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
            // Remap overrides the displayed label until cleared.
            c.LabelText.Text = c.RemappedLabel ?? c.Label;
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

    private static void OnRemappedLabelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is KeyCap c && c.LabelText != null)
        {
            // When remapped, show the new binding's label. When cleared,
            // fall back to the original cap label.
            c.LabelText.Text = c.RemappedLabel ?? c.Label;
            c.UpdateVisuals();
        }
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
        EnsureResourceCache();

        // Border + background + value text color, in priority order.
        Brush borderBrush;
        Brush background;
        Brush valueColor;
        // Default: NO effect. Every effect = DropShadow requires WPF to render
        // the cap to an intermediate bitmap, apply the blur, then composite.
        // Applying a shadow to all ~91 caps at rest was making the canvas pay
        // 91 render-to-bitmap ops on every paint. The cheap soft shadow is now
        // reserved for state transitions (selection glow / remap accent).
        Effect? effect = null;

        if (IsSelected)
        {
            borderBrush = _brAccent!;
            background = _brKeySelected!;
            valueColor = _brFg1!;
            // Heavy DropShadowEffect (BlurRadius=18) is a software blur — 91
            // of them at once is the dominant cost on Ctrl+A. Skip when the
            // selection is large; the accent border + tinted background still
            // make multi-select unambiguous.
            if (EnableSelectionGlow) effect = _fxGlow;
        }
        else if (IsMarqueePreview)
        {
            borderBrush = _brAccentHi!;
            background = _brKeyHover!;
            valueColor = _brKeyTextDim!;
        }
        else if (!string.IsNullOrEmpty(RemappedLabel))
        {
            // Remapped — soft accent tint, accent border. Stands out against
            // the default surface but doesn't compete with Selected/RT.
            borderBrush = _brAccent!;
            background = _brAccentSoft!;
            valueColor = _brFg1!;
        }
        else if (RapidTriggerActive)
        {
            borderBrush = RtBorderBrush;
            background = _brKeyBg!;
            valueColor = _brKeyTextDim!;
        }
        else if (ActuationPoint <= 1.0)
        {
            borderBrush = _brActuationLow!;
            background = _brKeyBg!;
            valueColor = _brActuationLow!;
        }
        else if (ActuationPoint >= 2.8)
        {
            borderBrush = _brActuationHigh!;
            background = _brKeyBg!;
            valueColor = _brActuationHigh!;
        }
        else
        {
            borderBrush = _brKeyBorder!;
            background = _brKeyBg!;
            valueColor = _brKeyTextDim!;
        }

        Root.BorderBrush = borderBrush;
        Root.Background = background;
        Root.Effect = effect;

        // Dim modifier-key labels slightly — matches the JSX intent for non-letter keys.
        if (LabelText != null)
        {
            LabelText.Foreground = IsSelected
                ? _brFg1!
                : KeyType == "mod"
                    ? _brKeyTextDim!
                    : _brKeyText!;
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
                if (_ffMono != null) TopText.FontFamily = _ffMono;
                TopText.FontWeight = FontWeights.Medium;
                TopText.Foreground = valueColor;
                TopText.Visibility = Visibility.Visible;
            }
            else if (!string.IsNullOrEmpty(SubLabel))
            {
                TopText.Text = SubLabel!;
                if (_ffSans != null) TopText.FontFamily = _ffSans;
                TopText.FontWeight = FontWeights.Normal;
                TopText.Foreground = _brKeyTextDim!;
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
            RtBadge.Foreground = IsSelected ? _brFg1! : _brAccentHi!;
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
