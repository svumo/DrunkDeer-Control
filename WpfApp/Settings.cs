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
    private int quickSwitchKey = 35; // VK_END
    [JsonIgnore]
    private int quickSwitchModifiers = 0x02; // MOD_CONTROL
    [JsonIgnore]
    private bool usageStatsEnabled = true; // default ON; user can opt out in Options
    [JsonIgnore]
    private DateTime lastUsageReport = DateTime.MinValue;

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

    // VK code for the quick-switch hotkey key (default End = 35)
    public int QuickSwitchKey
    {
        get { return quickSwitchKey; }
        set { SetField(ref quickSwitchKey, value, nameof(QuickSwitchKey)); }
    }

    // Modifier flags (MOD_ALT=1, MOD_CONTROL=2, MOD_SHIFT=4)
    public int QuickSwitchModifiers
    {
        get { return quickSwitchModifiers; }
        set { SetField(ref quickSwitchModifiers, value, nameof(QuickSwitchModifiers)); }
    }

    // Anonymous usage stats — fires once-per-day from UsageReporter.cs.
    // Toggle in Options → Settings. Default ON; existing settings.json files
    // missing this key deserialize as ON (which is fine — the user can flip it
    // before the first ping fires).
    public bool UsageStatsEnabled
    {
        get { return usageStatsEnabled; }
        set { SetField(ref usageStatsEnabled, value, nameof(UsageStatsEnabled)); }
    }

    // UTC timestamp of the last successful telemetry POST. Drives the 24h
    // dedup interval in UsageReporter.ReportIfDueAsync.
    public DateTime LastUsageReport
    {
        get { return lastUsageReport; }
        set { SetField(ref lastUsageReport, value, nameof(LastUsageReport)); }
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
