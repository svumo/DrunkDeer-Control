using System.Text;
using WpfApp.Profile;
using Xunit;

namespace DrunkDeer.Tests;

public sealed class ProfileFileStoreTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "dd-pfs-" + Guid.NewGuid().ToString("N")[..8]);

    public ProfileFileStoreTests() => Directory.CreateDirectory(dir);
    public void Dispose() { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } }

    private string P(string name) => Path.Combine(dir, name);

    [Fact]
    public void WriteAtomic_creates_file_without_bom_and_no_temp_left()
    {
        ProfileFileStore.WriteAtomic(P("a.json"), "{\"x\":1}");
        var bytes = File.ReadAllBytes(P("a.json"));
        Assert.Equal("{\"x\":1}", Encoding.UTF8.GetString(bytes));
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Single(Directory.GetFiles(dir));
    }

    [Fact]
    public void WriteAtomic_overwrite_keeps_previous_version_as_bak()
    {
        ProfileFileStore.WriteAtomic(P("a.json"), "v1");
        ProfileFileStore.WriteAtomic(P("a.json"), "v2");
        ProfileFileStore.WriteAtomic(P("a.json"), "v3");
        Assert.Equal("v3", File.ReadAllText(P("a.json")));
        Assert.Equal("v2", File.ReadAllText(P("a.json.bak")));
        Assert.Equal(2, Directory.GetFiles(dir).Length);
    }

    [Fact]
    public void Rename_writes_new_file_and_keeps_old_as_bak()
    {
        ProfileFileStore.WriteAtomic(P("Old.json"), "old");
        ProfileFileStore.RenameAndWrite(P("Old.json"), P("New.json"), "new");
        Assert.Equal("new", File.ReadAllText(P("New.json")));
        Assert.False(File.Exists(P("Old.json")));
        Assert.Equal("old", File.ReadAllText(P("Old.json.bak")));
    }

    [Fact]
    public void Rename_onto_an_existing_profile_is_refused_and_both_survive()
    {
        ProfileFileStore.WriteAtomic(P("A.json"), "profile A");
        ProfileFileStore.WriteAtomic(P("B.json"), "profile B");
        Assert.Throws<IOException>(() => ProfileFileStore.RenameAndWrite(P("B.json"), P("A.json"), "profile B"));
        Assert.Equal("profile A", File.ReadAllText(P("A.json")));
        Assert.Equal("profile B", File.ReadAllText(P("B.json")));
    }

    [Fact]
    public void Case_only_rename_changes_the_file_name()
    {
        ProfileFileStore.WriteAtomic(P("game.json"), "v1");
        ProfileFileStore.RenameAndWrite(P("game.json"), P("Game.json"), "v2");
        var files = Directory.GetFiles(dir).Select(Path.GetFileName).ToArray();
        Assert.Contains("Game.json", files);
        Assert.DoesNotContain("game.json", files);
        Assert.Equal("v2", File.ReadAllText(P("Game.json")));
    }

    [Fact]
    public void Quarantine_moves_file_aside_and_never_overwrites_an_earlier_one()
    {
        File.WriteAllText(P("bad.json"), "{ not json");
        var first = ProfileFileStore.Quarantine(P("bad.json"));
        File.WriteAllText(P("bad.json"), "{ also bad");
        var second = ProfileFileStore.Quarantine(P("bad.json"));
        Assert.NotEqual(first, second);
        Assert.Equal("{ not json", File.ReadAllText(first));
        Assert.Equal("{ also bad", File.ReadAllText(second));
        Assert.False(File.Exists(P("bad.json")));
        Assert.DoesNotContain(Directory.GetFiles(dir), f => f.EndsWith(".json"));
    }

    [Fact]
    public void BackupOnce_does_not_replace_an_existing_backup()
    {
        File.WriteAllText(P("a.json"), "original");
        Assert.NotNull(ProfileFileStore.BackupOnce(P("a.json"), "v0"));
        File.WriteAllText(P("a.json"), "migrated");
        Assert.Null(ProfileFileStore.BackupOnce(P("a.json"), "v0"));
        Assert.Equal("original", File.ReadAllText(P("a.json.v0.bak")));
    }

    [Fact]
    public void CleanupStaleTemps_removes_only_temp_files()
    {
        File.WriteAllText(P("a.json"), "keep");
        File.WriteAllText(P("a.json.1234.tmp"), "half-written");
        Assert.Equal(1, ProfileFileStore.CleanupStaleTemps(dir));
        Assert.True(File.Exists(P("a.json")));
    }

    [Theory]
    [InlineData("Valorant", null)]
    [InlineData("FPS (2)", null)]
    [InlineData("", "Enter a name.")]
    [InlineData("   ", "Enter a name.")]
    [InlineData(" lead", "Names can't start or end with a space.")]
    [InlineData("dot.", "Names can't end with a full stop.")]
    [InlineData("a/b", "Names can't contain /")]
    [InlineData("CON", "\"CON\" is reserved by Windows. Pick another name.")]
    [InlineData("nul.txt", "\"nul.txt\" is reserved by Windows. Pick another name.")]
    [InlineData("typing", "A profile called \"typing\" already exists.")]
    public void ValidateName(string name, string? expected) =>
        Assert.Equal(expected, ProfileFileStore.ValidateName(name, ["Typing", "Default"]));

    [Fact]
    public void ValidateName_rejects_overlong_names()
    {
        var name = new string('a', ProfileFileStore.MaxNameLength + 1);
        Assert.NotNull(ProfileFileStore.ValidateName(name, []));
    }
}
