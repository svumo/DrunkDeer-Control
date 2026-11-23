using System;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Driver;

namespace WpfApp;

public sealed record UpdateInfo(
    Version Current,
    Version Latest,
    string LatestTag,
    string DownloadUrl,
    string ReleaseUrl,
    bool IsNewer,
    long? AssetSize,
    // Lowercase hex SHA-256 of the exe as published on the release, from
    // GitHub's asset digest or a DrunkDeer-Control.exe.sha256 sidecar.
    // Null when the release publishes neither: the in-app installer then
    // falls back to the browser download.
    string? Sha256,
    // Release body (markdown) for the update prompt. May be null/empty.
    string? Notes = null,
    // Full version text incl. any beta suffix ("2.7.2-beta.1"); what the
    // prompt, the skip list and the rollback record use.
    string LatestLabel = "");

public static class UpdateChecker
{
    private const string ApiUrl =
        "https://api.github.com/repos/svumo/DrunkDeer-Control/releases/latest";

    // GitHub's stable redirect — always 302s to the latest release's asset
    // with this filename. We never need to parse the API response for this URL.
    public const string DirectDownloadUrl =
        "https://github.com/svumo/DrunkDeer-Control/releases/latest/download/DrunkDeer-Control.exe";

    private const string ExeAssetName = "DrunkDeer-Control.exe";
    private const string ShaAssetName = "DrunkDeer-Control.exe.sha256";

    public const string ReleasesPageUrl =
        "https://github.com/svumo/DrunkDeer-Control/releases";

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);

    // "2.7.2-beta.1" for a beta build, "2.7.1" for a stable one (from the
    // csproj <Version>, via InformationalVersion, minus "+commit" metadata).
    public static string CurrentLabel => Current.ToString();

    public static AppVersion Current
    {
        get
        {
            var info = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (AppVersion.TryParse(info) is { } v) return v;
            var cv = CurrentVersion;
            return new AppVersion(cv.Major, cv.Minor, Math.Max(cv.Build, 0), null);
        }
    }

    // Beta builds follow the beta channel unless the user turned it off.
    public static bool IsBetaBuild => Current.IsPrerelease;

    private const string ListApiUrl =
        "https://api.github.com/repos/svumo/DrunkDeer-Control/releases?per_page=30";

    // Stable channel: GitHub's "latest" release (never a pre-release).
    // Beta channel: the newest published release of any kind, so a beta
    // tester moves on to the stable release once it supersedes their beta.
    public static Task<UpdateInfo?> CheckAsync(bool includeBetas = false) =>
        includeBetas ? FetchNewestIncludingBetasAsync() : FetchAsync(ApiUrl);

    // A specific release (e.g. "v2.6.1") — used to go back to the version
    // the user had before their last update.
    public static Task<UpdateInfo?> FetchByTagAsync(string tag) =>
        FetchAsync($"https://api.github.com/repos/svumo/DrunkDeer-Control/releases/tags/{Uri.EscapeDataString(tag)}");

    private static async Task<UpdateInfo?> FetchAsync(string apiUrl)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var http = NewClient();

            var json = await http.GetStringAsync(apiUrl, cts.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            return await ParseReleaseAsync(doc.RootElement, http, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"UpdateChecker: check failed — {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static async Task<UpdateInfo?> FetchNewestIncludingBetasAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var http = NewClient();
            var json = await http.GetStringAsync(ListApiUrl, cts.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            JsonElement? best = null;
            AppVersion? bestV = null;
            foreach (var rel in doc.RootElement.EnumerateArray())
            {
                if (rel.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) continue;
                var v = rel.TryGetProperty("tag_name", out var t) ? AppVersion.TryParse(t.GetString()) : null;
                if (v is null || !HasExeAsset(rel)) continue;
                if (bestV is null || v > bestV) { bestV = v; best = rel; }
            }
            if (best is not { } chosen)
            {
                DebugLogger.Log("UpdateChecker(beta): no release with an exe asset");
                return null;
            }
            return await ParseReleaseAsync(chosen, http, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"UpdateChecker(beta): check failed — {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static bool HasExeAsset(JsonElement rel)
    {
        if (!rel.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return false;
        foreach (var a in assets.EnumerateArray())
            if (a.TryGetProperty("name", out var n) && string.Equals(n.GetString(), ExeAssetName, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static HttpClient NewClient()
    {
        var http = new HttpClient();
        // GitHub's API rejects requests without a User-Agent.
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"DrunkDeer-Control/{CurrentVersion}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    private static async Task<UpdateInfo?> ParseReleaseAsync(JsonElement root, HttpClient http, CancellationToken ct)
    {
        var current = Current;
        {
            if (!root.TryGetProperty("tag_name", out var tagEl)
                || tagEl.GetString() is not { Length: > 0 } tag)
            {
                DebugLogger.Log("UpdateChecker: response missing tag_name");
                return null;
            }

            if (AppVersion.TryParse(tag) is not { } latest)
            {
                DebugLogger.Log($"UpdateChecker: cannot parse tag '{tag}' as Version");
                return null;
            }

            // Pull the exe asset's size, digest and tag-pinned download URL.
            // The pinned URL matters: the releases/latest redirect could
            // serve a newer release than the one whose hash we just read.
            long? assetSize = null;
            string? sha256 = null;
            string downloadUrl = DirectDownloadUrl;
            string? shaSidecarUrl = null;
            if (root.TryGetProperty("assets", out var assetsEl)
                && assetsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetsEl.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                    var url = asset.TryGetProperty("browser_download_url", out var urlEl) ? urlEl.GetString() : null;
                    if (string.Equals(name, ExeAssetName, StringComparison.OrdinalIgnoreCase))
                    {
                        if (asset.TryGetProperty("size", out var sizeEl) && sizeEl.TryGetInt64(out var size))
                            assetSize = size;
                        if (asset.TryGetProperty("digest", out var digestEl) && digestEl.ValueKind == JsonValueKind.String)
                            sha256 = UpdateIntegrity.ParseGitHubDigest(digestEl.GetString());
                        if (IsGitHubDownloadUrl(url)) downloadUrl = url!;
                    }
                    else if (string.Equals(name, ShaAssetName, StringComparison.OrdinalIgnoreCase) && IsGitHubDownloadUrl(url))
                    {
                        shaSidecarUrl = url;
                    }
                }
            }

            if (sha256 is null && shaSidecarUrl is not null)
            {
                try
                {
                    var text = await http.GetStringAsync(shaSidecarUrl, ct).ConfigureAwait(false);
                    sha256 = UpdateIntegrity.ParseSha256File(text);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    DebugLogger.Log($"UpdateChecker: sha256 sidecar fetch failed — {ex.GetType().Name}: {ex.Message}");
                }
            }

            var isNewer = latest > current;
            var shaLog = sha256 is null ? "none" : sha256[..12] + "…";
            DebugLogger.Log($"UpdateChecker: current={current} latest={latest} (tag={tag}) isNewer={isNewer} assetSize={assetSize} sha256={shaLog} url={downloadUrl}");
            string? notes = root.TryGetProperty("body", out var bodyEl) && bodyEl.ValueKind == JsonValueKind.String
                ? bodyEl.GetString() : null;
            return new UpdateInfo(current.Core, latest.Core, tag, downloadUrl, ReleasesPageUrl, isNewer, assetSize, sha256, notes, latest.ToString());
        }
    }

    // Only follow download links that point at this repo's GitHub releases.
    private static bool IsGitHubDownloadUrl(string? url) =>
        url is not null
        && url.StartsWith("https://github.com/svumo/DrunkDeer-Control/releases/download/", StringComparison.OrdinalIgnoreCase);
}
