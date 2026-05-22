using System.ComponentModel;
using System.IO;
using System.Text.Json.Serialization;
using System.Text.Json;
using Path = System.IO.Path;

namespace WpfApp;

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
    private bool hotkeyHintDismissed = false;

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
