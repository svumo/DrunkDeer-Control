using System.Text.Json;
using Driver;
using Xunit;

namespace DrunkDeer.Tests;

// The profile JSON is user data. These pin that a save/load round-trip keeps
// every field, that pre-2.6 files (no SchemaVersion, no ColourIndex) still
// load, and that the file name, not the JSON, decides the profile name.
public class ProfileRoundTripTests
{
    // Same options ProfileManager uses.
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    [Fact]
    public void Round_trip_keeps_settings_remaps_pairs_and_lighting()
    {
        var item = new ProfileItem
        {
            Name = "ignored",
            Note = "ranked",
            DirectSwitchKey = 0x70,
            DirectSwitchModifiers = 2,
            ProcessTriggers = [@"C:\Games\game.exe"],
            SchemaVersion = ProfileItem.CurrentSchemaVersion,
            Profile = new Profile
            {
                Keys_Array = [new KeySetting { Action_Point = 1.25m, Downstroke = 0.15m, Upstroke = 0.2m }],
                Settings = new ProfileSettings { RapidTriggerEnabled = true, LastWinEnabled = true },
            },
            RemapProfile = new RemapProfile
            {
                PerSlotHidUsage = [0, 0x04, 0x46],
                LwPairs = [[30, 32]],
                RdtPairs = [[17, 18]],
            },
            LightingProfile = new LightingProfile { Mode = LightingProfile.ModeBreath, Brightness = 7, Speed = 3, ColourIndex = 4 },
        };

        var json = JsonSerializer.Serialize(item, Options);
        var back = JsonSerializer.Deserialize<ProfileItem>(json, Options)!;

        Assert.Equal(item.Note, back.Note);
        Assert.Equal(item.DirectSwitchKey, back.DirectSwitchKey);
        Assert.Equal(item.DirectSwitchModifiers, back.DirectSwitchModifiers);
        Assert.Equal(item.ProcessTriggers, back.ProcessTriggers);
        Assert.Equal(ProfileItem.CurrentSchemaVersion, back.SchemaVersion);
        Assert.Equal(1.25m, back.Profile.Keys_Array[0].Action_Point);
        Assert.True(back.Profile.Settings.LastWinEnabled);
        Assert.Equal(new byte[] { 0, 0x04, 0x46 }, back.RemapProfile!.PerSlotHidUsage);
        Assert.Equal(30, back.RemapProfile.LwPairs[0][0]);
        Assert.Equal(18, back.RemapProfile.RdtPairs[0][1]);
        Assert.Equal(4, back.LightingProfile!.ColourIndex);
        Assert.Equal(LightingProfile.ModeBreath, back.LightingProfile.Mode);
    }

    [Fact]
    public void Name_is_not_serialised()
    {
        var json = JsonSerializer.Serialize(new ProfileItem { Name = "Secret name", Profile = new Profile() }, Options);
        Assert.DoesNotContain("Secret name", json);
    }

    [Fact]
    public void Pre_2_6_file_without_new_fields_loads_with_defaults()
    {
        const string legacy = """
        {
          "Note": "",
          "IsDefault": true,
          "Profile": { "Keys_Array": [], "Settings": { "RapidTriggerEnabled": false } },
          "LightingProfile": { "Mode": 2, "Brightness": 5, "Speed": 5 }
        }
        """;
        var item = JsonSerializer.Deserialize<ProfileItem>(legacy, Options)!;
        Assert.Equal(0, item.SchemaVersion);
        Assert.Equal(0, item.LightingProfile!.ColourIndex);
        Assert.True(item.IsDefault);
    }

    [Fact]
    public void Corrupt_json_throws_JsonException_so_the_loader_can_quarantine_it()
    {
        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<ProfileItem>("{ \"Profile\": ", Options));
    }
}
