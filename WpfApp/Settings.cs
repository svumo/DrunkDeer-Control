using System.ComponentModel;
using System.IO;
using System.Text.Json.Serialization;
using System.Text.Json;
using Path = System.IO.Path;

namespace WpfApp;

public enum TopTab
{
    Keyboard = 0,
    Lighting = 1,
}

public record Settings() : INotifyPropertyChanged
{
    private static readonly JsonSerializerOptions options = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private static readonly string SETTINGS_FILE_PATH = Path.Combine(Program.APP_DIR, "settings.json");

    [JsonIgnore]
    private string lastProfileUsedName = string.Empty;
    [JsonIgnore]
    private DateTime lastFirmwareCheck = DateTime.MinValue;
    [JsonIgnore]
    private string latestKnownFirmwareJson = "{}";
    [JsonIgnore]
    private string lastWinPairsJson = "[]";
    [JsonIgnore]
    private string releaseDualTriggerPairsJson = "[]";
    [JsonIgnore]
    private bool hotkeyHintDismissed = false;
    [JsonIgnore]
    private string lastKnownFirmwareByPidJson = "{}";
    [JsonIgnore]
    private string firmwareTooOldAckByPidJson = "{}";
    [JsonIgnore]
    private TopTab activeTopTab = TopTab.Keyboard;
    [JsonIgnore]
    private bool rgbFirstSyncAcknowledged = false;
    [JsonIgnore]
    private bool rgbCustomBrickAcknowledged = false;
    [JsonIgnore]
    private bool rgbUnverifiedModeAcknowledged = false;
    [JsonIgnore]
    private string recentLightingColorsJson = "[]";
    [JsonIgnore]
    private byte customRgbBrightnessScale = 9;

    [JsonIgnore]
    public bool IsDirty { get; set; }
    [JsonIgnore]
    public bool SaveOnDirty { get; set; } = false;
    public event PropertyChangedEventHandler? PropertyChanged;

    public string LastProfileUsedName
    {
        get { return lastProfileUsedName; }
        set { SetField(ref lastProfileUsedName, value, nameof(LastProfileUsedName)); }
    }

    // UTC timestamp of the last successful firmware-version poll
    // (FirmwareUpdateChecker.CheckIfDueAsync). Drives the 24h re-check
    // interval — we don't want to hammer the worker every launch.
    public DateTime LastFirmwareCheck
    {
        get { return lastFirmwareCheck; }
        set { SetField(ref lastFirmwareCheck, value, nameof(LastFirmwareCheck)); }
    }

    // Serialised Dictionary<string, string> of pid → version (e.g. "0x2383"
    // → "0x000a"). Cached from the last /firmware response so we can decide
    // whether to show the banner without making a network call on every
    // launch. Defaults to "{}" so JsonSerializer.Deserialize never throws.
    public string LatestKnownFirmwareJson
    {
        get { return latestKnownFirmwareJson; }
        set { SetField(ref latestKnownFirmwareJson, value, nameof(LatestKnownFirmwareJson)); }
    }

    // User-defined Last Win pairs. JSON-serialised array of [mainSlot, triggerSlot]
    // tuples; bidirectional behaviour is achieved by emitting both (a,b) and
    // (b,a) to the firmware. Empty by default — the LW master toggle without
    // pairs does nothing observable on hardware (firmware needs the explicit
    // pair table).
    public string LastWinPairsJson
    {
        get { return lastWinPairsJson; }
        set { SetField(ref lastWinPairsJson, value, nameof(LastWinPairsJson)); }
    }

    // User-defined Release-Dual-Trigger pairs. JSON-serialised array of
    // [pressSlot, releaseSlot] tuples; order matters (press = HID on press,
    // release = HID on release transition). Empty by default — the RDT
    // master toggle without pairs does nothing observable on hardware.
    public string ReleaseDualTriggerPairsJson
    {
        get { return releaseDualTriggerPairsJson; }
        set { SetField(ref releaseDualTriggerPairsJson, value, nameof(ReleaseDualTriggerPairsJson)); }
    }

    // Suppress the "use Alt+key for fewer conflicts" tip ONLY after the
    // user explicitly ticks "Don't show again". Default behaviour is to
    // show the tip every time a hotkey-recording chip is opened, so users
    // who skim the first dialog still see it later. Renamed from
    // HotkeyHintShown (which was auto-stamped on first display) so any
    // stale `true` from the older build gets ignored — System.Text.Json
    // drops unknown JSON fields.
    public bool HotkeyHintDismissed
    {
        get { return hotkeyHintDismissed; }
        set { SetField(ref hotkeyHintDismissed, value, nameof(HotkeyHintDismissed)); }
    }

    // Last-seen firmware version per USB PID, serialised as a JSON map of
    // PID-hex → firmware-hex (e.g. {"0x2391":"0x0017"}). Stamped on every
    // successful connect. KeyboardPerformanceView surfaces a one-time
    // banner when the connected keyboard's firmware doesn't match the
    // stored value — RDT/LW pair tables don't always transfer cleanly
    // across firmware versions, so the user gets a heads-up to delete +
    // recreate if pairs misbehave. See Known Issues window entry #5.
    public string LastKnownFirmwareByPidJson
    {
        get { return lastKnownFirmwareByPidJson; }
        set { SetField(ref lastKnownFirmwareByPidJson, value, nameof(LastKnownFirmwareByPidJson)); }
    }

    // Convenience accessors so callers don't have to deal with the JSON
    // wrapping. Both swallow malformed-JSON exceptions and treat them as
    // "no record" — the worst case is the banner shows once on every
    // launch until the dict is rewritten with valid JSON.
    public string? GetLastKnownFirmware(int productId)
    {
        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(lastKnownFirmwareByPidJson);
            if (map is null) return null;
            return map.TryGetValue($"0x{productId:x4}", out var version) ? version : null;
        }
        catch (JsonException) { return null; }
    }

    public void SetLastKnownFirmware(int productId, string firmwareHex)
    {
        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(lastKnownFirmwareByPidJson)
                      ?? new Dictionary<string, string>();
            map[$"0x{productId:x4}"] = firmwareHex;
            LastKnownFirmwareByPidJson = JsonSerializer.Serialize(map);
        }
        catch (JsonException)
        {
            // Rewrite from scratch on parse failure.
            var fresh = new Dictionary<string, string> { [$"0x{productId:x4}"] = firmwareHex };
            LastKnownFirmwareByPidJson = JsonSerializer.Serialize(fresh);
        }
    }

    // Per-PID "user clicked Continue anyway on the too-old-firmware modal"
    // stamp. Value is the firmware hex acknowledged — re-acked the next
    // time firmware changes (because the key value won't match anymore).
    // Pair to FirmwareTooOldDialog. Mirror of LastKnownFirmware
    // get/set above.
    public string FirmwareTooOldAckByPidJson
    {
        get { return firmwareTooOldAckByPidJson; }
        set { SetField(ref firmwareTooOldAckByPidJson, value, nameof(FirmwareTooOldAckByPidJson)); }
    }

    public bool IsFirmwareTooOldAcknowledged(int productId, string firmwareHex)
    {
        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(firmwareTooOldAckByPidJson);
            if (map is null) return false;
            return map.TryGetValue($"0x{productId:x4}", out var acked)
                   && string.Equals(acked, firmwareHex, StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException) { return false; }
    }

    public void SetFirmwareTooOldAcknowledged(int productId, string firmwareHex)
    {
        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(firmwareTooOldAckByPidJson)
                      ?? new Dictionary<string, string>();
            map[$"0x{productId:x4}"] = firmwareHex;
            FirmwareTooOldAckByPidJson = JsonSerializer.Serialize(map);
        }
        catch (JsonException)
        {
            var fresh = new Dictionary<string, string> { [$"0x{productId:x4}"] = firmwareHex };
            FirmwareTooOldAckByPidJson = JsonSerializer.Serialize(fresh);
        }
    }

    // Last-active top-level tab. Restored on launch so Lighting-focused users
    // don't have to re-click. Unknown enum values from a future version
    // deserialize to Keyboard (default) thanks to JsonStringEnumConverter +
    // the post-load sanity check.
    public TopTab ActiveTopTab
    {
        get { return activeTopTab; }
        set { SetField(ref activeTopTab, value, nameof(ActiveTopTab)); }
    }

    // First-use brick warning. Stays false until the user clicks Continue on
    // the modal that fires the FIRST time they press Sync on the Lighting
    // page. Once true, the modal never appears again on this install.
    public bool RgbFirstSyncAcknowledged
    {
        get { return rgbFirstSyncAcknowledged; }
        set { SetField(ref rgbFirstSyncAcknowledged, value, nameof(RgbFirstSyncAcknowledged)); }
    }

    // Second-level acknowledgement specific to per-key custom RGB (Mode=0x13).
    // Per-key writes are a separately-unverified path on A75 Pro hardware
    // (docs/rgb-protocol.md §200), so even after the preset-sync warning
    // has been dismissed the user gets one more confirmation before the
    // first custom packet leaves the host. Once true, never asked again.
    public bool RgbCustomBrickAcknowledged
    {
        get { return rgbCustomBrickAcknowledged; }
        set { SetField(ref rgbCustomBrickAcknowledged, value, nameof(RgbCustomBrickAcknowledged)); }
    }

    // One-shot acknowledgement before the first sync of any preset mode
    // outside the hardware-verified-safe set (RgbEffectCatalog.IsVerifiedSafe).
    // The full 19-mode catalog comes from the official driver's JS bundle
    // so it should work, but the verified set is the only one we've
    // confirmed doesn't brick. Once acked, all unverified modes pass.
    public bool RgbUnverifiedModeAcknowledged
    {
        get { return rgbUnverifiedModeAcknowledged; }
        set { SetField(ref rgbUnverifiedModeAcknowledged, value, nameof(RgbUnverifiedModeAcknowledged)); }
    }

    // Recent custom-paint colours, MRU-first, as a JSON array of "#rrggbb"
    // strings. Capped to 12 entries by LightingView. Persisted across
    // sessions so the user's recent palette survives app restart.
    public string RecentLightingColorsJson
    {
        get { return recentLightingColorsJson; }
        set { SetField(ref recentLightingColorsJson, value, nameof(RecentLightingColorsJson)); }
    }

    // Global brightness scale for Custom mode (0..9). Multiplies the stored
    // per-key RGB at packet-build time without mutating LightingProfile.
    // KeyColors — lets the user dim/brighten the whole keyboard without
    // losing the colour relationships they painted. Defaults to 9 = no
    // attenuation.
    public byte CustomRgbBrightnessScale
    {
        get { return customRgbBrightnessScale; }
        set { SetField(ref customRgbBrightnessScale, value, nameof(CustomRgbBrightnessScale)); }
    }

    protected void SetField<T>(ref T field, T value, string propertyName)
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            IsDirty = true;
            OnPropertyChanged(propertyName);
        }
    }

    protected virtual void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        if (SaveOnDirty && IsDirty)
        {
            Save();
        }
        IsDirty = false;
    }

    public void Save()
    {
        var json = JsonSerializer.Serialize(this, options);
        File.WriteAllText(SETTINGS_FILE_PATH, json);
        Console.WriteLine("Saving settings {0}", SETTINGS_FILE_PATH);
    }

    public static Settings? FromFile()
    {
        if (!File.Exists(SETTINGS_FILE_PATH)) return null;
        var text = File.ReadAllText(SETTINGS_FILE_PATH);
        try
        {
            var settings = JsonSerializer.Deserialize<Settings>(text, options);
            if (settings != null)
            {
                settings.IsDirty = false;
                settings.SaveOnDirty = true;
            }
            return settings;
        }
        catch (JsonException) { Console.WriteLine("Failed to deserialize settings file at {0}", SETTINGS_FILE_PATH); }
        return null;
    }
}
