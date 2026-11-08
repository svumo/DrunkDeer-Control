using System;
using System.Text.RegularExpressions;

namespace WpfApp;

// Pure helpers for verifying a downloaded update against its published
// SHA-256. No I/O, so Driver.Tests compiles this file in by link.
//
// What this protects against: truncated or corrupted downloads, CDN or proxy
// mix-ups, and an asset swapped after the update check. It does NOT protect
// against a compromised GitHub account, because the hash comes from the same
// release; that would need code signing.
public static partial class UpdateIntegrity
{
    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex Sha256Hex();

    [GeneratedRegex(@"\b([0-9a-fA-F]{64})\b")]
    private static partial Regex Sha256InText();

    /// <summary>
    /// Parses GitHub's release-asset <c>digest</c> field ("sha256:&lt;hex&gt;").
    /// Returns lowercase hex, or null if absent or not a SHA-256 digest.
    /// </summary>
    public static string? ParseGitHubDigest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest)) return null;
        const string prefix = "sha256:";
        if (!digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var hex = digest[prefix.Length..].Trim();
        return Sha256Hex().IsMatch(hex) ? hex.ToLowerInvariant() : null;
    }

    /// <summary>
    /// Parses a <c>.sha256</c> sidecar file. Accepts a bare hash, or the
    /// sha256sum / Get-FileHash style "&lt;hex&gt;  filename". Returns the first
    /// 64-hex-digit token in lowercase, or null.
    /// </summary>
    public static string? ParseSha256File(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        var m = Sha256InText().Match(content);
        return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
    }

    public static bool Matches(string? expectedHex, byte[] actualHash) =>
        expectedHex is { Length: 64 }
        && string.Equals(expectedHex, Convert.ToHexString(actualHash), StringComparison.OrdinalIgnoreCase);
}
