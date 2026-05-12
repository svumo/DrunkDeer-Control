using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Driver;

namespace WpfApp;

// Polls the telemetry worker's GET /firmware endpoint on launch and
// compares the connected keyboard's reported firmware version against
// the latest version DrunkDeer publishes for that USB PID. The worker's
// daily cron does the actual scraping — see telemetry-worker/src/index.ts.
//
// This is a *read*-only counterpart to UsageReporter: no payload, no
// device ID, just an unauthenticated GET. Fire-and-forget; never blocks
// the UI; swallows every error so a network blip can't keep the app
// from launching.
//
// Versions on the wire:
//   - Keyboard reports firmware as "0.09" (KeyboardSpecs.FirmwareVersion
//     is decimal-formatted "0.{MM}{LL}" from the spec packet bytes).
//   - Worker returns "0x0008" (hex of the same two-byte value pulled out
//     of the DrunkDeer Updater bundle's config.ini).
// The compare-as-int normaliser below understands both formats.
public static class FirmwareUpdateChecker
{
    // Same base host as UsageReporter.Endpoint — they're the same Worker.
    private const string Endpoint = "https://drunkdeer-telemetry.svumo.workers.dev/firmware";
    public const string DownloadsUrl = "https://drunkdeer.com/pages/downloads";

    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private static async Task<Dictionary<string, string>?> FetchAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"DrunkDeer-Control/{UpdateChecker.CurrentVersion}");
            DebugLogger.Log($"FirmwareUpdateChecker: GET {Endpoint}");
            using var response = await http.GetAsync(Endpoint, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                DebugLogger.Log($"FirmwareUpdateChecker: server returned {(int)response.StatusCode}");
                return null;
            }
            var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            var doc = JsonSerializer.Deserialize<FirmwareResponse>(body, JsonOpts);
            return doc?.Versions ?? new Dictionary<string, string>();
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"FirmwareUpdateChecker: fetch failed — {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static Dictionary<string, string>? TryParseCache(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    // PID is required for the per-keyboard lookup. KeyboardSpecs alone doesn't
    // expose it (that lives on HidDevice), so the caller passes both — keeps
    // this class free of HidSharp deps and easy to unit-test.
    public static async Task<FirmwareCheckResult?> CheckIfDueAsync(
        Settings settings,
        KeyboardSpecs? keyboard,
        int? productId)
    {
        try
        {
            var cached = TryParseCache(settings.LatestKnownFirmwareJson);

            if (DateTime.UtcNow - settings.LastFirmwareCheck >= CheckInterval)
            {
                var fresh = await FetchAsync().ConfigureAwait(false);
                if (fresh is not null)
                {
                    cached = fresh;
                    try
                    {
                        settings.LatestKnownFirmwareJson = JsonSerializer.Serialize(fresh);
                        settings.LastFirmwareCheck = DateTime.UtcNow;
                        settings.Save();
                    }
                    catch (Exception ex)
                    {
                        DebugLogger.Log($"FirmwareUpdateChecker: cache save failed — {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }

            if (keyboard is null || productId is null) return null;
            if (cached is null || cached.Count == 0) return null;

            var pidKey = $"0x{productId.Value:x4}";
            if (!cached.TryGetValue(pidKey, out var latestRaw)) return null;

            if (!TryParseFirmwareInt(keyboard.FirmwareVersion, out int currentInt)) return null;
            if (!TryParseFirmwareInt(latestRaw, out int latestInt)) return null;

            bool updateAvailable = latestInt > currentInt;
            var result = new FirmwareCheckResult(
                CurrentVersion: keyboard.FirmwareVersion,
                LatestVersion: FormatFirmwareDecimal(latestInt),
                DownloadsUrl: DownloadsUrl,
                UpdateAvailable: updateAvailable);

            DebugLogger.Log($"FirmwareUpdateChecker: pid={pidKey} current={result.CurrentVersion}({currentInt}) latest={result.LatestVersion}({latestInt}) updateAvailable={updateAvailable}");
            return result;
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"FirmwareUpdateChecker: check failed — {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    // Accepts either "0.09" (KeyboardSpecs decimal form) or "0x0008" (worker
    // hex form) and returns the underlying 16-bit firmware integer.
    //
    // KeyboardSpecs.FirmwareVersion is built as `string.Format("0.{0}{1}", packet[8], packet[7])`
    // — so "0.09" means upper byte 0, lower byte 9 → 0x0009 → 9. Note that
    // "0.018" means upper=0, lower=18 → 0x0012 → 18, NOT 0x0118. We parse the
    // chars after the dot as a single decimal integer matching that builder.
    internal static bool TryParseFirmwareInt(string? raw, out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        raw = raw.Trim();
        if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return int.TryParse(raw.AsSpan(2), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out value);
        }
        // "0.09" — split on first '.', interpret each half as decimal byte.
        // Upper byte is rarely non-zero (DrunkDeer firmware is all 0.x) but
        // we handle it for forward-compat.
        int dot = raw.IndexOf('.');
        if (dot < 0) return int.TryParse(raw, out value);
        var upperPart = raw.AsSpan(0, dot);
        var lowerPart = raw.AsSpan(dot + 1);
        if (!int.TryParse(upperPart, out int upper)) return false;
        if (!int.TryParse(lowerPart, out int lower)) return false;
        if (upper < 0 || upper > 255 || lower < 0 || lower > 255) return false;
        value = (upper << 8) | lower;
        return true;
    }

    // Inverse of TryParseFirmwareInt for display purposes — formats the
    // integer the way KeyboardSpecs would have, so banners read consistently.
    internal static string FormatFirmwareDecimal(int value)
    {
        int upper = (value >> 8) & 0xFF;
        int lower = value & 0xFF;
        return $"{upper}.{lower:D2}";
    }

    // Shape matches handleFirmware in telemetry-worker/src/index.ts.
    private sealed class FirmwareResponse
    {
        public Dictionary<string, string>? Versions { get; set; }
        public string? Bundle { get; set; }
        public string? Checked { get; set; }
    }
}

public sealed record FirmwareCheckResult(
    string CurrentVersion,
    string LatestVersion,
    string DownloadsUrl,
    bool UpdateAvailable);
