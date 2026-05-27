using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Driver;
using HidSharp;
using WpfApp.Components.KeyboardView;
using WpfApp.Profile;
using WpfApp.ViewModels;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Key = System.Windows.Input.Key;
using KeyboardKey = System.Windows.Input.Keyboard;
using ModifierKeys = System.Windows.Input.ModifierKeys;
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace WpfApp.Components.Lighting;

// Phase 2 RGB lighting editor. Hosts both the preset-mode controls (Off /
// Always On / Breath, brightness + speed) and the per-key custom painter
// (LightingCanvas + colour picker + quick presets + undo/redo).
//
// Dispatch on Sync:
//   * Mode in {Off, AlwaysOn, Breath, Marquee, Neon}  →  BuildLedModePacket
//   * Mode == ModeCustom                              →  BuildPerKeyRgbPackets
//
// Custom mode is gated by FirmwareCapabilities.SupportsCustomRgb (set via
// ApplyCapability). Even with the capability flag true the FIRST sync of
// a custom packet shows a brick-confirmation Window — once the user
// confirms, Settings.RgbCustomBrickAcknowledged sticks and the modal
// never reappears.
public partial class LightingView : System.Windows.Controls.UserControl
{
    private readonly KeyboardManager keyboardManager;
    private readonly Settings settings;
    private ProfileManager? profileManager;
    private ProfileItem? activeProfile;
    // Starts true to swallow ValueChanged events fired during InitializeComponent
    // — when WPF coerces Slider.Value from the XAML default to the (non-zero)
    // Minimum, ValueChanged fires before the rest of the named elements exist.
    private bool suppressSliderWriteback = true;
    private FirmwareCapabilities? currentCaps;

    // Selection model shared with the LightingCanvas. Kept here so paint
    // actions can read SelectedKeys without going through the canvas's
    // public surface every time.
    private readonly KeyboardCanvasViewModel canvasVm = new();

    // Current paint colour. Sliders, hex input, swatches, eyedropper, and
    // RGB sliders all converge on these three bytes; the current-colour
    // swatch and hex textbox reflect them on every change.
    private byte activeR = 0x95, activeG = 0x75, activeB = 0xCD;

    // Recent colours MRU. Persisted to Settings.RecentLightingColorsJson on
    // every change. Capped at MaxRecent — older entries fall off the tail.
    private const int MaxRecent = 12;
    private readonly List<(byte R, byte G, byte B)> recent = new();

    // Undo / redo. Each entry is a full 378-byte snapshot of LightingProfile
    // .KeyColors (cheap to copy; rebuilding from a diff would be more code
    // for no real win at this size). Bounded so a runaway interaction
    // doesn't grow the stack indefinitely.
    private const int MaxHistory = 64;
    private readonly Stack<byte[]> undoStack = new();
    private readonly Stack<byte[]> redoStack = new();

    public LightingViewModel ViewModel { get; } = new();

    public LightingView(KeyboardManager keyboardManager, Settings settings)
    {
        this.keyboardManager = keyboardManager;
        this.settings = settings;
        InitializeComponent();
        DataContext = ViewModel;
        ViewModel.PropertyChanged += OnViewModelChanged;
        keyboardManager.ConnectedKeyboardChanged += _ => Dispatcher.Invoke(RefreshConnectivity);

        // Hook the canvas up to the shared selection VM + colour-pick event.
        PaintCanvas.AttachSelectionViewModel(canvasVm);
        PaintCanvas.KeyColorPicked += OnCanvasColorPicked;
        canvasVm.SelectedKeys.CollectionChanged += (_, _) =>
        {
            RefreshSelectionStatus();
            SyncPickerFromSingleSelection();
        };
        // Resolve & install the active model's layout. Hot-plug to a
        // different keyboard model updates this via ApplyCapability.
        ApplyCanvasLayoutForCurrentKeyboard();

        // Seed palette swatches, recent colours strip, custom brightness.
        BuildPaletteGrid();
        LoadRecentFromSettings();
        RefreshRecentGrid();
        SyncPickerVisualsFromActive();
        CustomBrightnessSlider.Value = settings.CustomRgbBrightnessScale;

        // Populate the effect dropdown from the catalog. One-shot per view.
        EnsureEffectDropdownPopulated();

        // Keyboard shortcuts for undo / redo / select-all / paint when the
        // Lighting view has focus.
        PreviewKeyDown += OnPreviewKeyDown;

        RefreshConnectivity();
        RefreshDirtyDot();
        RefreshControlEnables();
        RefreshSelectionStatus();
        suppressSliderWriteback = false;
    }

    // Called by MainWindow whenever the connected keyboard's
    // FirmwareCapabilities change (initial connect, hot-plug, disconnect).
    //
    // Off / Always On / Breath are NOT gated here: they're the universal
    // baseline across every RGB-capable DrunkDeer firmware and must remain
    // editable offline. Custom is a separate gate (SupportsCustomRgb) —
    // disabled when null caps / unsupported model. Marquee / Neon stay
    // locked behind VerifiedRgbModes until JS extraction pins their codes.
    public void ApplyCapability(FirmwareCapabilities? caps)
    {
        currentCaps = caps;
        if (PresetCustom is null || EffectDropdown is null) return;

        // Custom (per-key painter) is its own gate. Tooltip flips on state.
        bool customAvailable = caps?.SupportsCustomRgb ?? false;
        PresetCustom.IsEnabled = customAvailable;
        PresetCustom.ToolTip = customAvailable
            ? "Custom per-key painting (unverified on hardware — confirm via first-sync modal)"
            : "Per-key painting not available for this keyboard / firmware";

        // The connected model might have changed too — refresh the canvas
        // layout so paint applies to the right keyboard.
        ApplyCanvasLayoutForCurrentKeyboard();
    }

    public void Attach(ProfileManager pm)
    {
        profileManager = pm;
        pm.CurrentProfileChanged += (_, item) => Dispatcher.Invoke(() => Rehydrate(item));
        if (pm.CurrentIndex >= 0 && pm.CurrentIndex < pm.Profiles.Count)
        {
            Rehydrate(pm.Profiles[pm.CurrentIndex]);
        }
    }

    private void Rehydrate(ProfileItem item)
    {
        activeProfile = item;
        suppressSliderWriteback = true;
        try
        {
            ViewModel.Hydrate(item.LightingProfile);
            BrightnessSlider.Value = ViewModel.Brightness;
            SpeedSlider.Value = ViewModel.Speed;
            UpdatePresetRadioFromMode();

            // Per-key colours: rebuild from the profile's stored KeyColors,
            // or fall back to an empty buffer. Hand the reference to the
            // canvas so paint actions mutate the same array we serialise.
            var lp = item.LightingProfile;
            var colors = lp?.KeyColors ?? Array.Empty<byte>();
            // Re-allocate to 378 bytes so PaintKeys has room to write without
            // each call having to grow the buffer.
            if (colors.Length < 126 * 3)
            {
                var grown = new byte[126 * 3];
                Array.Copy(colors, grown, Math.Min(colors.Length, grown.Length));
                colors = grown;
            }
            PaintCanvas.SetKeyColors(colors);

            // Clear undo / redo on profile switch — the new profile's
            // history is its own concern; sharing across profiles would
            // produce nonsense diffs. Same reasoning clears the selection:
            // a code like "w" might be painted very differently on the
            // new profile, so carrying selection over is more confusing
            // than helpful.
            undoStack.Clear();
            redoStack.Clear();
            canvasVm.ClearSelection();
            RefreshUndoRedoButtons();
        }
        finally { suppressSliderWriteback = false; }
        RefreshCustomCardVisibility();
        RefreshDirtyDot();
        RefreshControlEnables();
        RefreshValueLabels();
        RefreshSelectionStatus();
        ViewModel.StatusMessage = string.Empty;
    }

    private void UpdatePresetRadioFromMode()
    {
        // Custom toggle reflects ModeCustom selection. Dropdown selection
        // tracks any catalog entry. When in Custom mode, dropdown shows
        // no selection (the user is in per-key paint mode, not a preset).
        suppressSliderWriteback = true;
        try
        {
            PresetCustom.IsChecked = ViewModel.Mode == LightingProfile.ModeCustom;
            if (ViewModel.Mode == LightingProfile.ModeCustom)
            {
                EffectDropdown.SelectedItem = null;
            }
            else
            {
                // Select the dropdown item whose Code matches Mode.
                foreach (var item in EffectDropdown.Items)
                {
                    if (item is RgbEffectCatalog.Entry entry && entry.Code == ViewModel.Mode)
                    {
                        EffectDropdown.SelectedItem = item;
                        return;
                    }
                }
                // Mode is outside the catalog (shouldn't happen — IsAllowedMode
                // rejects). Leave dropdown unselected so the user notices.
                EffectDropdown.SelectedItem = null;
            }
        }
        finally
        {
            suppressSliderWriteback = false;
        }
    }

    // Populate the dropdown once on first load. Called from constructor /
    // first ApplyCapability. Items are RgbEffectCatalog.Entry records —
    // ComboBox uses DisplayName as the visible text via overriding the
    // item template would be cleaner, but ToString returns the record's
    // synthesised string which is noisy, so we set DisplayMemberPath.
    private bool effectDropdownInitialized;
    private void EnsureEffectDropdownPopulated()
    {
        if (effectDropdownInitialized || EffectDropdown is null) return;
        EffectDropdown.DisplayMemberPath = nameof(RgbEffectCatalog.Entry.DisplayName);
        foreach (var entry in RgbEffectCatalog.All)
        {
            EffectDropdown.Items.Add(entry);
        }
        effectDropdownInitialized = true;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(LightingViewModel.IsDirty):
                RefreshDirtyDot();
                break;
            case nameof(LightingViewModel.IsBrightnessEnabled):
            case nameof(LightingViewModel.IsSpeedEnabled):
                RefreshControlEnables();
                break;
            case nameof(LightingViewModel.StatusMessage):
            case nameof(LightingViewModel.HasStatusMessage):
                RefreshStatusBanner();
                break;
            case nameof(LightingViewModel.Mode):
                RefreshCustomCardVisibility();
                break;
        }
    }

    private void RefreshDirtyDot()
    {
        if (UnsyncedChip is null) return;
        UnsyncedChip.Visibility = ViewModel.IsDirty ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshControlEnables()
    {
        if (BrightnessSlider is null || SpeedSlider is null || SpeedLabel is null) return;
        BrightnessSlider.IsEnabled = ViewModel.IsBrightnessEnabled;
        SpeedSlider.IsEnabled = ViewModel.IsSpeedEnabled;
        SpeedLabel.Opacity = ViewModel.IsSpeedEnabled ? 1.0 : 0.5;
    }

    private void RefreshConnectivity()
    {
        if (SyncButton is null) return;
        bool connected = keyboardManager.IsConnected();
        SyncButton.IsEnabled = connected;
        SyncButton.ToolTip = connected
            ? "Push this profile's lighting settings to the keyboard."
            : "Connect a keyboard to sync.";
    }

    private void RefreshStatusBanner()
    {
        if (StatusBannerText is null || StatusBanner is null) return;
        StatusBannerText.Text = ViewModel.StatusMessage;
        StatusBanner.Visibility = ViewModel.HasStatusMessage ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshValueLabels()
    {
        if (BrightnessValueText is null || SpeedValueText is null) return;
        BrightnessValueText.Text = $"{ViewModel.Brightness} / 9";
        SpeedValueText.Text = $"{ViewModel.Speed} / 9";
    }

    private void RefreshCustomCardVisibility()
    {
        if (CustomCard is null) return;
        CustomCard.Visibility = ViewModel.Mode == LightingProfile.ModeCustom
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (HeaderTitle is not null)
        {
            HeaderTitle.Text = ViewModel.Mode == LightingProfile.ModeCustom
                ? "Per-key painting"
                : "Preset modes";
        }
    }

    private void RefreshSelectionStatus()
    {
        if (SelectionStatus is null) return;
        int n = canvasVm.SelectedKeys.Count;
        if (n == 0)
        {
            SelectionStatus.Text = "Click a key to select. Shift-click for range. Ctrl-click toggles. Drag the background to marquee. Ctrl+A selects all. Esc clears.";
            return;
        }
        if (n == 1)
        {
            var code = canvasVm.SelectedKeys.First();
            var layout = ResolveLayoutFlat();
            var lk = layout.FirstOrDefault(k => k.Code == code);
            var hex = GetKeyHex(lk);
            SelectionStatus.Text = lk is not null
                ? $"1 key selected · {lk.Label} (slot {lk.KeyIndex}){(hex is null ? "" : $" · {hex}")}"
                : $"1 key selected · {code}";
            return;
        }
        // Multi-key: summarise the painted-count vs total
        int painted = 0;
        var buf = PaintCanvas.KeyColors;
        var layoutFlat = ResolveLayoutFlat();
        var codeToKey = new Dictionary<string, LayoutKey>(layoutFlat.Count);
        foreach (var lk in layoutFlat) codeToKey[lk.Code] = lk;
        foreach (var code in canvasVm.SelectedKeys)
        {
            if (!codeToKey.TryGetValue(code, out var lk)) continue;
            if (lk.KeyIndex < 0 || lk.KeyIndex >= 126 || buf is null) continue;
            int off = lk.KeyIndex * 3;
            if (off + 3 > buf.Length) continue;
            if (buf[off] != 0 || buf[off + 1] != 0 || buf[off + 2] != 0) painted++;
        }
        SelectionStatus.Text = $"{n} keys selected · {painted} already painted";
    }

    // When exactly one painted key is selected, populate the colour picker
    // with that key's current paint so the user can immediately tweak +
    // repaint without re-typing the hex. Unpainted single-selects and
    // multi-selects leave the picker alone — overwriting the user's in-
    // progress colour every time the selection changes would be hostile.
    private void SyncPickerFromSingleSelection()
    {
        if (canvasVm.SelectedKeys.Count != 1) return;
        var code = canvasVm.SelectedKeys.First();
        var lk = ResolveLayoutFlat().FirstOrDefault(k => k.Code == code);
        if (lk is null || lk.KeyIndex < 0 || lk.KeyIndex >= 126) return;
        var buf = PaintCanvas.KeyColors;
        if (buf is null) return;
        int off = lk.KeyIndex * 3;
        if (off + 3 > buf.Length) return;
        byte r = buf[off], g = buf[off + 1], b = buf[off + 2];
        if (r == 0 && g == 0 && b == 0) return;
        if (r == activeR && g == activeG && b == activeB) return;
        activeR = r; activeG = g; activeB = b;
        SyncPickerVisualsFromActive();
    }

    private string? GetKeyHex(LayoutKey? lk)
    {
        if (lk is null) return null;
        if (lk.KeyIndex < 0 || lk.KeyIndex >= 126) return null;
        var buf = PaintCanvas.KeyColors;
        if (buf is null) return null;
        int off = lk.KeyIndex * 3;
        if (off + 3 > buf.Length) return null;
        byte r = buf[off], g = buf[off + 1], b = buf[off + 2];
        if (r == 0 && g == 0 && b == 0) return "unpainted";
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    // ---- Effect dropdown + Custom toggle --------------------------------

    private void OnEffectDropdownChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded || suppressSliderWriteback) return;
        if (EffectDropdown.SelectedItem is not RgbEffectCatalog.Entry entry) return;
        if (entry.Code == ViewModel.Mode) return;
        // Picking any preset implicitly clears Custom mode.
        suppressSliderWriteback = true;
        try { PresetCustom.IsChecked = false; } finally { suppressSliderWriteback = false; }
        ViewModel.Mode = entry.Code;
        PersistToProfile();
    }

    private void OnPresetCustomChecked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || suppressSliderWriteback) return;
        if (ViewModel.Mode == LightingProfile.ModeCustom) return;
        // Entering Custom clears any dropdown selection.
        suppressSliderWriteback = true;
        try { EffectDropdown.SelectedItem = null; } finally { suppressSliderWriteback = false; }
        ViewModel.Mode = LightingProfile.ModeCustom;
        PersistToProfile();
    }

    private void OnPresetCustomUnchecked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || suppressSliderWriteback) return;
        if (ViewModel.Mode != LightingProfile.ModeCustom) return;
        // Falling out of Custom defaults back to Off — the user has to pick
        // a preset from the dropdown if they want one. Avoids guessing.
        ViewModel.Mode = LightingProfile.ModeOff;
        PersistToProfile();
    }

    // ---- Top sliders ----------------------------------------------------

    private void BrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (suppressSliderWriteback) return;
        ViewModel.Brightness = (byte)e.NewValue;
        RefreshValueLabels();
        PersistToProfile();
    }

    private void SpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (suppressSliderWriteback) return;
        ViewModel.Speed = (byte)e.NewValue;
        RefreshValueLabels();
        PersistToProfile();
    }

    // ---- Custom-mode picker ---------------------------------------------

    private void RgbSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (suppressSliderWriteback) return;
        activeR = (byte)RSlider.Value;
        activeG = (byte)GSlider.Value;
        activeB = (byte)BSlider.Value;
        SyncPickerVisualsFromActive();
    }

    private void HexInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitHexInput();
            e.Handled = true;
        }
    }

    private void HexInput_LostFocus(object sender, RoutedEventArgs e) => CommitHexInput();

    private void CommitHexInput()
    {
        var text = HexInput.Text?.Trim() ?? string.Empty;
        if (TryParseHexColor(text, out byte r, out byte g, out byte b))
        {
            activeR = r; activeG = g; activeB = b;
            SyncPickerVisualsFromActive();
        }
        else
        {
            // Restore the displayed value so the textbox doesn't show
            // garbage after an invalid entry.
            HexInput.Text = $"#{activeR:X2}{activeG:X2}{activeB:X2}";
        }
    }

    // Accepts "#rgb", "#rrggbb", "rrggbb", with case-insensitive hex. Returns
    // true on success; out params are valid only when result is true. Short-
    // form (#rgb) doubles each nibble like CSS does (#abc → #aabbcc).
    private static bool TryParseHexColor(string raw, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        if (string.IsNullOrEmpty(raw)) return false;
        var s = raw.StartsWith("#") ? raw.Substring(1) : raw;
        if (s.Length == 3)
        {
            if (!byte.TryParse(new string(new[] { s[0], s[0] }), System.Globalization.NumberStyles.HexNumber, null, out r)) return false;
            if (!byte.TryParse(new string(new[] { s[1], s[1] }), System.Globalization.NumberStyles.HexNumber, null, out g)) return false;
            if (!byte.TryParse(new string(new[] { s[2], s[2] }), System.Globalization.NumberStyles.HexNumber, null, out b)) return false;
            return true;
        }
        if (s.Length == 6)
        {
            if (!byte.TryParse(s.Substring(0, 2), System.Globalization.NumberStyles.HexNumber, null, out r)) return false;
            if (!byte.TryParse(s.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out g)) return false;
            if (!byte.TryParse(s.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out b)) return false;
            return true;
        }
        return false;
    }

    private void SyncPickerVisualsFromActive()
    {
        suppressSliderWriteback = true;
        try
        {
            RSlider.Value = activeR;
            GSlider.Value = activeG;
            BSlider.Value = activeB;
            RText.Text = activeR.ToString();
            GText.Text = activeG.ToString();
            BText.Text = activeB.ToString();
            HexInput.Text = $"#{activeR:X2}{activeG:X2}{activeB:X2}";
            CurrentColorSwatch.Background = new SolidColorBrush(Color.FromRgb(activeR, activeG, activeB));

            // Also push the HSL representation. Skip if those sliders
            // haven't been materialized yet (XAML load order).
            if (HSlider is not null && SSlider is not null && LSlider is not null)
            {
                var (h, s, l) = RgbToHsl(activeR, activeG, activeB);
                HSlider.Value = h;
                SSlider.Value = s * 100;
                LSlider.Value = l * 100;
                HText.Text = ((int)h).ToString();
                SText.Text = ((int)(s * 100)).ToString();
                LText.Text = ((int)(l * 100)).ToString();
            }
        }
        finally { suppressSliderWriteback = false; }
    }

    private void HslSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (suppressSliderWriteback) return;
        if (HSlider is null || SSlider is null || LSlider is null) return;
        double h = HSlider.Value;
        double s = SSlider.Value / 100.0;
        double l = LSlider.Value / 100.0;
        var (r, g, b) = HslToRgb(h, s, l);
        activeR = r; activeG = g; activeB = b;
        // Update the RGB sliders + hex + swatch without recursing.
        SyncPickerVisualsFromActive();
    }

    // sRGB byte tuple → HSL. Hue in degrees (0..360), saturation + lightness
    // in [0..1]. Standard formula — see e.g. en.wikipedia.org/wiki/HSL_and_HSV.
    // Used to populate the HSL sliders whenever the RGB representation changes.
    private static (double H, double S, double L) RgbToHsl(byte rByte, byte gByte, byte bByte)
    {
        double r = rByte / 255.0;
        double g = gByte / 255.0;
        double b = bByte / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2.0;
        double h = 0.0, s = 0.0;
        if (max != min)
        {
            double d = max - min;
            s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);
            if (max == r)      h = (g - b) / d + (g < b ? 6.0 : 0.0);
            else if (max == g) h = (b - r) / d + 2.0;
            else               h = (r - g) / d + 4.0;
            h *= 60.0;
        }
        return (h, s, l);
    }

    private static (byte R, byte G, byte B) HslToRgb(double h, double s, double l)
    {
        // Clamp inputs defensively — slider Value coercion plus rounding can
        // sometimes push S/L outside [0,1].
        if (h < 0) h += 360;
        if (h >= 360) h -= 360;
        s = Math.Max(0, Math.Min(1, s));
        l = Math.Max(0, Math.Min(1, l));

        if (s == 0)
        {
            byte gray = (byte)Math.Round(l * 255.0);
            return (gray, gray, gray);
        }
        double q = l < 0.5 ? l * (1.0 + s) : l + s - l * s;
        double p = 2.0 * l - q;
        double hk = h / 360.0;
        double r = HueToRgb(p, q, hk + 1.0 / 3.0);
        double g = HueToRgb(p, q, hk);
        double b = HueToRgb(p, q, hk - 1.0 / 3.0);
        return (
            (byte)Math.Round(r * 255.0),
            (byte)Math.Round(g * 255.0),
            (byte)Math.Round(b * 255.0));
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6.0) return p + (q - p) * 6.0 * t;
        if (t < 1.0 / 2.0) return q;
        if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6.0;
        return p;
    }

    // 12-swatch curated palette. Picked to span common gaming use cases
    // (red/green/blue/yellow accents) and a few muted accents the design
    // system uses elsewhere. Hot-swappable by editing this array — no
    // schema migration needed.
    private static readonly (byte R, byte G, byte B, string Name)[] Palette =
    [
        (0xFF, 0xFF, 0xFF, "White"),
        (0xCC, 0xCC, 0xCC, "Light grey"),
        (0xFF, 0x00, 0x00, "Red"),
        (0xFF, 0x6E, 0x40, "Coral"),
        (0xFF, 0xAB, 0x40, "Amber"),
        (0xFF, 0xEB, 0x3B, "Yellow"),
        (0x00, 0xC8, 0x53, "Green"),
        (0x00, 0xBC, 0xD4, "Cyan"),
        (0x21, 0x96, 0xF3, "Blue"),
        (0x95, 0x75, 0xCD, "DrunkDeer purple"),
        (0xE9, 0x1E, 0x63, "Pink"),
        (0x10, 0x10, 0x10, "Off"),
    ];

    private void BuildPaletteGrid()
    {
        PaletteGrid.Children.Clear();
        foreach (var entry in Palette)
            PaletteGrid.Children.Add(MakeSwatchButton(entry.R, entry.G, entry.B, entry.Name, isRecent: false));
    }

    private void RefreshRecentGrid()
    {
        RecentGrid.Children.Clear();
        // Pad to 12 slots so the strip's height stays stable as colours
        // accumulate.
        int painted = 0;
        foreach (var (r, g, b) in recent)
        {
            RecentGrid.Children.Add(MakeSwatchButton(r, g, b, $"#{r:X2}{g:X2}{b:X2}", isRecent: true));
            painted++;
            if (painted >= MaxRecent) break;
        }
        for (int i = painted; i < MaxRecent; i++)
            RecentGrid.Children.Add(MakePlaceholderSwatch());
    }

    private System.Windows.Controls.Button MakeSwatchButton(byte r, byte g, byte b, string tooltip, bool isRecent)
    {
        var btn = new System.Windows.Controls.Button
        {
            Background = new SolidColorBrush(Color.FromRgb(r, g, b)),
            BorderBrush = (Brush)FindResource("DdBorderThin"),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(2),
            Height = 22,
            Width = 22,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = tooltip,
            Tag = (r, g, b),
        };
        btn.Click += (_, _) =>
        {
            activeR = r; activeG = g; activeB = b;
            SyncPickerVisualsFromActive();
        };
        return btn;
    }

    private System.Windows.Controls.Border MakePlaceholderSwatch() => new()
    {
        Background = (Brush)FindResource("DdSurface1"),
        BorderBrush = (Brush)FindResource("DdBorderThin"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(2),
        Margin = new Thickness(2),
        Height = 22,
        Width = 22,
    };

    private void PushRecent(byte r, byte g, byte b)
    {
        // Don't push pure black as a "recent" — it's the clear colour, not
        // a paint intent.
        if (r == 0 && g == 0 && b == 0) return;
        var entry = (r, g, b);
        recent.RemoveAll(c => c.R == r && c.G == g && c.B == b);
        recent.Insert(0, entry);
        if (recent.Count > MaxRecent) recent.RemoveRange(MaxRecent, recent.Count - MaxRecent);
        SaveRecentToSettings();
        RefreshRecentGrid();
    }

    private void LoadRecentFromSettings()
    {
        recent.Clear();
        try
        {
            var parsed = JsonSerializer.Deserialize<string[]>(settings.RecentLightingColorsJson);
            if (parsed is null) return;
            foreach (var hex in parsed)
            {
                if (TryParseHexColor(hex, out byte r, out byte g, out byte b))
                    recent.Add((r, g, b));
                if (recent.Count >= MaxRecent) break;
            }
        }
        catch { /* malformed settings — start with an empty recent strip */ }
    }

    private void SaveRecentToSettings()
    {
        var hexes = recent.Select(c => $"#{c.R:X2}{c.G:X2}{c.B:X2}").ToArray();
        settings.RecentLightingColorsJson = JsonSerializer.Serialize(hexes);
        settings.Save();
    }

    // ---- Eyedropper -----------------------------------------------------

    // Eyedropper is a plain Button rather than a ToggleButton because the
    // GlassSubtleBtn style targets Button (not ToggleButton), and applying
    // it to a ToggleButton throws at XAML parse time. We track the on/off
    // state in code instead, and reflect it on the canvas + status banner.
    // Toggling adjusts the button's visual hint via opacity + tooltip text.
    private void OnEyedropperClicked(object sender, RoutedEventArgs e)
    {
        if (EyedropperToggle is null) return;
        PaintCanvas.EyedropperMode = !PaintCanvas.EyedropperMode;
        EyedropperToggle.Opacity = PaintCanvas.EyedropperMode ? 1.0 : 0.75;
        EyedropperToggle.ToolTip = PaintCanvas.EyedropperMode
            ? "Eyedropper active — click a painted key to pick its colour. Click again or press Esc to cancel."
            : "Eyedropper: click a painted key to pick its colour.";
        SelectionStatus.Text = PaintCanvas.EyedropperMode
            ? "Eyedropper: click a painted key to pick its colour. Esc cancels."
            : ResolveSelectionStatusText();
    }

    private string ResolveSelectionStatusText()
    {
        int n = canvasVm.SelectedKeys.Count;
        return n switch
        {
            0 => "Click a key to select. Shift-click for range. Ctrl-click toggles. Drag the background to marquee. Ctrl+A selects all. Esc clears.",
            1 => $"1 key selected · {canvasVm.SelectedKeys.First()}",
            _ => $"{n} keys selected",
        };
    }

    private void OnCanvasColorPicked(object? sender, KeyColorPickedEventArgs e)
    {
        activeR = e.R; activeG = e.G; activeB = e.B;
        SyncPickerVisualsFromActive();
        // Exit eyedropper mode after a successful pick — restore the
        // button's normal visual + tooltip.
        PaintCanvas.EyedropperMode = false;
        if (EyedropperToggle is not null)
        {
            EyedropperToggle.Opacity = 0.75;
            EyedropperToggle.ToolTip = "Eyedropper: click a painted key to pick its colour.";
        }
        SelectionStatus.Text = $"Picked colour from {e.Code}: #{e.R:X2}{e.G:X2}{e.B:X2}";
    }

    // ---- Paint actions --------------------------------------------------

    private void OnPaintSelectedClicked(object sender, RoutedEventArgs e)
    {
        if (canvasVm.SelectedKeys.Count == 0)
        {
            ViewModel.StatusMessage = "Select keys first (click, shift-click, ctrl-click, or marquee-drag).";
            return;
        }
        PushUndoSnapshot();
        PaintCanvas.PaintSelected(activeR, activeG, activeB);
        PushRecent(activeR, activeG, activeB);
        PersistKeyColorsToProfile();
        ViewModel.StatusMessage = $"Painted {canvasVm.SelectedKeys.Count} key(s) with #{activeR:X2}{activeG:X2}{activeB:X2}";
    }

    private void OnClearSelectedClicked(object sender, RoutedEventArgs e)
    {
        if (canvasVm.SelectedKeys.Count == 0)
        {
            ViewModel.StatusMessage = "Nothing selected to clear.";
            return;
        }
        PushUndoSnapshot();
        PaintCanvas.ClearKeys(canvasVm.SelectedKeys.ToArray());
        PersistKeyColorsToProfile();
        ViewModel.StatusMessage = $"Cleared {canvasVm.SelectedKeys.Count} key(s) to black.";
    }

    private void OnClearAllClicked(object sender, RoutedEventArgs e)
    {
        PushUndoSnapshot();
        PaintCanvas.ClearAll();
        PersistKeyColorsToProfile();
        ViewModel.StatusMessage = "Cleared all keys.";
    }

    private void OnResetToFactoryClicked(object sender, RoutedEventArgs e)
    {
        PushUndoSnapshot();
        PaintCanvas.ClearAll();
        ViewModel.Mode = LightingProfile.ModeOff;
        UpdatePresetRadioFromMode();
        PersistKeyColorsToProfile();
        PersistToProfile();
        ViewModel.StatusMessage = "Reset to factory: all colours cleared, mode set to Off.";
    }

    private void OnQuickPresetClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button b || b.Tag is not string tag) return;
        PushUndoSnapshot();
        ApplyQuickPreset(tag);
        PersistKeyColorsToProfile();
        ViewModel.StatusMessage = $"Applied preset: {tag}";
    }

    // Resolve the active layout from the connected keyboard and apply a
    // named preset by writing into the canvas's KeyColors buffer.
    //
    // Currently supported (all paint over whatever's currently there — undo
    // is captured before calling):
    //   - rainbow_rows: each row a different hue, full saturation
    //   - gaming_wasd:  WASD + arrows red, everything else dim white
    //   - fps:          WASD + Shift + Ctrl + Space + 1..5 green, rest dim
    //   - mono:         every visible key set to the current paint colour
    //   - modifiers:    paint Type=="mod" keys with current colour
    //   - letters:      paint A..Z with current colour
    //   - gradient:     smooth gradient across the current selection
    private void ApplyQuickPreset(string preset)
    {
        var layout = ResolveLayoutFlat();
        switch (preset)
        {
            case "rainbow_rows":
                ApplyRainbowRows();
                return;
            case "gaming_wasd":
                ApplyGamingWasd();
                return;
            case "fps":
                ApplyFps();
                return;
            case "mono":
                PaintCanvas.PaintKeys(layout.Select(k => k.Code), activeR, activeG, activeB);
                PushRecent(activeR, activeG, activeB);
                return;
            case "modifiers":
                PaintCanvas.PaintKeys(
                    layout.Where(k => k.Type == "mod").Select(k => k.Code),
                    activeR, activeG, activeB);
                PushRecent(activeR, activeG, activeB);
                return;
            case "letters":
                PaintCanvas.PaintKeys(
                    layout.Where(k => k.Code.Length == 1 && char.IsLetter(k.Code[0])).Select(k => k.Code),
                    activeR, activeG, activeB);
                PushRecent(activeR, activeG, activeB);
                return;
            case "gradient":
                ApplyGradientAcrossSelection();
                return;
            case "cyberpunk":
                ApplyCyberpunk();
                return;
            case "sunset":
                ApplySunset();
                return;
            case "ocean":
                ApplyOcean();
                return;
            case "inferno":
                ApplyInferno();
                return;
            case "code_editor":
                ApplyCodeEditor();
                return;
            case "mono_dim":
                PaintCanvas.PaintKeys(layout.Select(k => k.Code), 0x18, 0x18, 0x18);
                return;
        }
    }

    private void ApplyCyberpunk()
    {
        var layout = ResolveLayoutFlat();
        // Base: dark purple
        PaintCanvas.PaintKeys(layout.Select(k => k.Code), 0x28, 0x12, 0x40);
        // WASD + arrows: hot pink
        var wasdArrows = new[] { "w", "a", "s", "d", "up", "down", "left", "right" };
        PaintCanvas.PaintKeys(wasdArrows, 0xFF, 0x10, 0x80);
        // Modifiers: cyan
        PaintCanvas.PaintKeys(
            layout.Where(k => k.Type == "mod").Select(k => k.Code),
            0x00, 0xE5, 0xFF);
    }

    private void ApplySunset()
    {
        var rows = ResolveLayoutRows();
        if (rows is null) return;
        var stops = new (byte R, byte G, byte B)[]
        {
            (0x4A, 0x14, 0x8C),  // deep violet (top row)
            (0xC2, 0x18, 0x5B),  // magenta
            (0xEF, 0x37, 0x3E),  // red
            (0xFF, 0x6E, 0x40),  // coral
            (0xFF, 0xA5, 0x00),  // orange
            (0xFF, 0xEB, 0x3B),  // yellow (bottom)
        };
        for (int i = 0; i < rows.Count; i++)
        {
            var s = stops[i % stops.Length];
            PaintCanvas.PaintKeys(rows[i].Select(k => k.Code), s.R, s.G, s.B);
        }
    }

    private void ApplyOcean()
    {
        var rows = ResolveLayoutRows();
        if (rows is null) return;
        // Top → bottom: deep blue → cyan
        var stops = new (byte R, byte G, byte B)[]
        {
            (0x0D, 0x47, 0xA1),
            (0x19, 0x76, 0xD2),
            (0x21, 0x96, 0xF3),
            (0x4F, 0xC3, 0xF7),
            (0x80, 0xDE, 0xEA),
            (0xB2, 0xEB, 0xF2),
        };
        for (int i = 0; i < rows.Count; i++)
        {
            var s = stops[i % stops.Length];
            PaintCanvas.PaintKeys(rows[i].Select(k => k.Code), s.R, s.G, s.B);
        }
        // White modifiers for contrast
        PaintCanvas.PaintKeys(
            ResolveLayoutFlat().Where(k => k.Type == "mod").Select(k => k.Code),
            0xFA, 0xFA, 0xFA);
    }

    private void ApplyInferno()
    {
        var rows = ResolveLayoutRows();
        if (rows is null) return;
        // Top → bottom: dark red → orange → yellow → white (heatmap-style)
        var stops = new (byte R, byte G, byte B)[]
        {
            (0x4A, 0x00, 0x00),
            (0x8B, 0x00, 0x00),
            (0xD9, 0x00, 0x00),
            (0xFF, 0x6E, 0x40),
            (0xFF, 0xC1, 0x07),
            (0xFF, 0xEB, 0x3B),
        };
        for (int i = 0; i < rows.Count; i++)
        {
            var s = stops[i % stops.Length];
            PaintCanvas.PaintKeys(rows[i].Select(k => k.Code), s.R, s.G, s.B);
        }
    }

    private void ApplyCodeEditor()
    {
        var layout = ResolveLayoutFlat();
        // Base: very dim grey
        PaintCanvas.PaintKeys(layout.Select(k => k.Code), 0x18, 0x18, 0x20);
        // Letters: soft cyan (the "identifiers" colour in many editor themes)
        PaintCanvas.PaintKeys(
            layout.Where(k => k.Code.Length == 1 && char.IsLetter(k.Code[0])).Select(k => k.Code),
            0x80, 0xCB, 0xC4);
        // Digits: warm amber ("literals" / numbers)
        PaintCanvas.PaintKeys(
            layout.Where(k => k.Code.Length == 1 && char.IsDigit(k.Code[0])).Select(k => k.Code),
            0xFF, 0xC1, 0x07);
        // Modifiers: muted blue ("keywords")
        PaintCanvas.PaintKeys(
            layout.Where(k => k.Type == "mod").Select(k => k.Code),
            0x42, 0xA5, 0xF5);
        // Symbols (everything not letter/digit/mod): subtle violet
        PaintCanvas.PaintKeys(
            layout.Where(k => k.Type != "mod"
                              && !(k.Code.Length == 1 && (char.IsLetter(k.Code[0]) || char.IsDigit(k.Code[0]))))
                  .Where(k => k.Code is "minus" or "plus" or "slash_k29" or "brkts_l" or "brkts_r"
                                          or "comma" or "period" or "virgue" or "colon" or "qotatn"
                                          or "swung" or "back" or "tab" or "return"
                                          or "esc" or "caps" or "space")
                  .Select(k => k.Code),
            0x9C, 0x27, 0xB0);
    }

    // ---- Import / Export ------------------------------------------------

    private void OnExportLightingClicked(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "DrunkDeer Lighting (*.lighting.json)|*.lighting.json|JSON files (*.json)|*.json",
            FileName = (activeProfile?.Name ?? "profile") + ".lighting.json",
            Title = "Export lighting",
        };
        if (dlg.ShowDialog(System.Windows.Window.GetWindow(this)) != true) return;

        var snap = ViewModel.Snapshot();
        snap.KeyColors = PaintCanvas.KeyColors ?? Array.Empty<byte>();
        // Compact representation — strip trailing-zero KeyColors when serialising
        // so the file isn't 378 bytes of zeros for "Off" profiles.
        var exportable = new ExportableLighting
        {
            Mode = snap.Mode,
            Brightness = snap.Brightness,
            Speed = snap.Speed,
            // Use sparse dict-of-hex for human-readable diffability.
            KeyColors = SparseKeyColors(snap.KeyColors),
            OutputBrightnessScale = settings.CustomRgbBrightnessScale,
            ExportedAt = DateTime.UtcNow.ToString("o"),
            ExportedFrom = activeProfile?.Name ?? string.Empty,
        };
        try
        {
            var json = JsonSerializer.Serialize(exportable, new JsonSerializerOptions { WriteIndented = true });
            System.IO.File.WriteAllText(dlg.FileName, json);
            ViewModel.StatusMessage = $"Exported lighting to {System.IO.Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"LightingView OnExportLightingClicked: EXCEPTION {ex.GetType().Name}: {ex.Message}");
            ViewModel.StatusMessage = "Export failed — see debug log.";
        }
    }

    private void OnImportLightingClicked(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "DrunkDeer Lighting (*.lighting.json)|*.lighting.json|JSON files (*.json)|*.json|All files|*.*",
            Title = "Import lighting",
        };
        if (dlg.ShowDialog(System.Windows.Window.GetWindow(this)) != true) return;

        try
        {
            var text = System.IO.File.ReadAllText(dlg.FileName);
            var imported = JsonSerializer.Deserialize<ExportableLighting>(text);
            if (imported is null)
            {
                ViewModel.StatusMessage = "Import failed — empty or invalid file.";
                return;
            }

            PushUndoSnapshot();

            // Apply onto the current profile. Mode + brightness + speed go
            // through the VM (which updates the radios and sliders). Key
            // colours go directly to the canvas.
            suppressSliderWriteback = true;
            try
            {
                ViewModel.Mode = imported.Mode.Clamp((byte)0, (byte)127);
                ViewModel.Brightness = imported.Brightness.Clamp((byte)0, (byte)9);
                ViewModel.Speed = imported.Speed.Clamp((byte)1, (byte)9);
                BrightnessSlider.Value = ViewModel.Brightness;
                SpeedSlider.Value = ViewModel.Speed;
                UpdatePresetRadioFromMode();
                RefreshValueLabels();

                var buffer = new byte[126 * 3];
                if (imported.KeyColors is not null)
                {
                    foreach (var kv in imported.KeyColors)
                    {
                        if (!int.TryParse(kv.Key, out int slot)) continue;
                        if (slot < 0 || slot >= 126) continue;
                        if (!TryParseHexColor(kv.Value, out byte r, out byte g, out byte b)) continue;
                        int off = slot * 3;
                        buffer[off] = r; buffer[off + 1] = g; buffer[off + 2] = b;
                    }
                }
                PaintCanvas.SetKeyColors(buffer);

                if (imported.OutputBrightnessScale.HasValue)
                {
                    settings.CustomRgbBrightnessScale = imported.OutputBrightnessScale.Value.Clamp((byte)0, (byte)9);
                    CustomBrightnessSlider.Value = settings.CustomRgbBrightnessScale;
                    if (CustomBrightnessText is not null)
                        CustomBrightnessText.Text = settings.CustomRgbBrightnessScale.ToString();
                    settings.Save();
                }
            }
            finally { suppressSliderWriteback = false; }

            PersistKeyColorsToProfile();
            ViewModel.StatusMessage = $"Imported lighting from {System.IO.Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"LightingView OnImportLightingClicked: EXCEPTION {ex.GetType().Name}: {ex.Message}");
            ViewModel.StatusMessage = "Import failed — see debug log.";
        }
    }

    // Sparse {slot: "#rrggbb"} map of non-black slots. Easier to diff in
    // version control than a 378-byte flat array, and decode is symmetric
    // (parse hex back into the flat array on import).
    private static Dictionary<string, string> SparseKeyColors(byte[] buf)
    {
        var dict = new Dictionary<string, string>();
        if (buf is null) return dict;
        for (int slot = 0; slot * 3 + 2 < buf.Length; slot++)
        {
            byte r = buf[slot * 3], g = buf[slot * 3 + 1], b = buf[slot * 3 + 2];
            if (r == 0 && g == 0 && b == 0) continue;
            dict[slot.ToString()] = $"#{r:X2}{g:X2}{b:X2}";
        }
        return dict;
    }

    // Wire-format-stable lighting export shape. Versionless on purpose —
    // any new fields can be added with default values and old files still
    // deserialise. KeyColors is a sparse {slotIndex: "#RRGGBB"} dict so
    // a "WASD-only red" lighting file is four lines, not 378 bytes.
    private sealed class ExportableLighting
    {
        public byte Mode { get; set; }
        public byte Brightness { get; set; }
        public byte Speed { get; set; }
        public Dictionary<string, string>? KeyColors { get; set; }
        public byte? OutputBrightnessScale { get; set; }
        public string ExportedAt { get; set; } = string.Empty;
        public string ExportedFrom { get; set; } = string.Empty;
    }

    private IReadOnlyList<LayoutKey> ResolveLayoutFlat()
    {
        var model = KeyboardLayoutResolver.Resolve(keyboardManager.KeyboardWithSpecs);
        return KeyboardLayout.VisualFlatFor(model) ?? KeyboardLayout.A75ProFlat;
    }

    private IReadOnlyList<IReadOnlyList<LayoutKey>>? ResolveLayoutRows()
    {
        var model = KeyboardLayoutResolver.Resolve(keyboardManager.KeyboardWithSpecs);
        return KeyboardLayout.VisualFor(model) ?? KeyboardLayout.A75Pro;
    }

    private void ApplyCanvasLayoutForCurrentKeyboard()
    {
        PaintCanvas.SetLayout(ResolveLayoutRows());
    }

    // 6-stop rainbow: red, orange, yellow, green, blue, violet. Each row in
    // the layout gets the next colour in the cycle.
    private static readonly (byte R, byte G, byte B)[] RainbowStops =
    [
        (0xFF, 0x00, 0x00),
        (0xFF, 0xA5, 0x00),
        (0xFF, 0xEB, 0x3B),
        (0x00, 0xC8, 0x53),
        (0x21, 0x96, 0xF3),
        (0x9C, 0x27, 0xB0),
    ];

    private void ApplyRainbowRows()
    {
        var rows = ResolveLayoutRows();
        if (rows is null) return;
        for (int i = 0; i < rows.Count; i++)
        {
            var stop = RainbowStops[i % RainbowStops.Length];
            PaintCanvas.PaintKeys(rows[i].Select(k => k.Code), stop.R, stop.G, stop.B);
        }
    }

    private void ApplyGamingWasd()
    {
        var layout = ResolveLayoutFlat();
        // Base dim white
        PaintCanvas.PaintKeys(layout.Select(k => k.Code), 0x40, 0x40, 0x40);
        // Accent reds for movement
        var accent = new[] { "w", "a", "s", "d", "up", "down", "left", "right", "lshift", "lctrl", "space" };
        PaintCanvas.PaintKeys(accent, 0xFF, 0x00, 0x00);
    }

    private void ApplyFps()
    {
        var layout = ResolveLayoutFlat();
        PaintCanvas.PaintKeys(layout.Select(k => k.Code), 0x20, 0x20, 0x20);
        var accent = new[]
        {
            "w", "a", "s", "d",
            "lshift", "lctrl", "space",
            "1", "2", "3", "4", "5",
            "r", "f", "e", "q",
        };
        PaintCanvas.PaintKeys(accent, 0x00, 0xC8, 0x53);
    }

    private void ApplyGradientAcrossSelection()
    {
        if (canvasVm.SelectedKeys.Count < 2)
        {
            ViewModel.StatusMessage = "Select at least 2 keys to apply a gradient.";
            return;
        }
        var codes = canvasVm.SelectedKeys.ToList();
        // Stable order so the gradient is deterministic regardless of selection
        // history — sort by row-major flat index in the active layout.
        var layout = ResolveLayoutFlat();
        var orderByIndex = new Dictionary<string, int>(layout.Count);
        for (int i = 0; i < layout.Count; i++) orderByIndex[layout[i].Code] = i;
        codes.Sort((a, b) =>
        {
            orderByIndex.TryGetValue(a, out int ai);
            orderByIndex.TryGetValue(b, out int bi);
            return ai.CompareTo(bi);
        });

        // Start: the current paint colour. End: hue-rotated 180° (a rough
        // "complement"). Lerp in RGB — not perceptually uniform but cheap
        // and good enough for a quick-paint preset.
        byte sr = activeR, sg = activeG, sb = activeB;
        byte er = (byte)(255 - sr), eg = (byte)(255 - sg), eb = (byte)(255 - sb);
        int n = codes.Count;
        for (int i = 0; i < n; i++)
        {
            double t = n == 1 ? 0 : (double)i / (n - 1);
            byte r = (byte)Math.Round(sr * (1 - t) + er * t);
            byte g = (byte)Math.Round(sg * (1 - t) + eg * t);
            byte b = (byte)Math.Round(sb * (1 - t) + eb * t);
            PaintCanvas.PaintKeys(new[] { codes[i] }, r, g, b);
        }
    }

    // ---- Undo / Redo ----------------------------------------------------

    private void PushUndoSnapshot()
    {
        var current = PaintCanvas.KeyColors;
        if (current is null || current.Length == 0)
        {
            undoStack.Push(Array.Empty<byte>());
        }
        else
        {
            var copy = new byte[current.Length];
            Array.Copy(current, copy, current.Length);
            undoStack.Push(copy);
        }
        if (undoStack.Count > MaxHistory)
        {
            // Drain the bottom — System.Collections.Generic.Stack doesn't
            // have a built-in "trim from bottom" so reverse + take MaxHistory.
            var keep = undoStack.ToArray().Take(MaxHistory).ToArray();
            undoStack.Clear();
            for (int i = keep.Length - 1; i >= 0; i--) undoStack.Push(keep[i]);
        }
        redoStack.Clear();
        RefreshUndoRedoButtons();
    }

    private void OnUndoClicked(object sender, RoutedEventArgs e)
    {
        if (undoStack.Count == 0) return;
        // Save current to redo before popping undo, so redo can restore it.
        var current = PaintCanvas.KeyColors;
        var snapshot = current is null ? Array.Empty<byte>() : (byte[])current.Clone();
        redoStack.Push(snapshot);

        var prev = undoStack.Pop();
        ApplyKeyColorsSnapshot(prev);
        PersistKeyColorsToProfile();
        RefreshUndoRedoButtons();
        ViewModel.StatusMessage = "Undo.";
    }

    private void OnRedoClicked(object sender, RoutedEventArgs e)
    {
        if (redoStack.Count == 0) return;
        var current = PaintCanvas.KeyColors;
        var snapshot = current is null ? Array.Empty<byte>() : (byte[])current.Clone();
        undoStack.Push(snapshot);

        var next = redoStack.Pop();
        ApplyKeyColorsSnapshot(next);
        PersistKeyColorsToProfile();
        RefreshUndoRedoButtons();
        ViewModel.StatusMessage = "Redo.";
    }

    private void ApplyKeyColorsSnapshot(byte[] snapshot)
    {
        // Replace the canvas's underlying buffer with the snapshot and force
        // a tint refresh. Snapshot length may be 0 (no per-key colours yet)
        // or 378 (full) — SetKeyColors handles both.
        var copy = new byte[Math.Max(snapshot.Length, 126 * 3)];
        if (snapshot.Length > 0) Array.Copy(snapshot, copy, Math.Min(snapshot.Length, copy.Length));
        PaintCanvas.SetKeyColors(copy);
    }

    private void RefreshUndoRedoButtons()
    {
        if (UndoBtn is null || RedoBtn is null) return;
        UndoBtn.IsEnabled = undoStack.Count > 0;
        RedoBtn.IsEnabled = redoStack.Count > 0;
    }

    // ---- Keyboard shortcuts --------------------------------------------

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel.Mode != LightingProfile.ModeCustom) return;
        bool ctrl = (KeyboardKey.Modifiers & ModifierKeys.Control) != 0;
        if (ctrl && e.Key == Key.Z)
        {
            OnUndoClicked(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (ctrl && (e.Key == Key.Y || (e.Key == Key.Z && (KeyboardKey.Modifiers & ModifierKeys.Shift) != 0)))
        {
            OnRedoClicked(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.Enter)
        {
            // Quick-paint: Ctrl+Enter applies current colour to selection.
            OnPaintSelectedClicked(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    // ---- Custom-mode brightness scale ----------------------------------

    private void CustomBrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (suppressSliderWriteback) return;
        byte v = (byte)e.NewValue;
        settings.CustomRgbBrightnessScale = v;
        settings.Save();
        if (CustomBrightnessText is not null) CustomBrightnessText.Text = v.ToString();
    }

    // ---- Persistence ----------------------------------------------------

    private void PersistToProfile()
    {
        if (activeProfile is null || profileManager is null) return;
        var snap = ViewModel.Snapshot();
        // Preserve the existing KeyColors when persisting from preset
        // sliders (they don't touch per-key data).
        if (activeProfile.LightingProfile is { } prior)
            snap.KeyColors = prior.KeyColors;
        activeProfile.LightingProfile = snap;
        profileManager.ScheduleSave(activeProfile);
    }

    // Persist the current canvas KeyColors buffer onto the active profile.
    // Allocates a fresh LightingProfile if one doesn't exist yet so first-
    // paint doesn't silently lose the bytes. Also bumps the VM's paint-
    // revision so the Unsynced chip lights up — IsDirty is purely
    // mode/brightness/speed-driven, otherwise it'd miss colour-only edits.
    private void PersistKeyColorsToProfile()
    {
        if (activeProfile is null || profileManager is null) return;
        var snap = ViewModel.Snapshot();
        snap.KeyColors = PaintCanvas.KeyColors ?? Array.Empty<byte>();
        activeProfile.LightingProfile = snap;
        profileManager.ScheduleSave(activeProfile);
        ViewModel.MarkPaintDirty();
    }

    // ---- Sync + Lights off ---------------------------------------------

    private async void OnSyncClicked(object sender, RoutedEventArgs e)
    {
        // First-use preset warning (existing). Applies to any RGB packet.
        if (!settings.RgbFirstSyncAcknowledged)
        {
            var prelim = BrickWarningDialog.ShowFirstSyncWarning(System.Windows.Window.GetWindow(this));
            if (!prelim) return;
            settings.RgbFirstSyncAcknowledged = true;
            settings.Save();
        }

        // Second-level gate specific to Custom: per-key writes are an
        // additional risk surface; ask once even after the preset warning
        // has been dismissed.
        if (ViewModel.Mode == LightingProfile.ModeCustom && !settings.RgbCustomBrickAcknowledged)
        {
            var ok = BrickWarningDialog.ShowCustomModeWarning(System.Windows.Window.GetWindow(this));
            if (!ok) return;
            settings.RgbCustomBrickAcknowledged = true;
            settings.Save();
        }

        // Third-level gate: catalog entries the official driver emits but we
        // haven't live-tested on hardware. The full 19-mode list comes from
        // the same JS bundle that powers drunkdeer.com so risk is low, but
        // a one-shot ack covers us. Skipped for the verified-safe presets
        // and for Custom (which has its own dedicated ack above).
        if (ViewModel.Mode != LightingProfile.ModeCustom
            && !RgbEffectCatalog.IsVerifiedSafe(ViewModel.Mode)
            && !settings.RgbUnverifiedModeAcknowledged)
        {
            var ok = BrickWarningDialog.ShowUnverifiedModeWarning(System.Windows.Window.GetWindow(this));
            if (!ok) return;
            settings.RgbUnverifiedModeAcknowledged = true;
            settings.Save();
        }

        ViewModel.StatusMessage = string.Empty;
        bool wrote;
        if (ViewModel.Mode == LightingProfile.ModeCustom)
        {
            wrote = await SyncCustomAsync();
        }
        else
        {
            var packet = Packets.BuildLedModePacket(ViewModel.Snapshot());
            wrote = await WriteRgbPacketAsync(packet);
        }

        if (wrote)
        {
            ViewModel.MarkSynced();
            ViewModel.StatusMessage = ViewModel.Mode == LightingProfile.ModeCustom
                ? $"Synced custom lighting · {CountPaintedKeys()} painted key(s) · output brightness {settings.CustomRgbBrightnessScale}/9"
                : $"Synced: mode={ViewModel.Mode} brightness={ViewModel.Brightness} speed={ViewModel.Speed}";
        }
        else
        {
            ViewModel.StatusMessage = keyboardManager.IsConnected()
                ? "Sync failed — packet write did not complete."
                : "No keyboard connected.";
        }
    }

    // Build the per-key packet batch and fire each frame at the firmware.
    // Uses Settings.CustomRgbBrightnessScale as the emitted brightness
    // (independent of LightingProfile.Brightness which would have been
    // 0..9 for preset modes only — here the user has a dedicated slider
    // that doesn't perturb the saved profile colours).
    private async Task<bool> SyncCustomAsync()
    {
        if (keyboardManager.KeyboardWithSpecs is not { } keyboard) return false;

        // Build a transient LightingProfile so we can keep the wire builder
        // signature stable. KeyColors comes from the canvas; brightness
        // comes from the per-user output slider (Settings).
        var send = new LightingProfile
        {
            Mode       = LightingProfile.ModeCustom,
            Brightness = settings.CustomRgbBrightnessScale,
            Speed      = ViewModel.Speed,
            KeyColors  = PaintCanvas.KeyColors ?? Array.Empty<byte>(),
        };
        var layout = ResolveLayoutFlat();
        int packetCount = ResolveRgbPacketCount();

        return await Task.Run(() =>
        {
            try
            {
                using HidStream stream = keyboard.Keyboard.Open();
                var packets = Packets.BuildPerKeyRgbPackets(send, layout, packetCount);
                bool allOk = true;
                for (int i = 0; i < packets.Length; i++)
                {
                    // WritePacketNoAck mirrors the JS sendTurboLedModeData
                    // path — fire each packet, don't wait for ack (RGB
                    // writes don't ack on hardware).
                    if (!stream.WritePacketNoAck(packets[i]))
                    {
                        DebugLogger.Log($"LightingView SyncCustomAsync: packet {i + 1}/{packets.Length} write failed");
                        allOk = false;
                    }
                    // Inter-packet delay. The official JS scheduler emits
                    // these with setTimeout(…, 0) between each send (per
                    // docs/rgb-protocol.md, JS 32326), which gives ~1-4 ms
                    // of event-loop spacing per packet. A tight synchronous
                    // loop here fires all 7 within microseconds — the
                    // firmware's input buffer can't drain that fast and
                    // silently drops some, manifesting as "spam Sync to
                    // make all keys light up" and "some keys never light".
                    // 15 ms is a conservative replacement (~105 ms total
                    // sync wall time — imperceptible to user). Skip the
                    // delay after the last packet to keep sync responsive.
                    if (i < packets.Length - 1)
                    {
                        System.Threading.Thread.Sleep(15);
                    }
                }
                return allOk;
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"LightingView SyncCustomAsync: EXCEPTION {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }).ConfigureAwait(true);
    }

    // 6 packets for G60* / G65* / G65lite, 7 everywhere else.  Sourced from
    // the official JS `transmit_color_report_packet` model-family hardcode
    // (docs/rgb-protocol.md §85-94). Centralised here so future models can
    // override based on the resolved KeyboardModel.
    private int ResolveRgbPacketCount()
    {
        var model = KeyboardLayoutResolver.Resolve(keyboardManager.KeyboardWithSpecs);
        if (model is null) return 7;
        return model.DisplayName.StartsWith("G60") || model.DisplayName.StartsWith("G65") ? 6 : 7;
    }

    private int CountPaintedKeys()
    {
        var buf = PaintCanvas.KeyColors;
        if (buf is null || buf.Length == 0) return 0;
        int n = 0;
        for (int i = 0; i + 3 <= buf.Length; i += 3)
        {
            if (buf[i] != 0 || buf[i + 1] != 0 || buf[i + 2] != 0) n++;
        }
        return n;
    }

    private async void OnLightsOffClicked(object sender, RoutedEventArgs e)
    {
        // Decision rule: panic-off does NOT mutate the saved profile.
        var probe = new LightingProfile { Mode = LightingProfile.ModeOff, Brightness = 0, Speed = 0 };
        var packet = Packets.BuildLedModePacket(probe);
        ViewModel.StatusMessage = string.Empty;
        bool ok = await WriteRgbPacketAsync(packet);
        if (ok)
        {
            ViewModel.MarkExternalPush(probe);
            ViewModel.StatusMessage = "Lights off sent.";
        }
        else
        {
            ViewModel.StatusMessage = keyboardManager.IsConnected()
                ? "Lights off failed — packet write did not complete."
                : "No keyboard connected.";
        }
    }

    private async Task<bool> WriteRgbPacketAsync(byte[] packet)
    {
        if (keyboardManager.KeyboardWithSpecs is not { } keyboard) return false;
        return await Task.Run(() =>
        {
            try
            {
                using HidStream stream = keyboard.Keyboard.Open();
                return stream.WritePacketNoAck(packet);
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"LightingView WriteRgbPacketAsync: EXCEPTION {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }).ConfigureAwait(true);
    }
}
