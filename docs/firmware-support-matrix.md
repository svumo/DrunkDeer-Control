# DrunkDeer Control — firmware support matrix

This is the "is my keyboard supported" reference. Tiers correspond to the
`SupportTier` enum in [Driver/FirmwareCapabilities.cs](../Driver/FirmwareCapabilities.cs);
the in-app pill in the keyboard header reads the same source.

## Tier meanings

| Tier | UI pill | What it means |
|---|---|---|
| **Verified** | 🟢 green dot | We have WebHID captures + on-hardware testing for this exact (model, firmware). Full feature set unlocked. |
| **Beta** | 🟡 yellow + "beta" badge | Model recognised, firmware version not yet verified. The same packet stream as Verified is used (no version branching observed in the protocol), but flagged so you can opt out if anything misbehaves. |
| **Unknown** | 🔴 red + "unrecognized" badge | The keyboard reports as a model we haven't reverse-engineered (currently: KG-series). Sync is disabled — please submit diagnostics so we can add support. |

## Current matrix

Last updated: 2026-05-20 (release 2.2.0).

### A75 Pro (TypeCode 750, PIDs `0x2391` / `0x2a08` / `0x2383`)

| Firmware | Tier | Notes |
|---|---|---|
| `0x0009` | Verified (inherited) | Older factory units. Original reverse-engineering target. Wire format equivalent to 0x0017 per the JS bundle; "inherited" because the public updater can't downgrade for live retesting. |
| `0x0017` | **Verified** | Newer factory units + V2.3.4 updater bundle. WebHID-captured 2026-05-20; AP / Sync verified end-to-end. |
| any other | Beta | Same packet stream used; please report if anything misbehaves. |

### A75 (TypeCode 75, 751)

| Firmware | Tier | Notes |
|---|---|---|
| `0x0021` | Beta | V2.3.1 bundle baseline. Untested locally. |
| `0x0027` | Beta | V2.3.4 bundle (newer). Untested locally. |
| any other | Beta | — |

### A75 Ultra / A75 Master (TypeCode 756 / 757)

| Firmware | Tier | Notes |
|---|---|---|
| `0x0052` / `0x0055` | Beta | Different MCU family (NXP); we have no Ultra/Master hardware. Should work via the shared protocol but unverified. |

### G65 / G75 / G60 / X60 / G65-Lite / G65-m1/m2/m3 / G60-m1/m2/m3 / G75-JP / A75-UK/FR/DE

| Firmware | Tier | Notes |
|---|---|---|
| any | Beta | All recognised by the resolver, all share the universal protocol. No on-hardware testing on our side. |

### KG645U / KG650U / KG650 (stubs)

| Firmware | Tier | Notes |
|---|---|---|
| any | **Unknown** | TypeBytes `(0x0d, 0x01, 0x02)`, `(0x0f, 0x01, 0x01)`, `(0x0f, 0x01, 0x02)` from the V2.3.4 bundle. Detected so the keyboard shows up in telemetry / debug logs, but no layout or protocol verification done. Sync disabled. |

### Unlisted models

Anything the resolver can't identify (no TypeCode match) falls through to **Unknown**. Sync is disabled. The connection pill shows "unrecognized" and the debug log captures the raw spec response — that's the right artifact to share with us in a diagnostics submission.

## Getting your config verified

If you're on a Beta or Unknown tier and want it promoted to Verified, the cheapest path is a WebHID capture of `drunkdeer-antler.com` doing the same actions on your hardware. Instructions for that flow will land in the "Submit diagnostics" feature (planned for 2.3.0). Until then, file a GitHub issue with:

- Output of the new "Capabilities" line in `%LocalAppData%\DrunkDeer Control\debug.log`
- Your keyboard model and a couple of test actions that didn't work as expected

We'll prioritise based on telemetry — the more users we see on a given firmware, the higher it climbs in the queue.

## Why we don't gate features per-firmware

Across every firmware we've captured to date, the wire protocol is **byte-identical**. The JS web driver has no `if (firmware >= X)` branches around packet construction; it sends the same bytes to every supported board. That means our app doesn't need per-firmware code paths either — it sends the universal packet stream and trusts the firmware.

The tier system exists to flag **trust**, not behaviour. A Beta tier doesn't mean we're sending different bytes; it means we haven't personally watched the firmware accept those bytes correctly. If a future firmware actually does diverge protocol-wise (we'd see it in WebHID captures), we'd add per-firmware overrides at the `FirmwareCapabilities` level — and the existing scaffold supports it.
