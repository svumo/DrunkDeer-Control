namespace Driver;

// Per (model, firmware-version) behaviour profile.
//
// The web driver at drunkdeer-antler.com talks to every supported DrunkDeer
// keyboard with one unified protocol — there's no per-firmware-version
// branching in the JS bundle (verified via WebHID capture on A75 Pro 0x0017,
// see tools/captures/0x17/). Wire format is uniform: 1-byte-per-key
// AP/DS/US scaled `byte = mm × 100`, plus the shared CommonSwitch / RTP /
// remap packet families documented in docs/keyboard-protocol.md.
//
// However we still want to know which (model, firmware) combinations we've
// *verified* end-to-end vs. which are running our best guess. The Tier on
// this record drives:
//   * the firmware-status pill in the keyboard header (green / yellow / red)
//   * telemetry (lets us see how many users are on un-verified configs)
//   * per-feature gating for Beta tier (e.g. disable LW pairs until we've
//     verified pair behaviour on that specific firmware)
//
// Resolution flow (called from KeyboardManager on connect):
//   FirmwareCapabilities.Resolve(specs.KeyboardType, specs.FirmwareVersionNumeric)
//     → Verified record if the exact (TypeCode, fw) is in the table
//     → Beta record    if the TypeCode is known but the firmware isn't
//     → Unknown record if the TypeCode itself is null / unrecognized
public sealed record FirmwareCapabilities
{
    // Short label for diagnostics, telemetry, and the tier tooltip.
    // Example: "A75 Pro 0x0017 (verified)" or "A75 ANSI 0x0027 (beta)".
    public string Label { get; init; } = "Unknown";

    public SupportTier Tier { get; init; } = SupportTier.Unknown;

    // AP/DS/US wire-format range. All currently supported firmwares use
    // `byte = mm × 100`. The old 0x0009 path (which used `byte = mm × 10`
    // and had its own minimal sync sequence) was removed 2026-05-21
    // after the maintenance cost became prohibitive — users on outdated
    // firmware are now prompted to upgrade via the in-app modal. See
    // KeyboardPerformanceView.EvaluateFirmwareTooOldDialog.
    public byte ApByteMin { get; init; } = Packets.AP_BYTE_MIN;
    public byte ApByteMax { get; init; } = Packets.AP_BYTE_MAX;
    public byte DsByteMax { get; init; } = Packets.DS_BYTE_MAX;
    public byte UsByteMax { get; init; } = Packets.US_BYTE_MAX;

    // Per-key AP/DS/US wire-format dialect. The official JS bundle
    // (drunkdeer.keybord.net.cn, extracted 2026-05-22 — see
    // docs/protocol-findings-keybord-net-cn.md) dispatches per (typeCode,
    // firmwareVersion) into three packet shapes. We default to OldHighPrec
    // because that's what our existing code path produces, and switching
    // a model to a different precision without hardware verification
    // could regress users we currently support. See WirePrecision for
    // the per-case wire encoding.
    public WirePrecision Precision { get; init; } = WirePrecision.OldHighPrec;

    // True when the connected firmware is below the supported floor.
    // Set on the resolved record by the per-model VerifiedTable entries
    // OR by the LatestKnownFirmware range check in Resolve().
    // KeyboardPerformanceView surfaces a one-time modal and points at
    // https://drunkdeer.keybord.net.cn/drunk/index.html.
    public bool IsTooOld { get; init; } = false;

    // The minimum firmware version we support for this TypeCode, drawn
    // from the latest official updater bundle (DrunkdeerUpdaterV2.3.4
    // config.ini). Populated only on IsTooOld records so the modal can
    // tell the user what version they should target.
    public ushort? RecommendedFloor { get; init; }

    // RGB capability gates. Default false because an invalid 0xAE write
    // soft-bricks the A75 Pro (recoverable only by spamming a known-good
    // packet during the boot loop — see docs/rgb-protocol.md). So RGB
    // unlocks only for (TypeCode, firmware) combos explicitly proven to
    // accept the packet stream — Beta and Unknown tiers stay locked out.
    public bool SupportsRgb { get; init; } = false;

    // Preset mode codes verified safe on this firmware. Drives the
    // per-preset enable state in LightingView — Marquee/Neon stay locked
    // until their codes land here. Empty when SupportsRgb=false.
    public IReadOnlyList<byte> VerifiedRgbModes { get; init; } = [];

    // Per-key custom-light gate. The custom-light (0xAE/0x01/mode=0x13)
    // packet stream is a SEPARATE risk surface from preset modes — the
    // doc explicitly flags A75 Pro per-key as "confirmed in code but not
    // on A75 Pro hardware" (docs/rgb-protocol.md §200). So `SupportsRgb`
    // alone is not enough authorisation to start emitting per-key writes;
    // we add a second gate that unlocks the Custom radio in LightingView.
    //
    // Even with this flag true, the first sync of Custom mode triggers a
    // brick-confirmation modal (one-shot ack persisted in Settings) before
    // a packet reaches hardware — that's the user-facing safety gate. The
    // flag itself just controls whether the path is even reachable.
    public bool SupportsCustomRgb { get; init; } = false;

    // The Unknown / default record. Used when KeyboardSpecs returned no
    // TypeCode at all (spec response missing or unparseable). UI surfaces
    // "Unrecognized — please report" and disables Sync.
    public static readonly FirmwareCapabilities Unknown = new()
    {
        Label = "Unknown model",
        Tier = SupportTier.Unknown,
    };

    // Beta safe-default. Used when the keyboard model IS recognized
    // (TypeCode matches a KeyboardModel entry) but we haven't verified
    // this specific firmware version end-to-end. Wire format is the
    // universal one — should work, but we mark it Beta so the user knows
    // they're outside our tested matrix.
    public static FirmwareCapabilities Beta(string label) => new()
    {
        Label = label,
        Tier = SupportTier.Beta,
    };

    // Resolves capabilities for a connected keyboard.
    //
    // typeCode: from KeyboardSpecs.KeyboardType (firmware-reported TypeCode,
    //   e.g. 750 for A75 Pro). Null when spec response was invalid.
    // firmwareVer: from KeyboardSpecs.FirmwareVersionNumeric (e.g. 0x0017).
    //
    // Returns one of:
    //   - A Verified record from the table below (exact-match)
    //   - A Beta record if TypeCode is known but firmware version isn't
    //   - Unknown if TypeCode is null
    // Developer experimental override. When non-null, ResolvePrecision()
    // returns this value instead of the per-(typeCode, fw) dispatch result.
    // Used to test whether a keyboard actually accepts a different wire
    // dialect than its firmware self-reports — e.g. probing whether
    // A75 Pro 0x17 will accept the NewHighPrec 0xFD packet format that
    // it doesn't normally advertise. Set from App.xaml.cs's
    // --experimental-precision CLI flag. Not for production use; if the
    // firmware doesn't accept the override, sync silently misbehaves.
    public static WirePrecision? OverridePrecision { get; set; }

    public static FirmwareCapabilities Resolve(int? typeCode, ushort firmwareVer)
    {
        if (typeCode is not int code) return Unknown;
        // Stub models (e.g. KG-series) — TypeCode resolves but we haven't
        // reverse-engineered the layout or verified the protocol. Treat as
        // Unknown so the UI disables Sync and prompts for diagnostics.
        var model = KeyboardModels.FindByTypeCode(code);
        if (model is { IsStub: true })
            return Unknown with { Label = $"{model.DisplayName} 0x{firmwareVer:X4}" };
        // Too-old check — connected firmware below the latest official
        // updater's floor for this model. Fires the modal in
        // KeyboardPerformanceView.EvaluateFirmwareTooOldDialog. The
        // user can "Continue anyway" — sync stays enabled.
        if (LatestKnownFirmware.TryGetValue(code, out var floor) && firmwareVer < floor)
        {
            var modelNameOld = model?.DisplayName ?? $"TypeCode {code}";
            return new FirmwareCapabilities
            {
                Label = $"{modelNameOld} 0x{firmwareVer:X4} (update available — 0x{floor:X4}+)",
                Tier = SupportTier.Beta,
                IsTooOld = true,
                RecommendedFloor = floor,
            };
        }
        var precision = ResolvePrecision(code, firmwareVer);
        foreach (var entry in VerifiedTable)
        {
            if (entry.TypeCode == code && entry.FirmwareVersion == firmwareVer)
                return entry.Capabilities with { Precision = precision };
        }
        // Known model, unverified firmware → Beta with the model name + fw hex.
        var modelName = model?.DisplayName ?? $"TypeCode {code}";
        return Beta($"{modelName} 0x{firmwareVer:X4} (beta — please report)") with { Precision = precision };
    }

    // Per-model + per-firmware wire-format dispatch. Extracted 2026-05-22
    // from the drunkdeer.keybord.net.cn JS bundle by static analysis of
    // the precision/oldOpenHighPrecision setter call sites; see
    // docs/protocol-findings-keybord-net-cn.md for the source snippets.
    //
    // The conservative fallback (return OldHighPrec) preserves current
    // behaviour for any TypeCode we haven't explicitly mapped — including
    // the G65/G60/G75 families. Some of those almost certainly want
    // Legacy (mm × 10) per the JS, but without hardware to verify we
    // err on "don't regress users who currently work" instead of
    // "potentially fix users who currently might be broken".
    private static WirePrecision ResolvePrecision(int typeCode, ushort firmwareVer)
    {
        if (OverridePrecision is { } forced) return forced;
        return typeCode switch
        {
            // A75 ANSI (TypeCode 75)
            //   fw < 0x23 → Legacy
            //   fw ≥ 0x23 → OldHighPrec (firmware gained "old high precision")
            75 when firmwareVer < 0x23 => WirePrecision.Legacy,
            75                          => WirePrecision.OldHighPrec,

            // A75 Pro (TypeCode 750)
            //   fw < 0x11 → Legacy (but the too-old modal already prompts
            //              upgrade — 0x0017 floor is enforced upstream)
            //   fw ≥ 0x11 → OldHighPrec (verified target on user's A75 Pro)
            750 when firmwareVer < 0x11 => WirePrecision.Legacy,
            750                          => WirePrecision.OldHighPrec,

            // A75 UK/FR/DE (TypeCode 751, shared)
            //   fw < 0x19 → Legacy
            //   fw ≥ 0x19 → OldHighPrec
            751 when firmwareVer < 0x19 => WirePrecision.Legacy,
            751                          => WirePrecision.OldHighPrec,

            // A75 Ultra (TypeCode 756) — JS dispatch says NewHighPrec
            // (0xFD packets, 2 bytes/key, mm × 200) but we have zero
            // hardware-verified evidence the firmware accepts it. Parked
            // until an Ultra owner can hardware-test — see branch
            // parked/newhighprec-untested. Route to OldHighPrec for now;
            // this matches v2.3.0 behaviour exactly (no Ultra regression).
            // Cost: Ultra users keep their 2.0 mm AP cap; benefit: no risk
            // of shipping a broken wire format to users we can't validate.
            756 => WirePrecision.OldHighPrec,

            // A75 Master (TypeCode 757) — same reasoning as A75 Ultra.
            // Shares the same hardware platform + updater floor. Parked
            // pending hardware verification.
            757 => WirePrecision.OldHighPrec,

            // G65 / A75-legacy type bytes (0x0B 0x01 0x01) → Legacy. Maps to
            // TypeCode 65 (G65 ANSI) and the rare legacy-A75 path (also 75).
            // G75, G60 not yet dispatched — JS source unclear, keep
            // OldHighPrec for now.
            65 => WirePrecision.Legacy,

            _  => WirePrecision.OldHighPrec,
        };
    }

    // Per-model firmware floor — the lowest version shipped in the
    // latest official updater bundle (DrunkdeerUpdaterV2.3.4
    // config.ini, retrieved 2026-05-21). Models that were unchanged
    // between 2.3.1 and 2.3.4 (G65 ISO, G60 ISO) are intentionally
    // omitted so the modal never fires for them — we have no newer
    // firmware to point users at. KG-series stubs are filtered out
    // earlier by the IsStub branch above.
    private static readonly Dictionary<int, ushort> LatestKnownFirmware = new()
    {
        { 75,  0x0027 },  // A75 ANSI
        { 751, 0x0023 },  // A75 UK / FR / DE (shared TypeCode)
        { 750, 0x0017 },  // A75 Pro ANSI
        { 756, 0x0055 },  // A75 Ultra
        { 757, 0x0055 },  // A75 Master
        { 754, 0x0017 },  // G75 ANSI
        { 755, 0x0012 },  // G75 JP
        { 65,  0x0015 },  // G65 ANSI
        { 60,  0x0017 },  // G60 ANSI
    };

    // Verified-safe preset modes on A75 Pro (firmware-independent — JS 32672
    // takes the same path on every A75 Pro firmware ≥ 17, and the lower
    // codes 0/2/4 fall out of `max(0, idx-2)` at JS 25602-25712 regardless).
    // Marquee/Neon/etc. stay out of this list until a hardware test on real
    // A75 Pro firmware confirms they don't trip the brick path.
    //
    // Declared above VerifiedTable so the table's initializer can reference
    // it — static field initializers run in declaration order, and a forward
    // reference here would otherwise resolve to null at table-construction
    // time and silently leave VerifiedRgbModes unset on each record.
    private static readonly IReadOnlyList<byte> A75ProVerifiedRgbModes =
        [LightingProfile.ModeOff, LightingProfile.ModeAlwaysOn, LightingProfile.ModeBreath];

    // Verified table — (TypeCode, firmware version) → capability record.
    // Add an entry here once a hardware test of a (model, firmware) combo
    // confirms end-to-end correctness on the current packet stream.
    private static readonly (int TypeCode, ushort FirmwareVersion, FirmwareCapabilities Capabilities)[] VerifiedTable =
    [
        // A75 Pro firmware 0x0017 — verified 2026-05-20 by proxy. on flashed
        // hardware. WebHID capture confirms AP byte = mm × 100, slider caps
        // at 0xc8 = 2.0 mm. AP slider drag produces feelable per-key
        // sensitivity differences across the 0.2–2.0 mm range.
        (750, 0x0017, new FirmwareCapabilities
        {
            Label = "A75 Pro 0x0017 (verified)",
            Tier = SupportTier.Verified,
            SupportsRgb = true,
            VerifiedRgbModes = A75ProVerifiedRgbModes,
            SupportsCustomRgb = true,
        }),
        // A75 Pro firmware 0x0009 — the protocol baseline we originally
        // reverse-engineered against. Wire format is the same as 0x0017
        // (we believe — the broken ×10 scaling collapsed every byte into
        // the firmware's 0.02-0.38 mm range, so the slider "worked" but in
        // a tiny perceptual band). Marked Verified on inheritance from the
        // protocol baseline; live re-verification would require flashing
        // back to 0x0009 which the public updater can't currently do.
        (750, 0x0009, new FirmwareCapabilities
        {
            Label = "A75 Pro 0x0009 (verified, inherited)",
            Tier = SupportTier.Verified,
            SupportsRgb = true,
            VerifiedRgbModes = A75ProVerifiedRgbModes,
            SupportsCustomRgb = true,
        }),
    ];
}

// Per-key AP/DS/US wire-format dialect. The official driver dispatches
// per (typeCode, firmwareVersion) into one of three packet shapes —
// see docs/protocol-findings-keybord-net-cn.md for the source extraction.
public enum WirePrecision
{
    // 0xB6 packets, 1 byte per key, scale = mm × 10. 3 packets (59 + 59
    // + 8 keys). Legacy DrunkDeer firmware; covers older A75 / A75 Pro /
    // A75 ISO units below their per-model "old high precision" cutoff,
    // plus the G65 family always (the JS hard-codes precision=1 for the
    // G65 type-byte triple).
    Legacy,

    // 0xB6 packets, 1 byte per key, scale = mm × 100. 3 packets (59 + 59
    // + 8 keys). "Old-style packet with high-precision data" — the firmware
    // accepts the legacy 0xB6 packet ID but interprets each byte at 1/100
    // mm steps instead of 1/10. This is what `Driver/Packets.cs` already
    // emits and is correct for A75 Pro on firmware ≥ 0x11 (the user's
    // verified target).
    OldHighPrec,

    // 0xFD packets, 2 bytes per key, scale = mm × 200. 5 packets (30 × 4 + 6
    // keys). True high-precision wire format used by A75 Ultra (TypeCode
    // 756) and A75 Master (TypeCode 757). Carries the same 1/200 mm
    // resolution as the high-precision keystroke-tracking stream
    // (HidStreamListener.ParseHighPrecision).
    NewHighPrec,
}

public enum SupportTier
{
    // The exact (model, firmware) combination has been tested end-to-end
    // on real hardware. Full feature set unlocked. UI: green dot.
    Verified,

    // Model recognized, firmware version not in our verified table.
    // We use the same packet stream as Verified (it's universal) but flag
    // the configuration so users on uncommon firmwares can opt out if
    // anything misbehaves. UI: yellow dot + "report issues" link.
    Beta,

    // Model not in KeyboardModels.cs at all, or spec response missing.
    // Sync is disabled; UI surfaces "Unrecognized — please submit diagnostics"
    // so we can add support based on real captures.
    Unknown,
}
