using System.Reflection;
using System.Text.Json;

namespace Driver;

// Loads the diagnostic-qualified capability records (Driver/capabilities/*.json,
// embedded at build) into the table FirmwareCapabilities.Resolve consults.
//
// This is the app side of the diagnostic → app boundary. The diagnostic
// (private repo) emits one JSON per qualified (model, firmware); we embed those
// JSONs and turn each into a FirmwareCapabilities row. Adding a qualified
// keyboard = drop a JSON + rebuild — no edit to the resolver.
//
// Schema: docs/diagnostic/capability-record-schema.md. Only the fields the app
// actually gates on are read here (tier + RGB gates); the `evidence` block is
// provenance and is intentionally ignored at runtime.
//
// Robustness contract: a malformed or unknown-version record is SKIPPED with a
// debug log line, never thrown. A bad file must not crash device connect or
// silently downgrade a good record — it just doesn't contribute a row, and that
// (model, fw) falls through to Beta/Unknown exactly as an absent record would.
internal static class CapabilityRecordLoader
{
    private const int SupportedSchemaVersion = 1;

    // Symbolic RGB mode name → wire byte. Symbolic in the JSON so the file
    // survives any renumbering of the LightingProfile constants and reads
    // clearly in review. Names match the historical constant names, not the
    // JS catalog labels (see docs/2026-05-27-session.md on the mismatch).
    private static readonly Dictionary<string, byte> RgbModeByName =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Off"]      = LightingProfile.ModeOff,
            ["Marquee"]  = LightingProfile.ModeMarquee,
            ["AlwaysOn"] = LightingProfile.ModeAlwaysOn,
            ["Breath"]   = LightingProfile.ModeBreath,
            ["Neon"]     = LightingProfile.ModeNeon,
        };

    // Built once on first access. Each entry mirrors a row that used to be
    // hand-coded in FirmwareCapabilities.VerifiedTable.
    public static IReadOnlyList<(int TypeCode, ushort FirmwareVersion, FirmwareCapabilities Capabilities)> Load()
    {
        var result = new List<(int, ushort, FirmwareCapabilities)>();
        var asm = typeof(CapabilityRecordLoader).Assembly;

        foreach (var resourceName in asm.GetManifestResourceNames())
        {
            // Embedded name looks like "Driver.capabilities.750-0017.json".
            if (!resourceName.Contains(".capabilities.", StringComparison.Ordinal)
                || !resourceName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var row = ParseResource(asm, resourceName);
                if (row is { } r) result.Add(r);
            }
            catch (Exception ex)
            {
                // Never let one bad record break resolution for every keyboard.
                DebugLogger.Log($"CapabilityRecordLoader: skipped {resourceName}: {ex.Message}");
            }
        }

        return result;
    }

    private static (int, ushort, FirmwareCapabilities)? ParseResource(Assembly asm, string resourceName)
    {
        using var stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("resource stream was null");
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;

        var schemaVersion = GetInt(root, "schemaVersion") ?? 0;
        if (schemaVersion != SupportedSchemaVersion)
        {
            DebugLogger.Log($"CapabilityRecordLoader: {resourceName} schemaVersion {schemaVersion} != {SupportedSchemaVersion}; skipping");
            return null;
        }

        var typeCode = GetInt(root, "typeCode")
            ?? throw new InvalidOperationException("missing typeCode");
        var firmware = GetInt(root, "firmware")
            ?? throw new InvalidOperationException("missing firmware");
        if (firmware is < 0 or > 0xFFFF)
            throw new InvalidOperationException($"firmware {firmware} out of ushort range");

        var model = root.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String
            ? m.GetString()!
            : $"TypeCode {typeCode}";
        var fwHex = root.TryGetProperty("firmwareHex", out var fh) && fh.ValueKind == JsonValueKind.String
            ? fh.GetString()!
            : $"0x{firmware:X4}";

        var tier = ParseTier(root);

        // RGB gates. Absent rgb block = all gates off (safe default — RGB stays
        // locked, which is the brick-safe state).
        bool rgbSupported = false;
        bool rgbCustom = false;
        IReadOnlyList<byte> verifiedModes = [];
        if (root.TryGetProperty("rgb", out var rgb) && rgb.ValueKind == JsonValueKind.Object)
        {
            rgbSupported = GetBool(rgb, "supported") ?? false;
            rgbCustom = GetBool(rgb, "supportsCustom") ?? false;
            verifiedModes = ParseModes(rgb);
        }

        // A record may only unlock RGB on a Verified board. Beta/Unknown stay
        // locked regardless of what the file claims — the brick gate is not
        // something a non-verified record gets to open.
        if (tier != SupportTier.Verified)
        {
            rgbSupported = false;
            rgbCustom = false;
            verifiedModes = [];
        }

        var caps = new FirmwareCapabilities
        {
            Label = $"{model} {fwHex} ({tier.ToString().ToLowerInvariant()})",
            Tier = tier,
            SupportsRgb = rgbSupported,
            VerifiedRgbModes = verifiedModes,
            SupportsCustomRgb = rgbCustom,
        };

        return (typeCode, (ushort)firmware, caps);
    }

    private static SupportTier ParseTier(JsonElement root)
    {
        if (root.TryGetProperty("tier", out var t) && t.ValueKind == JsonValueKind.String)
        {
            return t.GetString() switch
            {
                "Verified" => SupportTier.Verified,
                "Beta"     => SupportTier.Beta,
                _          => SupportTier.Unknown,
            };
        }
        return SupportTier.Unknown;
    }

    private static IReadOnlyList<byte> ParseModes(JsonElement rgb)
    {
        if (!rgb.TryGetProperty("verifiedModes", out var modes) || modes.ValueKind != JsonValueKind.Array)
            return [];

        var bytes = new List<byte>();
        foreach (var el in modes.EnumerateArray())
        {
            if (el.ValueKind == JsonValueKind.String
                && RgbModeByName.TryGetValue(el.GetString()!, out var code))
            {
                bytes.Add(code);
            }
            else
            {
                DebugLogger.Log($"CapabilityRecordLoader: unknown RGB mode name '{el}' ignored");
            }
        }
        return bytes;
    }

    private static int? GetInt(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v)
            ? v : null;

    private static bool? GetBool(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? p.GetBoolean() : null;
}
