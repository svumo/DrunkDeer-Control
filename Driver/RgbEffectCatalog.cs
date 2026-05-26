namespace Driver;

// Single source of truth for the gen-1 driver's RGB effect catalog.
// Extracted 2026-05-27 from tools/captures/antler-extracted/index.CJWCGjvj.js
// via tools/captures/extract_a75_modes.mjs — the `ddeerA75ProColour` class's
// `m_color_names` array. Same 21 entries on every A75 / G65 / G75 / G60
// family class checked; we treat this catalog as the universal gen-1
// catalog for A75 Pro (gen-1 hardware) AND A75 Pro on gen-2 OEM hardware
// (which routes through the /drunk/ Vite driver — the same JS bundle).
//
// Wire code derivation: per JS source the effect's wire mode byte is
// `max(0, idx-2)` where idx is the position in `m_color_names`. Indices 0/1/2
// all map to code 0 — Off, Turbo, and Custom share the wire code but use
// different packet shapes. We expose Off in the dropdown; Custom is its own
// per-key painter (mode byte 0x13, separate packet stream); Turbo is hidden
// (RGB-on-turbo-slot is a niche feature we haven't surfaced).
public static class RgbEffectCatalog
{
    public sealed record Entry(
        byte Code,
        string DisplayName,
        bool IsVerifiedSafe,
        bool SupportsColor,
        bool SupportsSpeed);

    // 19 user-facing effects: Off + 18 presets. Custom is excluded — it's
    // exposed as a separate "Custom" toggle in the UI (different packet
    // shape, different code path). Turbo-mode-light is excluded — niche,
    // turbo-slot-only feature with no current UI surface.
    //
    // IsVerifiedSafe: hardware-confirmed not to brick A75 Pro firmware 0x09
    // / 0x17. Currently Off + the four codes Phase 1 / 2a tested. The rest
    // come from the JS catalog with no live hardware verification — they
    // SHOULD work since the official driver emits them, but a defensive
    // brick-warning fires on first sync of an unverified mode.
    //
    // SupportsColor / SupportsSpeed mirror the JS catalog's `useColourful`
    // and the implicit speed-disabled-for-static-modes convention. UI
    // disables the irrelevant sliders per effect.
    public static readonly Entry[] All =
    [
        new(0,  "Off",                       true,  false, false),
        new(1,  "Rotate Marquee",            true,  true,  true),
        new(2,  "Wave Spectrum",             true,  true,  true),
        new(3,  "Surf to the right",         false, true,  true),
        new(4,  "Breath",                    true,  true,  true),
        new(5,  "Center Surfing",            false, true,  true),
        new(6,  "Spectrum",                  false, true,  true),
        new(7,  "Ripple",                    true,  true,  true),
        new(8,  "Always light",              false, true,  false),
        new(9,  "Light by press",            false, true,  true),
        new(10, "Serpentine to the center",  false, true,  true),
        new(11, "Colorful fountain",         false, true,  true),
        new(12, "Laser Key",                 false, true,  true),
        new(13, "Glowing Fish",              false, true,  true),
        new(14, "Surfing Cross",             false, true,  true),
        new(15, "Heart",                     false, true,  true),
        new(16, "Traffic",                   false, true,  true),
        new(17, "Gluttonous Snake",          false, true,  true),
        new(18, "Raindrops",                 false, true,  true),
    ];

    public static Entry? FindByCode(byte code)
    {
        foreach (var e in All)
            if (e.Code == code) return e;
        return null;
    }

    public static bool IsVerifiedSafe(byte code) =>
        FindByCode(code)?.IsVerifiedSafe ?? false;
}
