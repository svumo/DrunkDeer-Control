using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Driver;
using Microsoft.Win32;

namespace WpfApp;

// Anonymous usage telemetry. Sends a small heartbeat once per day so the
// maintainer can answer "is anyone using this?" without ever learning who
// "anyone" is. Worker source — including the privacy claims about IPs and
// data retention — lives at `telemetry-worker/` in this repo.
//
// Exact payload (everything that goes over the wire):
//
//   {
//     "id":     "abcdef0123456789",                  // HMAC-SHA256(MachineGuid, salt) → 16 hex chars
//     "app":    "1.6.0",                             // Assembly.Version of this build
//     "os":     "Microsoft Windows NT 10.0.26200.0", // Environment.OSVersion.VersionString
//     "kb_pid": "0x2383",                            // connected keyboard's USB PID, or null
//     "kb_fw":  "0.48",                              // connected keyboard's firmware, or null
//     "ts":     1730000000                           // unix seconds, current UTC
//   }
//
// What is NEVER sent:
//   - Username, computer name, file paths, or any other PII.
//   - Profile data, keystrokes, or anything tied to keyboard configuration.
//   - The raw MachineGuid — only its HMAC-SHA256 truncated hash.
//
// Opt out: Settings → "Anonymous usage stats" toggle (default on).
public static class UsageReporter
{
    // Cloudflare Worker source lives at telemetry-worker/ in this repo.
    private const string Endpoint = "https://drunkdeer-telemetry.svumo.workers.dev/ping";

    private static readonly TimeSpan PingInterval = TimeSpan.FromHours(24);

    // HMAC salt is a public constant. Its only purpose is to ensure the hash
    // isn't a plain SHA256 of the MachineGuid (which would be brute-forceable
    // by anyone with a list of MachineGuids and the source for this method).
    // Keep this version-suffixed so we can rotate without breaking dedup if
    // we ever need to.
    private const string HashSalt = "drunkdeer-telemetry-v1";

    public static async Task ReportIfDueAsync(Settings settings, KeyboardWithSpecs? keyboard)
    {
        if (!settings.UsageStatsEnabled) return;

        // The first launch ever has LastUsageReport == DateTime.MinValue, so
        // we ping immediately. After that, only every 24h.
        if (DateTime.UtcNow - settings.LastUsageReport < PingInterval) return;

        if (Endpoint.Contains("PLACEHOLDER", StringComparison.Ordinal))
        {
            DebugLogger.Log("UsageReporter: endpoint is a placeholder, skipping ping");
            return;
        }

        try
        {
            var payload = new
            {
                id = GetStableDeviceId(),
                app = UpdateChecker.CurrentVersion.ToString(),
                os = Environment.OSVersion.VersionString,
                // USB Product ID from the HID device (e.g. 0x2383 for G65,
                // 0x2391/0x2a08 for A75 Pro). NOT KeyboardSpecs.KeyboardType
                // which is a derived model number.
                kb_pid = keyboard is { Keyboard: { ProductID: int pid } } ? $"0x{pid:x4}" : null,
                kb_fw = keyboard?.Specs.FirmwareVersion,
                ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"DrunkDeer-Control/{UpdateChecker.CurrentVersion}");
            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            DebugLogger.Log($"UsageReporter: pinging {Endpoint} (id={payload.id} app={payload.app} kb_pid={payload.kb_pid})");
            using var response = await http.PostAsync(Endpoint, content, cts.Token).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                settings.LastUsageReport = DateTime.UtcNow;
                settings.Save();
            }
            else
            {
                DebugLogger.Log($"UsageReporter: server returned {(int)response.StatusCode} {response.ReasonPhrase}");
            }
        }
        catch (Exception ex)
        {
            // Telemetry must never affect the app. Log and move on.
            DebugLogger.Log($"UsageReporter: ping failed — {ex.GetType().Name}: {ex.Message}");
        }
    }

    // 16 lowercase hex chars derived from HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid.
    // MachineGuid is a per-OS-install identifier — survives reboots, doesn't
    // travel with user accounts. The hash is one-way; a leak of the endpoint
    // database does not let anyone work backward to the source machine.
    private static string GetStableDeviceId()
    {
        var seed = ReadMachineGuid();
        if (seed is null)
        {
            // The registry read can fail in restricted environments. Fall back
            // to MachineName so we still get a stable-per-host hash, but never
            // send the raw MachineName itself.
            seed = "fallback-" + Environment.MachineName;
            DebugLogger.Log("UsageReporter: MachineGuid read failed, using MachineName fallback for hash");
        }

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(HashSalt));
        var bytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(seed));
        return Convert.ToHexString(bytes.AsSpan(0, 8)).ToLowerInvariant();
    }

    private static string? ReadMachineGuid()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid") as string;
        }
        catch
        {
            return null;
        }
    }
}
