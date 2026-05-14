using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Driver;

/*
 * {
        className: string;
        value: number;
        name: string;
        isBorder: boolean;
        height: number;
        keyname: string;
        action_point: number;
        downstroke: number;
        upstroke: number;
        rdt: boolean;
    }
 */
public abstract record RapidTriggerPlusKey
{
    // Dont save bloat - but web export may include these
    public string? ClassName { get; set; }
    public string? Name { get; set; }
    public bool? IsBorder { get; set; }
    public int? Height { get; set; }
    public int? Value { get; set; }
    public string Keyname { get; set; } = string.Empty;
    public decimal Action_Point { get; set; }
    public decimal Downstroke { get; set; }
    public decimal Upstroke { get; set; }
}

public sealed record ReleaseDoubleTriggerKey : RapidTriggerPlusKey
{
    public bool Rdt { get; set; }
}

public sealed record LastWinTriggerKey : RapidTriggerPlusKey
{
    public bool? Rdt { get; set; }
    public object? Rdt_ { get; set; } // ReleaseDoubleTriggerKey? property loop?
    public int LwIndex { get; set; }
}
/*
 * currentRDT: {
    isRdtEnabled: boolean;
    mainKey: {
        className: string;
        value: number;
        name: string;
        isBorder: boolean;
        height: number;
        keyname: string;
        action_point: number;
        downstroke: number;
        upstroke: number;
        rdt: boolean;
    };
    triggerKey: {
        className: string;
        value: number;
        name: string;
        isBorder: boolean;
        height: number;
        keyname: string;
        action_point: number;
        downstroke: number;
        upstroke: number;
        rdt: boolean;
    }
    x2Reset: number;
    x2Active: number;
} 
 */
public sealed record ReleaseDoubleTriggerRapidTriggerPlusSetting
{
    public bool IsRdtEnabled { get; set; }
    public ReleaseDoubleTriggerKey? MainKey { get; set; }
    public ReleaseDoubleTriggerKey? TriggerKey { get; set; }
    public decimal X2Reset { get; set; }
    public decimal X2Active { get; set; }
    public decimal Y2Active { get; set; }
}

public sealed record LastWinRapidTriggerPlusSetting
{
    public bool IsRdtEnabled { get; set; }
    public ReleaseDoubleTriggerKey? MainKey { get; set; }
    public ReleaseDoubleTriggerKey? TriggerKey { get; set; }
}

public sealed record RapidTriggerPlus
{
    public ReleaseDoubleTriggerRapidTriggerPlusSetting[] Rdt_RtpSettings { get; set; } = [];
    public bool Rdt_Watch_Change { get; set; }
    public LastWinTriggerKey[][] Lw_Temp_list { get; set; } = [];
    public LastWinRapidTriggerPlusSetting[] Lw_RtpSettings { get; set; } = [];
    public bool Lw_Watch_Change { get; set; }
    public string Rtp_Model { get; set; } = string.Empty;

    // Web export includes these, but we ignore them
    public bool Rdt_Open { get; set; }
    public bool Lw_Open { get; set; }
}

public sealed record KeySetting
{
    public string KeyName { get; set; } = string.Empty;
    public decimal Action_Point { get; set; }
    public decimal Downstroke { get; set; }
    public decimal Upstroke { get; set; }
}

public sealed record Profile
{
    public string Storagename { get; set; } = string.Empty;
    public string Showname { get; set; } = string.Empty;
    public KeySetting[] Keys_Array { get; set; } = [];
    public RapidTriggerPlus? RTP { get; set; }

    // Web export may include this
    public bool IsActive { get; set; }

    // Global keyboard mode flags. Missing from older profile JSON files —
    // defaults to all-off, which matches the existing app's effective behavior
    // before this field existed.
    public ProfileSettings Settings { get; set; } = new();

    // Pass-through bucket for any field the web driver writes but we don't
    // model (RGB lighting block, future schema additions). Captured on
    // import, re-emitted verbatim on export so we never silently drop a
    // user's keyboard lighting when they round-trip a profile through us.
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; set; }
}

// Mirrors the official DrunkDeer driver's keyboardObj toggles, verified
// against the production WebHID source (see Driver/Packets.cs for the wire
// format that consumes these). Five of these go in the common switch packet
// (Turbo, RT, LW, RDT, RTMatch); the other three travel via their own packets
// and are wired in Phase C+ (Keystroke Tracking, LW Replace, AutoMatchMode).
public sealed record ProfileSettings
{
    // Common switch packet bytes 7, 8, 10, 11
    public bool RapidTriggerEnabled { get; set; }
    public bool TurboEnabled { get; set; }
    public bool LastWinEnabled { get; set; }
    public bool ReleaseDualTriggerEnabled { get; set; }
    public bool RTMatchEnabled { get; set; }

    // Separate packets, not yet wired (Phase C+):
    //   sendTrackingStartData / sendTrackingStopData (0xFD 0x03 0x01/0x00)
    public bool KeystrokeTrackingEnabled { get; set; }
    //   sendLwReplaceData (0xFC 0x0B <value>)
    public bool LastWinReplaceEnabled { get; set; }
    //   sendRtModeDate (0xFD 0x0C <value>) — 0..255, 255 = off
    public byte AutoMatchMode { get; set; } = 255;
}

public sealed record KeyRemapSetting
{
    public int KeyIndex { get; set; }
    public string KeyText { get; set; } = string.Empty;
    public int KeyCmd { get; set; }
    public int KeyType { get; set; }
    public int KeyCode { get; set; }
}

public sealed record RemapProfile
{
    public string Storagename { get; set; } = string.Empty;
    public string Showname { get; set; } = string.Empty;
    public KeyRemapSetting[] KeyCodeDefault { get; set; } = [];
    public Dictionary<string, int> HotKeyMap { get; set; } = new();
    public KeyRemapSetting[] KeyCodeFn1 { get; set; } = [];
    public KeyRemapSetting[] KeyCodeFn2 { get; set; } = [];

    // Per-slot HID-usage overrides written by the keyboard view's Remap drawer.
    // Element i is the new HID code for firmware slot i, or 0 if that slot
    // should keep its factory default. Empty array (legacy profiles imported
    // before this field existed) is treated as "all defaults". This is the
    // input to Packets.BuildFullRemapSequenceTyped on profile push.
    public byte[] PerSlotHidUsage { get; set; } = [];

    // Last-Win pairs stored per profile. Each entry is a 2-element [main,
    // trigger] slot-index pair. Empty array = no LW pairs (LW master toggle
    // alone is a no-op without an explicit pair table). Stored as byte[][]
    // rather than a (byte,byte) tuple so it survives a round-trip through
    // System.Text.Json with no custom converter.
    public byte[][] LwPairs { get; set; } = [];
}

public record ProfileItem : INotifyPropertyChanged
{
    [JsonIgnore]
    private string name = string.Empty;
    [JsonIgnore]
    private bool isDefault = false;
    [JsonIgnore]
    private string note = string.Empty;
    [JsonIgnore]
    private int directSwitchKey = 0; // 0 = none
    [JsonIgnore]
    private int directSwitchModifiers = 0;
    [JsonIgnore]
    private string[] processTriggers = [];
    [JsonIgnore]
    private Profile? profile;
    [JsonIgnore]
    private RemapProfile? remapProfile;

    [JsonIgnore]
    public bool IsDirty { get; set; }
    public event PropertyChangedEventHandler? PropertyChanged;


    [JsonIgnore]
    public string Name
    {
        get { return name; }
        set { SetField(ref name, value, nameof(Name)); }
    }

    public string Note
    {
        get { return note; }
        set { SetField(ref note, value, nameof(Note)); }
    }

    // Virtual key code for a direct-jump hotkey (0 = none)
    public int DirectSwitchKey
    {
        get { return directSwitchKey; }
        set { SetField(ref directSwitchKey, value, nameof(DirectSwitchKey)); }
    }

    // Modifier flags for the direct-jump hotkey (MOD_ALT=1, MOD_CONTROL=2, MOD_SHIFT=4)
    public int DirectSwitchModifiers
    {
        get { return directSwitchModifiers; }
        set { SetField(ref directSwitchModifiers, value, nameof(DirectSwitchModifiers)); }
    }

    public bool IsDefault
    {
        get { return isDefault; }
        set { SetField(ref isDefault, value, nameof(IsDefault)); }
    }

    public string[] ProcessTriggers
    {
        get { return processTriggers; }
        set { SetField(ref processTriggers, value, nameof(ProcessTriggers)); }
    }

    public required Profile Profile
    {
        get
        {
            if (profile is null)
            {
                throw new Exception("Profile is null");
            }
            return profile;
        }
        set { SetField(ref profile, value, nameof(Profile)); }
    }

    public RemapProfile? RemapProfile
    {
        get { return remapProfile; }
        set { SetField(ref remapProfile, value, nameof(RemapProfile)); }
    }

    [JsonIgnore]
    private bool isActiveProfile = false;

    [JsonIgnore]
    public bool IsActiveProfile
    {
        get => isActiveProfile;
        set
        {
            if (isActiveProfile != value)
            {
                isActiveProfile = value;
                OnPropertyChanged(nameof(IsActiveProfile));
            }
        }
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
    }

    // Force reference identity for equality and hashing. WPF's Selector keys
    // its internal SelectedItems dictionary by ItemInfo, which delegates to
    // the wrapped item's Equals/GetHashCode. Records auto-generate VALUE-based
    // equality from every property — and ProfileItem properties mutate
    // constantly (Name on rename, IsActiveProfile on profile switch, settings
    // on edit). Each mutation changes the record's hash code, so the Selector
    // can't find its own entries on cleanup → ghost selections accumulate
    // until two entries hash-collide and the dict throws "duplicate key,"
    // crashing the process. Reference identity sidesteps the entire mess:
    // every loaded ProfileItem is unique by instance and its hash never
    // changes for the lifetime of that instance.
    public virtual bool Equals(ProfileItem? other) => ReferenceEquals(this, other);
    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
}
