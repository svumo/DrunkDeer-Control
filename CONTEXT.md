# DrunkDeer-Control — Domain Context

Canonical glossary. The codebase has confusing terminology (especially "gen 1" vs "gen 2") that's been used inconsistently across sessions and docs. This file is the single source of truth for what each term means. Update it inline when a term is resolved, not in batches.

---

## Site vs Firmware: the two senses of "gen 2"

The word "gen 2" appears in this codebase referring to **two completely unrelated things**. They are orthogonal — gen-2 firmware works with the gen-1 site, and the gen-2 site dispatches to both gen-1 and gen-2 firmware.

### Gen-1 site / Gen-2 site

- **Gen-1 site** = `drunkdeer-antler.com`. The older official web driver. Its JS bundle in `tools/captures/antler-extracted/` is `index.CJWCGjvj.js`.
- **Gen-2 site** = `drunkdeer.keybord.net.cn`. The newer Vue/Vite official web driver. Its JS bundle in `tools/captures/keybord-net-cn/` is `index.QC8Mvgui.js`.
- There is **only one gen-2 site**. When a user picks a different keyboard model on it, the SAME JS dispatches differently internally — there is no separate per-model deployment. The earlier intuition that "newer firmware loads a different site" is wrong; the site routes everything through one JS bundle with internal `(TypeCode, firmware)` dispatch.
- Neither site exposes a real `index.json` catalog. The catalog is *embedded inside the JS bundle* and must be extracted via static analysis. Past attempts: `tools/captures/keybord-net-cn/extract.mjs`, `docs/protocol-findings-keybord-net-cn.md`.

### Gen-1 firmware / Gen-2 firmware

- **Gen-1 firmware** = older DrunkDeer hardware. Vendor ID `0x352D`. `MaxOutputReportLength == 64`. HID Report ID prefix on writes is implicit.
- **Gen-2 firmware** = newer DrunkDeer hardware with OEM-relabel variants. Vendor IDs include `0x352D`, `0x19F5`, others. `MaxOutputReportLength == 65`. HID Report ID `0x04` is **required** on writes.
- The single reliable indicator that you're on gen-2 firmware is `MaxOutputReportLength == 65`. See `docs/keyboard-protocol-gen2.md`.
- Gen-2 firmware is handled by `Driver/PacketsGen2.cs`, `Driver/Gen2KeyboardChannel.cs`, `Driver/Gen2WebHidChannel.cs`.

### Why this gets confused

The user's primary keyboard is **A75 Pro gen-1 firmware** (64-byte HID reports). When they observe the gen-2 site handling a friend's A75 Pro gen-2 firmware differently, the natural assumption is "different site = different protocol path." In reality, the same site is doing internal dispatch on the HID descriptor size + firmware version + TypeCode.

---

## Protocol families

Different DrunkDeer firmware variants speak structurally different protocols, not just different transport details. There are currently **two protocol families** in the wild — and the dialects below (`WirePrecision`) only apply within the first family.

### `drunkdeer-gen1-alphabet`

Used by:
- Gen-1 firmware (VID `0x352D`, 64-byte HID, no Report ID requirement) — covers the user's A75 Pro 0x0017 and the documented G65 / A75 / Ultra / etc. catalog.
- Gen-2 firmware non-OEM variants (VID `0x352D` newer, 65-byte HID, Report ID `0x04` required) — same opcodes, just bigger HID reports. Documented in `docs/keyboard-protocol-gen2.md`.

Operations are independent opcodes (`0xA0` identity, `0xB5` common-switch, `0xB6` per-key, `0xA7/0xA8` RTP authority, `0xAA` reset/clear, `0xFC` pair tables, `0xFD` pull queries). See `docs/keyboard-protocol.md` for the full opcode map and `Driver/Packets.cs` for the C# builders.

Within this family, per-key `0xB6` writes have three sub-dialects (see "Wire-format dialects" below).

### `drunkdeer-extended-gateway`

Used by:
- Gen-2 OEM firmware variants (VID `0x19F5 / 0xFB5C` confirmed; likely other OEM-relabel variants too). 64-byte HID, no Report ID.

A single envelope format wraps all operations: `[0x55, sub_cmd, 0x00, cs, length, addr_lo, addr_hi, is_last, data...]`. Sub-commands (e.g. `0xA1` WriteKeyTriggerChunk, `0xA5` WriteLwPairs, `0x04` ReadBaseBlock) select the operation. Per-profile data is memory-addressed: profile slot N writes to address `N × 0x400`. Per-key records are 8-byte structured `KeyTriggerEntry` values with bit-packed fields.

Documented in `docs/gen2-wire-format-confirmed.md`, derived from a USBPcap on tester B's A75 Pro OEM (firmware 1.7) and cross-validated against `deerios/DrunkDeerSDK::protocol/base_messages.yaml`. Implemented in `Driver/PacketsGen2.cs`.

### Disambiguation

`hidReportSize == 65` is the reliable indicator of *transport*-level gen-2, but it does **not** tell you which protocol family the firmware speaks. The protocol family is determined by which command alphabet the firmware *responds* to. In practice today:

- VID `0x352D` (any HID size): `drunkdeer-gen1-alphabet`
- VID `0x19F5` (and likely future OEM VIDs): `drunkdeer-extended-gateway`

Schema treatment: see [docs/adr/0002-protocol-families.md](docs/adr/0002-protocol-families.md). Each firmware variant in a descriptor carries a `protocolFamily` field selecting one of the two shapes.

---

## Wire-format dialects (within `drunkdeer-gen1-alphabet` only)

Per `Driver/FirmwareCapabilities.cs::WirePrecision` and `docs/protocol-findings-keybord-net-cn.md`. These dialects apply only to the per-key `0xB6` writes within the gen-1 alphabet — they have no equivalent in the extended-gateway family, which uses memory-addressed `KeyTriggerEntry` records instead.

| Dialect      | Packet ID | Bytes/key | Scale     | Used by                                              |
|--------------|-----------|-----------|-----------|------------------------------------------------------|
| Legacy       | 0xB6      | 1         | mm × 10   | G65 always; A75 ANSI fw < 0x23; A75 Pro fw < 0x11; A75 ISO fw < 0x19 |
| OldHighPrec  | 0xB6      | 1         | mm × 100  | A75 ANSI fw ≥ 0x23; A75 Pro fw ≥ 0x11; A75 ISO fw ≥ 0x19            |
| NewHighPrec  | 0xFD      | 2         | mm × 200  | A75 Ultra (TypeCode 756); A75 Master (TypeCode 757) — *per JS bundle, not hardware-verified in this codebase* |

The codebase currently routes Ultra/Master to `OldHighPrec` despite the JS bundle saying they want `NewHighPrec`. This is a deliberate conservatism flag in `ResolvePrecision` — owner has no Ultra/Master hardware to verify.

---

## Descriptor

The per-keyboard data record that drives the driver. A descriptor file lives at `descriptors/keyboards/{ModelName}.json` and contains:

- **Identity**: TypeCode, TypeBytes, PIDs, display name, internal name (profile prefix), `siteModelKey` (mapping to upstream catalog JSON name)
- **Layout reference**: `keyNamesRef` pointing to `descriptors/layouts/{LayoutName}.json` (the 126-element key-name array)
- **Firmware variants**: array of one record per firmware version, each fully self-contained (no inheritance), containing:
  - Version number, display label
  - Wire-format details: HID report size, report ID requirement, precision dialect, AP/DS/US ranges and scales
  - Capabilities: per-feature flags (RGB presets, RGB custom, knob, rapid trigger, RDT, LW, etc.)
  - Opcodes: structured objects per packet builder, with named field placeholders and chunking specs
  - Evidence registry: per-feature `verifiedOn` arrays with kind = `hardware` / `har` / `usbpcap` / `js-static` and metadata

The driver becomes an interpreter that consumes descriptors. No per-keyboard C# code is added when supporting a new keyboard — only a new descriptor file.

Schema is locked by [docs/adr/0001-descriptor-schema.md](docs/adr/0001-descriptor-schema.md). Current version: `1`.

---

## Trust tiers (legacy term, being replaced)

Today's `SupportTier` enum (Verified / Beta / Unknown) at `Driver/FirmwareCapabilities.cs::SupportTier` is a single-value tier per (model, firmware). The descriptor schema replaces this with a per-feature evidence registry — RGB may be verified while RDT is unverified on the same firmware. The legacy enum stays in code until the descriptor migration replaces the resolve path.
