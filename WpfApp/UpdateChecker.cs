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
    string? Notes = null);

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

    public static Task<UpdateInfo?> CheckAsync() => FetchAsync(ApiUrl);

    // A specific release (e.g. "v2.6.1") — used to go back to the version
    // the user had before their last update.
    public static Task<UpdateInfo?> FetchByTagAsync(string tag) =>
        FetchAsync($"https://api.github.com/repos/svumo/DrunkDeer-Control/releases/tags/{Uri.EscapeDataString(tag)}");

    private static async Task<UpdateInfo?> FetchAsync(string apiUrl)
    {
        var current = CurrentVersion;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var http = new HttpClient();
            // GitHub's API rejects requests without a User-Agent.
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"DrunkDeer-Control/{current}");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            var json = await http.GetStringAsync(apiUrl, cts.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("tag_name", out var tagEl)
                || tagEl.GetString() is not { Length: > 0 } tag)
            {
                DebugLogger.Log("UpdateChecker: response missing tag_name");
                return null;
            }

            var stripped = tag.TrimStart('v', 'V');
            if (!Version.TryParse(stripped, out var latest))
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
            if (doc.RootElement.TryGetProperty("assets", out var assetsEl)
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
                    var text = await http.GetStringAsync(shaSidecarUrl, cts.Token).ConfigureAwait(false);
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
            string? notes = doc.RootElement.TryGetProperty("body", out var bodyEl) && bodyEl.ValueKind == JsonValueKind.String
                ? bodyEl.GetString() : null;
            return new UpdateInfo(current, latest, tag, downloadUrl, ReleasesPageUrl, isNewer, assetSize, sha256, notes);
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"UpdateChecker: check failed — {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    // Only follow download links that point at this repo's GitHub releases.
    private static bool IsGitHubDownloadUrl(string? url) =>
        url is not null
        && url.StartsWith("https://github.com/svumo/DrunkDeer-Control/releases/download/", StringComparison.OrdinalIgnoreCase);
}
