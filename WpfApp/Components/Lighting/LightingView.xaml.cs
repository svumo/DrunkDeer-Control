using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Driver;
using HidSharp;
using WpfApp.Profile;
using MessageBox = System.Windows.Forms.MessageBox;
using MessageBoxButtons = System.Windows.Forms.MessageBoxButtons;
using MessageBoxIcon = System.Windows.Forms.MessageBoxIcon;
using MessageBoxDefaultButton = System.Windows.Forms.MessageBoxDefaultButton;
using DialogResult = System.Windows.Forms.DialogResult;

namespace WpfApp.Components.Lighting;

// Phase 1 RGB lighting editor. Pushes preset mode / brightness / speed only
// — no color, no per-key painting. Sync runs through its own short-lived
// HID open, never via ProfileManager.PushCurrentProfileAsync, so swapping
// profiles never blinks the LEDs (RGB write is firmware-inherently flashy).
public partial class LightingView : System.Windows.Controls.UserControl
{
    private readonly KeyboardManager keyboardManager;
    private readonly Settings settings;
    private ProfileManager? profileManager;
    private ProfileItem? activeProfile;
    // Starts true to swallow ValueChanged events fired during InitializeComponent
    // — when WPF coerces Slider.Value from the XAML default to the (non-zero)
    // Minimum, ValueChanged fires before the rest of the named elements exist.
    // The handler reaches RefreshValueLabels, hits a null x:Name, and the
    // exception bubbles out of the constructor leaving _lightingView=null.
    // Flipped to false at the end of the constructor.
    private bool suppressSliderWriteback = true;

    public LightingViewModel ViewModel { get; } = new();

    public LightingView(KeyboardManager keyboardManager, Settings settings)
    {
        this.keyboardManager = keyboardManager;
        this.settings = settings;
        InitializeComponent();
        DataContext = ViewModel;
        ViewModel.PropertyChanged += OnViewModelChanged;
        keyboardManager.ConnectedKeyboardChanged += _ => Dispatcher.Invoke(RefreshConnectivity);
        RefreshConnectivity();
        RefreshDirtyDot();
        RefreshControlEnables();
        // XAML parse is done; allow user slider edits to flow into the VM.
        suppressSliderWriteback = false;
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
        // suppressSliderWriteback prevents the slider's ValueChanged from
        // looping back and re-marking the profile dirty during hydration.
        suppressSliderWriteback = true;
        try
        {
            ViewModel.Hydrate(item.LightingProfile);
            BrightnessSlider.Value = ViewModel.Brightness;
            SpeedSlider.Value = ViewModel.Speed;
            UpdatePresetRadioFromMode();
        }
        finally { suppressSliderWriteback = false; }
        RefreshDirtyDot();
        RefreshControlEnables();
        RefreshValueLabels();
        // Status banner is per-action transient — clear it on profile switch
        // so a stale "Write failed" doesn't carry over to a different profile.
        ViewModel.StatusMessage = string.Empty;
    }

    private void UpdatePresetRadioFromMode()
    {
        PresetOff.IsChecked       = ViewModel.Mode == LightingProfile.ModeOff;
        PresetAlwaysOn.IsChecked  = ViewModel.Mode == LightingProfile.ModeAlwaysOn;
        PresetBreath.IsChecked    = ViewModel.Mode == LightingProfile.ModeBreath;
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
        }
    }

    // Refresh* helpers are no-ops if their target named elements haven't been
    // materialized yet — they get called from event handlers that can fire
    // mid-InitializeComponent (e.g. a Slider's Minimum coercion).
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

    private void OnPresetChecked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || suppressSliderWriteback) return;
        if (sender is not System.Windows.Controls.RadioButton rb || rb.IsChecked != true) return;
        byte newMode = rb.Name switch
        {
            nameof(PresetOff)      => LightingProfile.ModeOff,
            nameof(PresetAlwaysOn) => LightingProfile.ModeAlwaysOn,
            nameof(PresetBreath)   => LightingProfile.ModeBreath,
            _ => ViewModel.Mode,
        };
        if (newMode == ViewModel.Mode) return;
        ViewModel.Mode = newMode;
        PersistToProfile();
    }

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

    private void PersistToProfile()
    {
        if (activeProfile is null || profileManager is null) return;
        activeProfile.LightingProfile = ViewModel.Snapshot();
        profileManager.ScheduleSave(activeProfile);
    }

    private async void OnSyncClicked(object sender, RoutedEventArgs e)
    {
        if (!settings.RgbFirstSyncAcknowledged)
        {
            var result = MessageBox.Show(
                "First time syncing RGB lighting.\n\n" +
                "Sending invalid RGB data can soft-brick this keyboard, recoverable only by spamming correct data during the boot loop. The values you've selected are within the safe range, but treat this as a one-way door.\n\n" +
                "Recovery procedure: docs/rgb-protocol.md → \"Soft-brick recovery\".\n\n" +
                "Continue?",
                "RGB sync — first use",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (result != DialogResult.OK) return;
            settings.RgbFirstSyncAcknowledged = true;
        }

        var packet = Packets.BuildLedModePacket(ViewModel.Snapshot());
        ViewModel.StatusMessage = string.Empty;
        bool ok = await WriteRgbPacketAsync(packet);
        if (ok)
        {
            ViewModel.MarkSynced();
            ViewModel.StatusMessage = $"Synced: mode={ViewModel.Mode} brightness={ViewModel.Brightness} speed={ViewModel.Speed}";
        }
        else
        {
            ViewModel.StatusMessage = keyboardManager.IsConnected()
                ? "Sync failed — packet write did not complete."
                : "No keyboard connected.";
        }
    }

    private async void OnLightsOffClicked(object sender, RoutedEventArgs e)
    {
        // Decision rule: panic-off does NOT mutate the saved profile. The
        // packet we build uses Mode=Off + the profile's stored brightness/speed
        // values, but the snapshot is built locally and discarded after send.
        var probe = new LightingProfile { Mode = LightingProfile.ModeOff, Brightness = 0, Speed = 0 };
        var packet = Packets.BuildLedModePacket(probe);
        ViewModel.StatusMessage = string.Empty;
        bool ok = await WriteRgbPacketAsync(packet);
        if (ok)
        {
            // Hardware is now in Off state — page state stays (per panic-off
            // rule), but the dirty calculation should reflect the new hw state.
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
                // WritePacketNoAck matches the official Antler driver's
                // fire-and-forget RGB behaviour. RGB writes do not ack on
                // hardware; waiting for one would exhaust the drain on
                // unrelated input reports.
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
