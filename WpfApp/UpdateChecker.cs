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
    bool IsNewer);

public static class UpdateChecker
{
    private const string ApiUrl =
        "https://api.github.com/repos/svumo/DrunkDeer-Control/releases/latest";

    // GitHub's stable redirect — always 302s to the latest release's asset
    // with this filename. We never need to parse the API response for this URL.
    public const string DirectDownloadUrl =
        "https://github.com/svumo/DrunkDeer-Control/releases/latest/download/DrunkDeer-Control.exe";

    public const string ReleasesPageUrl =
        "https://github.com/svumo/DrunkDeer-Control/releases";

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);

    public static async Task<UpdateInfo?> CheckAsync()
    {
        var current = CurrentVersion;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var http = new HttpClient();
            // GitHub's API rejects requests without a User-Agent.
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"DrunkDeer-Control/{current}");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            var json = await http.GetStringAsync(ApiUrl, cts.Token).ConfigureAwait(false);
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

            var isNewer = latest > current;
            DebugLogger.Log($"UpdateChecker: current={current} latest={latest} (tag={tag}) isNewer={isNewer}");
            return new UpdateInfo(current, latest, tag, DirectDownloadUrl, ReleasesPageUrl, isNewer);
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"UpdateChecker: check failed — {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
