# DrunkDeer RGB / Custom-Lighting Wire Protocol

Companion to [keyboard-protocol.md](keyboard-protocol.md). Covers the RGB
custom-lighting write path, which the app does **not** implement yet (we only
*read* RGB state from the spec response today).

## Provenance & verification status

This was derived two ways and cross-checked:

1. **Community C# port** (`DrunkDeerSharp`, shared informally — *not* vendored
   into this repo, reference only, unlicensed). `KeyboardProtocol.WriteRgb`.
2. **Official Antler driver JS** — two copies cross-checked:
   - `drunkdeer_minifiedjs.js` (beautified/trimmed derivative, 1.5 MB / 33k
     lines, supplied by the contributor; build/version unknown). Line
     citations in the "Source map" section refer to this file.
   - **Live production bundle** pulled directly from
     `https://drunkdeer-antler.com/js/index.CJWCGjvj.js` on 2026-05-19
     (build hash `CJWCGjvj`, 6.2 MB raw-minified). The RGB code is
     **byte-identical** to the beautified copy and to the byte map below —
     so this doc reflects *current production*, not a stale build.

**Result: the community format is a faithful, byte-exact port of the official
driver, and matches the live production bundle.** Every header byte matches.
Two behavioural divergences in the community port (packet count, commit) —
documented below.

| Aspect | Status |
|---|---|
| Custom-light packet header byte map | ✅ Verified against official JS |
| `0x80 + index` entry encoding, 4 bytes/key | ✅ Verified (JS line 32291) |
| 13 keys/packet, 52-byte slices | ✅ Verified (JS line 32323-32324) |
| `modeIndex = 0x13` (19) for custom light, high-precision | ✅ Verified (JS line 20937) |
| Packet count = fixed 6 (G60/G65 fam) / 7 (others) | ✅ Verified (JS line 32307-32320) |
| No per-packet ACK (fire-and-forget) | ✅ Verified (JS `setTimeout(…,0)`, line 32326) |
| Mode-select packet byte map (preset modes) | ✅ Verified (JS 24482, 29940-29942) |
| Mode-select arg shuffle: `data[6]=speed, data[7]=brightness, data[8]=preset` | ✅ Verified (JS 21185-21193, 24482) |
| Brightness slider range 0..9, speed slider range 0..9 | ✅ Verified (JS 21180, 21188-21189) |
| **A75 Pro** firmware mode codes: Off=0, AlwaysOn=2, Breath=4 | ✅ Verified in JS (firmware-independent, JS 32672 + 25602-25712) |
| **A75 Pro** Mode=Off (0) on-device | ✅ Verified 2026-05-20, A75 Pro firmware 0x09. Clean LED-off, no boot loop. |
| **A75 Pro** Mode=AlwaysOn (2) on-device | ✅ Verified 2026-05-20, A75 Pro firmware 0x09. Constant glow, brightness 1–9 stepped clean. |
| **A75 Pro** Mode=Breath (4) on-device | ✅ Verified 2026-05-20, A75 Pro firmware 0x09. Animation responds to speed 1–9. |
| Brightness 0–9 safe on **A75 Pro** | ✅ Verified 2026-05-20. Full range exercised, no firmware issues. |
| Marquee / Neon / other preset codes on **A75 Pro** | ⚠️ NOT tested. UI locks them behind "Hardware verification pending". |

> Hardware caveat carried from the contributor: sending invalid RGB data
> **soft-bricks the A75**, recoverable only by spamming correct data during the
> boot loop. There is no in-place A75 firmware fix. Treat every value that
> reaches `0xAE` as load-bearing. Do not ship an RGB write to hardware without
> the input guards in the last section.

## Custom-light write packet (`0xAE`)

Per-frame, the keyboard receives **6 or 7** packets of this form. Source of
truth: `sendTurboLedModeData` (JS line 32325 / builder at 29944-29948).

Byte indices below are the **HID report buffer** (C# convention: `data[0]` is
the `0x04` report ID; the JS `Uint8Array` index `a[i]` maps to `data[i+1]`).

| `data[]` | JS `a[]` | Value | Meaning |
|---|---|---|---|
| `data[0]` | — | `0x04` | HID report ID (added host-side) |
| `data[1]` | `a[0]` | `0xAE` (174) | Command: LED data |
| `data[2]` | `a[1]` | `0x01` | Subcommand |
| `data[3]` | `a[2]` | `0`/`1` | Turbo-mode colour slot |
| `data[4]` | `a[3]` | `0x00` | — |
| `data[5]` | `a[4]` | `0x13` (19) | Mode index (custom light, high-precision kbds) |
| `data[6]` | `a[5]` | `0x06` | Constant |
| `data[7]` | `a[6]` | `0..9` | Brightness (slider value) |
| `data[8]` | `a[7]` | `0xFF` | End-of-header marker |
| `data[9..60]` | `a[8..59]` | entries | Up to 13 key entries, 4 bytes each |
| `data[sliceLen+9]` | `a[r+5]` | `0xFF` | Trailing sentinel |

### Entry encoding (JS line 32291)

```js
w.push(128 + Q[s][k].value, t, a, r)   // t=RR, a=GG, r=BB sliced from "#RRGGBB"
```

Each key entry is 4 bytes: `[ 0x80 + layoutIndex, R, G, B ]`. Keys "off" are
`(0,0,0)`. The official driver stores colour as hex-string pairs and parses
them at build time (`+("0x"+E[w])`); the community port keeps raw bytes — the
emitted wire bytes are identical, the C# approach is just simpler.

### Packet count (JS line 32307-32320) — DIVERGENCE

Official driver hardcodes by model family:

- `ddeerG60*`, `ddeerG65*`, `ddeerG65lite` → **6** packets
- everything else (incl. A75 / A75 Pro) → **7** packets

The community port instead computes `ceil(rgbIndexCount / 13)`. For A75 that's
`ceil(82/13)=7` ✓, but for G65 it yields `5`, not the official `6`. **When we
implement this, use the official fixed table, not the derived count.**

### Commit — DIVERGENCE

The official `transmit_color_report_packet` fires the 6/7 packets via
`setTimeout(…,0)` and **does not** send any commit afterward. The community
port adds a `0xA0 0x02` read-back as a synchronisation barrier — that is a
custom host-side hack (`0xA0 0x02` is otherwise the identity request), **not
part of the official protocol**. The whole-keyboard blink on update is
firmware-inherent and present in the official driver too; the extra `0xA0`
is not what causes it and is probably unnecessary.

## Mode-select / "lighting off" packet (`0xAE`, no entries)

Preset modes and "off" use a shorter `0xAE/0x01` packet built by
`sendLedModeData` (JS 29940-29942), dispatched via `event_set_led_mode`
(JS 32281) called as `event_set_led_mode(0, modeCode, …, brightness, …)`
from `apiSaveColourSolution` (JS 24482):

| `data[]` | JS `a[]` | Value | Meaning |
|---|---|---|---|
| `data[1]` | `a[0]` | `0xAE` | Command |
| `data[2]` | `a[1]` | `0x01` | Subcommand |
| `data[3]` | `a[2]` | `0x00` | Turbo slot |
| `data[4]` | `a[3]` | `0x00` | First arg (always 0 in `apiSaveColourSolution`) |
| `data[5]` | `a[4]` | mode | **Firmware mode code** — see "Mode codes per family" |
| `data[6]` | `a[5]` | `0..9` | **Speed** (animated modes only; ignored by Off/Always-On) |
| `data[7]` | `a[6]` | `0..9` | **Brightness** (hard ceiling — values ≥10 brick A75 Pro) |
| `data[8]` | `a[7]` | param | Preset colour-index / colourful flag — send `0` for plain preset writes |

Wire-layer arg shuffle (verified at JS 24482): `apiSaveColourSolution(_, A, e, B, C)`
calls `event_set_led_mode(0, Q, C, B, A)`, where in `apiSaveColourSolution`'s caller
(`ColorPanel`, JS 20988) `B = sliderColorList[0].value = brightness` and
`C = sliderColorList[1].value = speed`. `sendLedModeData(Q', A', e', B', C')` then
emits `data[5]=A', data[6]=e', data[7]=B', data[8]=C'` — so:
- `Q` (mode) → `data[5]`
- `C` (speed slider) → `data[6]`
- `B` (brightness slider) → `data[7]`
- `A` (colour index) → `data[8]`

Slider ranges (JS 21180, 21188-21189): brightness 0..9, speed 0..9 — both hard,
both confirmed in the production UI.

### Mode codes per family

The byte sent at `data[5]` depends on which colour table the keyboard is bound to:

| Keyboard | colourObject class | `.code` populated? | Path taken | Off | Always On | Breath |
|---|---|---|---|---|---|---|
| **A75 Pro** (kbd type 750) | `zw` (JS 25598) | ❌ no | `max(0, array_index - 2)` | **0** | **2** | **4** |
| A75 Ultra (type 756) | `ms` (JS 28562) | ✅ yes | `t.code` direct | 240 | 1 | 3 |
| A75 Master (type 757) | (per JS 28950) | ✅ yes | `t.code` direct | 240 | 1 | 3 |
| A75 base (type 75) | `Hw` | depends on firmware | mixed | TBD | TBD | TBD |

**A75 Pro path is firmware-independent**: line 32672 sets `precision=2` AND
`oldOpenHighPrecision=true` together when firmware ≥ 17. The transform at 24482
fires `Q = t.code` only when `precision===2 && !oldOpenHighPrecision` — A75 Pro
never matches that, so the legacy `max(0, array_index - 2)` path always wins.
Off/AlwaysOn/Breath sit at array indices 0/4/6 in the `zw` table → wire codes
0/2/4. Values verified against JS 25602-25712.

The community port's `SetLightingOff` writing `data[5]=0x05, data[6]=0x09` was
targeting A75 base, not A75 Pro — do not copy that constant for A75 Pro work.

## Soft-brick recovery (A75 Pro)

> **No in-place firmware repair exists for A75 Pro.** Treat this section as the
> only safety net.

If an invalid `0xAE` write puts the keyboard into a boot loop (LEDs flashing
on/off, key inputs not registering, Windows enumerating then disconnecting
the device repeatedly), the recovery procedure is:

1. **Do not unplug.** The boot-loop window is the only time the firmware
   accepts a corrective write.
2. Send a known-good `0xAE` mode-select packet **continuously** during the
   loop: mode=0 (Off), brightness=0, speed=0, all other slots 0. Bytes:
   `04 AE 01 00 00 00 00 00 00 …` (rest zero-padded to 64 bytes).
3. Use a tight loop with no inter-packet delay — the firmware's accept
   window between reboots is sub-100ms.
4. Within 5-30 seconds the firmware should re-stabilize and stop the loop.
5. Once stable, immediately follow with another `mode=0, brightness=0`
   write to commit a known-good state, then close the HID handle.

A working recovery host is essentially: open HID stream, loop `WritePacket`
with the 64-byte all-zero (after header) packet, no sleep, until the device
stops disconnecting. The official Antler driver does not expose this — it's
a manual operation.

**Prevention is cheaper:** clamp brightness ≤ 9 AND speed ≤ 9 AND
whitelist mode codes at the API boundary (`Driver.Packets.BuildLedModePacket`),
*and* in the UI before the value reaches the builder. Defence in depth.

## Required input guards before any hardware write

The community library does **not** clamp these; we must, at the API boundary:

1. **Brightness → clamp to `0..9`.** `WriteRgb` writes `brightness` raw to
   `data[7]`; no public method validates it. This is the highest-risk
   soft-brick vector. The official UI sources it from a bounded slider; we
   have no such guarantee in a library call.
2. **modeIndex → whitelist** (custom light = `0x13`; only known-good mode
   codes otherwise).
3. **Entry index → assert `0 ≤ layoutIndex ≤ 127`** before `0x80 + index`
   (unchecked `(byte)` cast wraps silently otherwise).
4. **R/G/B** are already byte-typed (0–255) — safe by construction.
5. Treat **A75 Pro as unverified**: the grid (`RgbIndices*`) and the 0xAE
   map are confirmed in *code* but not on A75 Pro *hardware*. First on-device
   test must have the boot-loop recovery understood and ready.

## Source map (Antler JS, `drunkdeer_minifiedjs.js`)

- `29940-29942` `sendLedModeData` — mode-select packet builder
- `29944-29948` `sendTurboLedModeData` — custom-light packet builder
- `32281-32285` `event_set_led_mode`
- `32286-32293` `buildPkt_custom_led_mode_select` — entry array (`128+index`)
- `32306-32330` `transmit_color_report_packet` — packet count + slicing + send
- `20937` call site: high-precision passes `modeIndex = 19`
- `24482` `apiSaveColourSolution` → `event_set_led_mode` arg order

Bundle had **no** base64 data URIs (code-only), so the strip-regex prep step
was unnecessary for this file — it still applies to the full site bundle.
