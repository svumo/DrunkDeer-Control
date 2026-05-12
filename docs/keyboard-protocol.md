# DrunkDeer Keyboard USB HID Protocol Reference

Reference for the DrunkDeer USB HID protocol and the supported keyboard model catalog. This is a living document for contributors maintaining `Driver/` and the WPF app.

## 1. Methodology

Protocol details below were extracted by static analysis of the official web driver's JavaScript bundle at `https://drunkdeer-antler.com/`, specifically `index.CJWCGjvj.js` (~6.2 MB minified). No packet sniffing, devtools tracing, or hardware was required.

The bundler's minifier renamed local variables but preserved object property names, because dynamic property lookups (`obj["keyName"]`) would break otherwise. As a result every `B[7] = Number(I.keyboardObj.turboMode)`-style assignment is greppable. Local cache for working sessions: `C:\Users\skdes\AppData\Local\Temp\dd-index.js`.

Cross-reference verification: a real A75 Ultra `Profile1.json` from a user matches the JS-extracted layout exactly (91 / 91 named slots).

## 2. USB Devices

Primary vendor ID: **`0x352D`** (DrunkDeer, 13613 decimal).

### 2.1 PIDs requested by the JS driver

Filters passed to `navigator.hid.requestDevice`:

| PID | PID | PID | PID |
|---|---|---|---|
| 0x2382 | 0x2383 | 0x2384 | 0x2386 |
| 0x2387 | 0x238F | 0x2390 | 0x2391 |
| 0x2394 | 0x23B3 | 0x23B4 | 0x23B5 |
| 0x23B6 | | | |

### 2.2 Secondary VIDs (platform quirks)

| VID | Vendor | PIDs |
|---|---|---|
| `0x05AC` | Apple (1452) | `0x024F` (already in our code) |
| `0x19F5` | Keychron (6645) | `0xFB3E`, `0xFB42`, `0xFB50`, `0xFB5C`, `0xFB5E` |
| `0x1A81` | unknown | `0x2094` |

`Driver/KeyboardManager.cs` currently recognizes only 8 PIDs — a subset of the above. The Apple and Keychron entries are presumably due to OEM relabeling or USB descriptor quirks on certain platforms.

## 3. Keyboard Type Identification

The host sends an identity request packet beginning with `0xA0 0x02 0x00 ...`. The keyboard responds with the same prefix and the model is determined by bytes **[4], [5], [6]** of the response.

`Driver/KeyboardSpecs.GetKeyboardType` has a partial switch. The complete map from the JS:

| Type | Bytes (4,5,6) | Model | Layout key |
|---|---|---|---|
| 60 | (11, 3, 1) | G60 | `ddeerG60Profile` |
| 65 | (11, 2, 1) or (15, 1, 1) | G65 | `ddeerG65Profile` |
| 75 | (11, 1, 1) or (11, 4, 1) | A75 | `ddeerA75Profile` |
| 600 | (11, 3, 3) | G60m1 | `ddeerG60m1Profile` |
| 601 | (11, 19, 1) | G60m2 | `ddeerG60m2Profile` |
| 602 | (11, 21, 1) | G60m3 | `ddeerG60m3Profile` |
| 603 | (11, 6, 5) | X60 | `ddeerX60Profile` |
| 650 | (11, 2, 5) | G65 Lite | `ddeerG65liteProfile` |
| 651 | (11, 2, 3) | G65m1 | `ddeerG65m1Profile` |
| 652 | (11, 16, 1) | G65m2 | `ddeerG65m2Profile` |
| 653 | (11, 18, 1) | G65m3 | `ddeerG65m3Profile` |
| 750 | (11, 4, 3) | A75 Pro | `ddeerA75ProProfile` |
| 751 | (11, 4, 2) | A75 UK/FR/DE | `ddeerA75UKProfile` / `FR` / `DE` |
| 754 | (11, 4, 5) | G75 | `ddeerG75Profile` |
| 755 | (11, 4, 7) | G75 JP | `ddeerG75JPProfile` |
| 756 | (11, 4, 4) | A75 Ultra | `ddeerA75UltraProfile` |
| 757 | (11, 5, 4) | A75 Master | `ddeerA75MasterProfile` |

## 4. Keyboard Layouts

19 model layouts exist in the JS. Each is a 126-element array of `KeyName` strings; empty strings mark unused slots.

**Shared slot grid**: all models share a 126-slot grid imposed by the firmware. So slot 14 is `KP7` on the A75 family but `""` (empty) on the G65 family. Layouts with an F-row use slots 0–20 for it; layouts without an F-row leave those slots empty and start with `ESC` at slot 21.

| Layout | Named slots | Notes |
|---|---|---|
| `ddeerG75Profile` | 86 | Full-size with F-row and right nav column |
| `ddeerG75JPProfile` | 86 | Japanese ISO; includes `JAP_*` keys |
| `ddeerA75FRProfile` | 92 | ISO French |
| `ddeerA75DEProfile` | 92 | ISO German |
| `ddeerA75UKProfile` | 92 | ISO UK |
| `ddeerA75Profile` | 91 | ANSI; identical to `ddeerA75ProProfile` |
| `ddeerA75ProProfile` | 91 | ANSI; identical to `ddeerA75Profile` |
| `ddeerA75UltraProfile` | 91 | Identical to `ddeerA75MasterProfile`; differs from A75/A75Pro only at slots 99/100 (NUMS position swap) |
| `ddeerA75MasterProfile` | 91 | Identical to `ddeerA75UltraProfile` |
| `ddeerX60Profile` | 91 | Same body as A75 Ultra |
| `ddeerG65Profile` | 70 | Identical across G65 family |
| `ddeerG65liteProfile` | 70 | Identical to G65 |
| `ddeerG65m1Profile` | 70 | Identical to G65 |
| `ddeerG65m2Profile` | 70 | Identical to G65 |
| `ddeerG65m3Profile` | 70 | Identical to G65 |
| `ddeerG60Profile` | 63 | Identical across G60 family |
| `ddeerG60m1Profile` | 63 | Identical to G60 |
| `ddeerG60m2Profile` | 63 | Identical to G60 |
| `ddeerG60m3Profile` | 63 | Identical to G60 |

**Action item**: `Driver/KeyboardLayout.cs` currently hand-builds an 82-key A75 Pro layout. The JS has 91. Replace with the extracted layout.

## 5. Common Switch Packet

Header: `0xB5 0x00 0x1E 0x01 0x00 0x00 0x01 ...`

This packet writes the feature-toggle state. Verified byte map:

```
Offset  Field                       Values
------  --------------------------  --------------------------------
B[0]    0xB5                        fixed
B[1]    0x00                        fixed
B[2]    0x1E                        fixed
B[3]    0x01                        fixed
B[4]    0x00                        fixed
B[5]    0x00                        fixed
B[6]    0x01                        fixed
B[7]    TurboEnabled                0 / 1
B[8]    RapidTriggerEnabled         0 / 1
B[9]    (reserved)                  0
B[10]   LW + RDT combined enum      0=neither, 1=LW only,
                                    2=RDT only, 3=both
B[11]   RTMatchEnabled              0 / 1  (new feature, not wired
                                    in our code yet)
B[12..62]  (zero-padded)            0
```

### 5.1 Historic comment error in `Driver/Packets.cs`

The existing comment speculates about an `if valueT==1 then 0x00 otherwise rtp_lw` conditional. **No such conditional exists in firmware.** Conflict resolution between Turbo and other modes is enforced in the official driver's UI layer, not the device. Our packet builder should write the toggle bytes literally.

## 6. Other Packets (outliers)

Not part of the common switch:

| Function (JS name) | Packet prefix | Purpose |
|---|---|---|
| `sendTrackingStartData` | `[0xFD, 0x03, 0x01, ...]` | Enable Keystroke Tracking |
| `sendTrackingStopData` | `[0xFD, 0x03, 0x00, ...]` | Disable Keystroke Tracking |
| `sendLwReplaceData(v)` | `[0xFC, 0x0B, v, ...]` | Last Win Replace toggle |
| `sendRtModeDate(v)` | `[0xFD, 0x0C, v, ...]` | Auto Match Mode; `v` is 0..255, `255` = off |

## 7. Spec Response (READ direction)

The keyboard returns model state in the spec response packet. `Driver/KeyboardSpecs.cs` currently reads bytes 15, 16, 18, 19. The JS reads additional offsets:

| Offset | Field | Notes |
|---|---|---|
| 7 | turbovalue | Duplicate of [15] |
| 8 | rtvalue | Duplicate of [16] |
| 15 | turbovalue | |
| 16 | rtvalue | |
| 18 | rtdvalue (RTP) | |
| 19 | lwvalue | |
| 20 | profileIndex | High-precision mode only |
| 22 | colormodel | RGB main mode (5 modes) |
| 23 | colorspeed | |
| 24 | brightness | |
| 30 | rtMatchValue | RTMatch enabled; **not currently read** |
| 31 | autoMatchModeIndex | 0..255; `255` = RTMatch off |
| 32 | lw_replace | boolean |
| 34..36 | firmware version | 3 bytes (major, minor, patch) |

**Keystroke Tracking is not reported back from the keyboard** — it is a UI-managed state only.

## 8. Feature Support Matrix

Per the JS, all software-side toggles are universal across the current lineup; no per-model gating found. Hardware-dependent variation:

| Feature | Support |
|---|---|
| Turbo, Rapid Trigger, RT+, Last Win, RT Match | All models (universal) |
| RGB main (5 modes: Off / Marquee / Neon / Always On / Breath) | All current models |
| LED Strip (separate RGB channels) | All current models |
| Knob / Encoder | G4 variants only — the `G4` prefix marks knob-equipped models (49 references in JS) |
| Macros (recording + playback) | **Not supported.** The "record" UI is for keystroke visualization only, not macro playback |

## 9. Localization

The driver ships 7 languages: `en`, `zh-CN`, `zh-TW`, `ja`, `de`, `fr`, `ko`.

## 10. Firmware Update

The driver detects out-of-date firmware against three thresholds — `v34`, `v63`, `v81` — exposed as constants `Tg.v34`, `Tg.v63`, `Tg.v81`. When out-of-date firmware is detected the driver opens:

```
https://drunkdeer.com/pages/user-manual-select
```

…which links to the official updater. Latest binary observed at the time of extraction:

```
https://cdn.shopify.com/s/files/1/0671/4694/0719/files/DrunkdeerUpdaterV2.3.1.zip
```

### 10.1 `is_m_u_l` flag

Set when firmware ≤ `v34`. Gates certain profile features on older firmware. Likely "is multi-user lock" or "is mass-update lock" — exact meaning not confirmed.

### 10.2 External API

The driver references `https://api.drunkdeer.club` but it does not appear to be used for profile sync — possibly telemetry or version checks. **Out of scope for this app.**

## 11. References

### Source files in this repo

- `Driver/Packets.cs` — packet builders (see §5 for the comment-error fix)
- `Driver/KeyboardSpecs.cs` — type identification (extend per §3, spec read per §7)
- `Driver/KeyboardManager.cs` — VID/PID filter list (extend per §2)
- `Driver/KeyboardLayout.cs` — current hand-built A75 Pro layout, 82 keys; the JS layout has 91 and should replace it

### External

- Plan: `C:\Users\skdes\.claude\plans\keyboard-performance-and-remap.md`
- JS bundle cache: `C:\Users\skdes\AppData\Local\Temp\dd-index.js`
- Source URL: `https://drunkdeer-antler.com/index.CJWCGjvj.js`
