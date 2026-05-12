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

### 5.2 UI-level coupling rules (mirrored from official driver)

The firmware itself does **not** validate conflicts between the toggle bits — it will accept any combination of byte 7 / 8 / 10 / 11 values. But the official DrunkDeer driver enforces two coupling rules in its UI layer that we mirror, so our `Sync` action never writes a non-sensical state to the device:

1. **Turbo ⊥ Keystroke Tracking** — mutually exclusive. Enabling one disables the other.
2. **LW (or RDT) on → force RT on, force Turbo off** — the firmware's LW/RDT deconfliction pipeline runs **inside** the Rapid Trigger pipeline, and Turbo bypasses both. So LW only behaves correctly when RT is engaged and Turbo is off.

Both couplings are implemented in `KeyboardDebugWindow.OnModeStripToggle` ([WpfApp/Components/KeyboardView/KeyboardDebugWindow.xaml.cs](../WpfApp/Components/KeyboardView/KeyboardDebugWindow.xaml.cs), see the Turbo/Keystroke branch around lines 399–408 and the LW/RDT branch around lines 414–426). The ModeStrip control surfaces the coupled changes back to the user via two-way binding on `RapidTriggerEnabled` / `TurboEnabled`.

## 6. Other Packets (outliers) — summary

Not part of the common switch. See §9 for the full byte maps and the no-ACK
caveat; this table is just the index.

| Function (JS name) | Packet prefix | Purpose |
|---|---|---|
| `sendTrackingStartData` | `[0xFD, 0x03, 0x01, ...]` | Enable Keystroke Tracking |
| `sendTrackingStopData` | `[0xFD, 0x03, 0x00, ...]` | Disable Keystroke Tracking |
| `sendLwReplaceData(v)` | `[0xFC, 0x0B, v, ...]` | Last Win Replace toggle |
| `sendRtModeDate(v)` | `[0xFD, 0x0C, v, ...]` | Auto Match Mode; `v` is 0..255, `255` = off |
| `sendClearRtpData(m)` | `[0xFC, 0x0A, m, ...]` | Clear LW/RDT pair table; prereq for §8 |
| `sendCreateLwData(p)` | `[0xFC, 0x01, 0x00, n, …]` | Register LW pair table; see §8 |
| `sendRemapKeyData` | `[0xA0, 0x02, 0x04, …]` | Per-layer remap; see §10 |

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

## 8. Last Win pairs

The Common Switch packet's LW bit (byte 10) is just a master switch. Without a pair table registered, the firmware has nothing to deconflict, so enabling LW alone does nothing observable. The pair table lives in a separate packet.

### 8.1 Wire format

```
B[0]   0xFC
B[1]   0x01
B[2]   0x00
B[3]   pairCount                  // up to 14 pairs (56-byte payload window)
B[4..] per-pair quad, 4 bytes each:
       [mainSlot, triggerSlot, 0x00, 0x00]
```

Slot values are **firmware slot indices** (0..125), not HID usage codes. The official driver derives them from `keyboard.value` in each model's layout map. For A75 Pro: `W=44`, `A=64`, `S=65`, `D=66`. These are the same slot numbers the per-key AP/DS/US packets (`BuildPacketKeyPoint`) address.

Bidirectional behaviour requires **both directions** as separate entries — the firmware honours directionality per entry. So `A ⇄ D` is two pairs: `(64, 66)` and `(66, 64)`.

Builder: `Packets.BuildCreateLwPairsPacket` in [Driver/Packets.cs](../Driver/Packets.cs).

### 8.2 Required preamble: clear-rtp

The pair packet must be preceded by a clear-rtp packet that primes firmware state and re-states the LW/RDT combined mode:

```
B[0]   0xFC
B[1]   0x0A
B[2]   mode                       // 0=none, 1=LW only, 2=RDT only, 3=both
                                  // (same enum as Common Switch B[10])
```

Builder: `Packets.BuildClearRtpPacket` in [Driver/Packets.cs](../Driver/Packets.cs). The official driver's pair-save sequence is always **clear → pairs → common-switch**, in that order.

### 8.3 Open behaviour question

With byte-correct `clear → pairs → common-switch` packets sent, the firmware **acknowledges** every packet but does **not** activate LW switching in practice on the current implementation. The official driver requires the user to manually configure pairs in its UI ("select 2 keys" flow) and runs the full save sequence documented in §11 — which is significantly more than our current Sync does.

Agent A is reverse-engineering whether the full `rtpSaveToKeyboard` flow (per-key RTP authority + download packets, with a 196-packet remap stream first) is required to commit pairs. See §11 for the open frontier.

## 9. Outlier global toggles (no-ACK)

Three feature toggles live outside the Common Switch packet and outside the LW pair table. They share a common property the firmware does **not** echo them back — so a read-after-write ACK check would time out (each timeout is ~3 s). Send them with `HidStream.WritePacketNoAck` ([Driver/HidDeviceExtensions.cs:52](../Driver/HidDeviceExtensions.cs)), which is fire-and-forget.

| Toggle | Wire | Source builder | Notes |
|---|---|---|---|
| Keystroke Tracking | `[0xFD, 0x03, <0\|1>, 0×60]` | `Packets.BuildKeystrokeTrackingPacket` | UI-only state on read side (§7) |
| Last Win Replace | `[0xFC, 0x0B, <0\|1>, 0×60]` | `Packets.BuildLastWinReplacePacket` | Spec-response byte 32 reflects it |
| Auto-Match Mode | `[0xFD, 0x0C, <0..255>, 0×60]` | `Packets.BuildAutoMatchModePacket` | `255` = off; spec-response byte 31 reflects it |

These packets are write-only. Using the normal `TryWritePacket` path (which calls `WritePacket` and validates the read-back) will log a mismatch and return false, even though the write succeeded. The dedicated no-ACK path was added precisely to avoid that false-failure case.

## 10. Remap packets

Per-layer keymap. The host sends 14 packets per layer, 9 entries per packet, covering all 126 firmware slots. The official driver writes 14 layers (1..14) on every full save, for 196 packets total — see §11.

### 10.1 Packet header

```
B[0..2]   0xA0 0x02 0x04          // fixed
B[3]      pktNumber                // 1..14
B[4]      0x0E                     // fixed
B[5]      layer                    // 1..14
B[6..59]  entry region            // 9 entries × 6 bytes = 54 bytes
B[60]     0xA5                     // tail marker
B[61..62] 0x00 0x00
```

Builder: `Packets.BuildRemapPackets` in [Driver/Packets.cs](../Driver/Packets.cs).

### 10.2 Entry packing (gotcha)

Entries are **packed**, not laid out at fixed 6-byte offsets within the entry region. The sliding write position only advances when an entry is actually written (`keyCmd > 0`); default-no-remap slots are skipped entirely.

```csharp
int r = 6;                          // sliding write position
for (int w = 0; w < 9; w++)
{
    int slot = (pktNumber - 1) * 9 + w;
    if (hidUsageBySlot[slot] == 0) continue;   // default → skip
    packet[r++] = (byte)slot;       // slot index
    packet[r] = 0xFC;               // keyCmd for plain HID
    r += 2;                         // skip one filler byte
    packet[r] = hidUsageBySlot[slot];
    r += 3;                         // advance to next 6-byte boundary
}
```

The legacy `BuildPacketsRemapping` writes at fixed offsets (`6 + charNumber * 6`), which works for **full** profile saves where every slot in a packet has a non-zero `KeyCmd`. For partial remaps (one or two keys), it leaves gaps that the firmware then reads as the wrong slot — that bug is why `BuildRemapPackets` exists as a separate, packed implementation.

### 10.3 Entry types (KeyType byte)

| KeyType | Byte layout (after slot) | Used for |
|---|---|---|
| 0 | `[slot, cmd, 0, code, 0, 0]` | Plain HID keyboard keys. `cmd = 0xFC`, `code = HID usage` (e.g. `A=0x04`, `1=0x1E`) |
| 1, 2 | `[slot, cmd, code, 0, 0, 0]` | Media keys / modifiers; `cmd` typically equals `code` for media |
| 3 | `[slot, cmd, 0, 0, 0, 0]` | Command-only (e.g. LED mode switch, `cmd = 0xFB`) |
| 4 | `[slot, 0xF8, rtpNumber, groupNumber, posInGroup, rdtEnabled]` | RDT / LW pair association |

The Type-0 default key entries in the JS bundle are constructed as `new g(idx, 0xFC, "<label>", "<icon>", <HID code>)` — `keyCmd = 0xFC`, `keyType = 0`. Our `BuildRemapPackets` mirrors that exactly.

## 11. Full profile-save flow (open frontier)

The official driver's `rtpSaveToKeyboard` routine — cached at `C:\Users\skdes\AppData\Local\Temp\dd-index.js` — does considerably more than our current Sync. The sequence (paraphrased from the decompiled JS):

```text
for layer in 1..14:
    send 14-packet remap stream for that layer            // 196 packets total

clear_rtp_alt = [0xAA, 0x00, 0x01, ...]                   // NOTE: 0xAA, NOT
                                                           // 0xFC 0x0A — this is
                                                           // a DIFFERENT clear

for each remapped key:
    sendRTPAuthorityData(rtpNumber)                       // [0x07, rtpN, 0x00, 0x2B, 0x01, ...]
    sendRTPAuthorityDownloadData(rtpNumber, keyCode)      // [0xA8, rtpN, 0x01, 0x01, 0x01, 0x26, 0x01, ...]

send LW / RDT pair packets                                // §8
send common-switch                                        // §5
```

Note the two distinct "clear" packets:

- `[0xAA, 0x00, 0x01, …]` — the existing `CLEAR_UP_RTP_PACKET` constant in [Driver/Packets.cs](../Driver/Packets.cs), used inside the legacy `BuildPacketsRapidTriggerPlus`. Sits between remaps and per-key RTP authority packets.
- `[0xFC, 0x0A, mode, …]` — `BuildClearRtpPacket(mode)`, used as a prelude to LW pair registration (§8.2).

Both appear in the official flow; they are not interchangeable.

### Open question

Our current Sync sends per-key AP/DS/US + a single layer of remap packets + LW pair + common-switch — but **not** the full 14-layer remap, **not** the alternate clear, and **not** the per-key RTP authority/download pair. The firmware ACKs everything we send, but LW pairs and remap commits do not take effect on hardware. Agent A is investigating whether the full save flow above is the missing ingredient, or whether one specific subset (e.g. the per-key authority packets) is what gates pair activation.

## 12. Feature Support Matrix

Per the JS, all software-side toggles are universal across the current lineup; no per-model gating found. Hardware-dependent variation:

| Feature | Support |
|---|---|
| Turbo, Rapid Trigger, RT+, Last Win, RT Match | All models (universal) |
| RGB main (5 modes: Off / Marquee / Neon / Always On / Breath) | All current models |
| LED Strip (separate RGB channels) | All current models |
| Knob / Encoder | G4 variants only — the `G4` prefix marks knob-equipped models (49 references in JS) |
| Macros (recording + playback) | **Not supported.** The "record" UI is for keystroke visualization only, not macro playback |

## 13. Localization

The driver ships 7 languages: `en`, `zh-CN`, `zh-TW`, `ja`, `de`, `fr`, `ko`.

## 14. Firmware Update

The driver detects out-of-date firmware against three thresholds — `v34`, `v63`, `v81` — exposed as constants `Tg.v34`, `Tg.v63`, `Tg.v81`. When out-of-date firmware is detected the driver opens:

```
https://drunkdeer.com/pages/user-manual-select
```

…which links to the official updater. Latest binary observed at the time of extraction:

```
https://cdn.shopify.com/s/files/1/0671/4694/0719/files/DrunkdeerUpdaterV2.3.1.zip
```

### 14.1 `is_m_u_l` flag

Set when firmware ≤ `v34`. Gates certain profile features on older firmware. Likely "is multi-user lock" or "is mass-update lock" — exact meaning not confirmed.

### 14.2 External API

The driver references `https://api.drunkdeer.club` but it does not appear to be used for profile sync — possibly telemetry or version checks. **Out of scope for this app.**

## 15. References

### Source files in this repo

- `Driver/Packets.cs` — packet builders (see §5 for the comment-error fix)
- `Driver/KeyboardSpecs.cs` — type identification (extend per §3, spec read per §7)
- `Driver/KeyboardManager.cs` — VID/PID filter list (extend per §2)
- `Driver/KeyboardLayout.cs` — current hand-built A75 Pro layout, 82 keys; the JS layout has 91 and should replace it

### External

- Plan: `C:\Users\skdes\.claude\plans\keyboard-performance-and-remap.md`
- JS bundle cache: `C:\Users\skdes\AppData\Local\Temp\dd-index.js`
- Source URL: `https://drunkdeer-antler.com/index.CJWCGjvj.js`
