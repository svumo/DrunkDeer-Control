using System.Security.Cryptography;
using System.Text;
using WpfApp;
using Xunit;

namespace DrunkDeer.Tests;

public class UpdateIntegrityTests
{
    private const string Hex = "5cf3362b0a9e1d2c3b4a59687766554433221100ffeeddccbbaa998877665544";

    [Theory]
    [InlineData("sha256:" + Hex, Hex)]
    [InlineData("SHA256:" + "5CF3362B0A9E1D2C3B4A59687766554433221100FFEEDDCCBBAA998877665544", Hex)]
    [InlineData("sha512:" + Hex, null)]
    [InlineData("sha256:abc", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Parses_github_asset_digest(string? digest, string? expected) =>
        Assert.Equal(expected, UpdateIntegrity.ParseGitHubDigest(digest));

    [Theory]
    [InlineData(Hex, Hex)]
    [InlineData(Hex + "  DrunkDeer-Control.exe\n", Hex)]
    [InlineData("SHA256 hash of DrunkDeer-Control.exe:\r\n" + Hex + "\r\n", Hex)]
    [InlineData("not a hash", null)]
    [InlineData(null, null)]
    public void Parses_sha256_sidecar(string? content, string? expected) =>
        Assert.Equal(expected, UpdateIntegrity.ParseSha256File(content));

    [Fact]
    public void Matches_real_hash_case_insensitively()
    {
        var data = Encoding.UTF8.GetBytes("drunkdeer");
        var hash = SHA256.HashData(data);
        var hex = Convert.ToHexString(hash);
        Assert.True(UpdateIntegrity.Matches(hex.ToLowerInvariant(), hash));
        Assert.True(UpdateIntegrity.Matches(hex, hash));
    }

    [Fact]
    public void Rejects_wrong_or_missing_hash()
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("drunkdeer"));
        Assert.False(UpdateIntegrity.Matches(Hex, hash));
        Assert.False(UpdateIntegrity.Matches(null, hash));
        Assert.False(UpdateIntegrity.Matches("", hash));
    }
}
