# Protocol findings — `drunkdeer.keybord.net.cn` bundle

Extracted 2026-05-22 from `https://drunkdeer.keybord.net.cn/drunk/index.html` →
`./js/index.QC8Mvgui.js` (6.14 MB Vite-built bundle). Raw bundle saved at
[tools/captures/keybord-net-cn/](../tools/captures/keybord-net-cn/) for
re-derivation.

This is a **different deployment** from the `drunkdeer-antler.com` source
that `docs/keyboard-protocol.md` was originally extracted from. The two
bundles share the same protocol vocabulary but the newer one has
materially richer wire formats, additional features (RGB, knob,
firmware-side multi-profile), and **changes to behaviour we previously
believed was settled** (RDT pair packets, per-key sensitivity wire
format).

This document lists everything new or different from our current
`Driver/Packets.cs` and `docs/keyboard-protocol.md`. Each finding is
flagged P0/P1/P2 by suspected priority.

---

## P0: phantom-RDT bug suspect — `sendCreateRdtData` IS used

Our `Driver/Packets.cs` comment says:
> Release-Dual-Trigger does NOT have its own pair-table packet on the
> wire. The JS bundle defines `sendCreateRdtData` ([0xFC, 0x03, count, …])
> but the official driver does NOT actually emit it during RDT save —
> verified by WebHID intercept of drunkdeer.com (2026-05-20).

**That's not true on `keybord.net.cn`.** The new bundle calls
`sendCreateRdtData(Q)` unconditionally when entering RDT mode:

```js
let C = NA().sendClearRtpData(e);  // fc 0a (mode)
// ... write C, await ack ...
if (A === "lw") {
  let E = NA().sendCreateLwData(Q);  // fc 01 (LW pairs)
  // ... write E, await ack ...
} else {
  let E = NA().sendCreateRdtData(Q); // fc 03 (RDT pairs)  ← we never send this
  // ... write E, await ack ...
}
```

**Wire format** of `sendCreateRdtData(B)`:

```
B[0] = 0xFC
B[1] = 0x03
B[2] = pair count
for each pair (8 bytes per entry, starting at B[3]):
  +0  = main_slot   (press key, byte)
  +1  = trigger_slot (release key, byte)
  +2  = active_lo   ((x2Active - main.upstroke) * 200, clamped 10..50)
  +3  = active_hi
  +4  = reset_lo    ((x2Reset - main.upstroke) * 200, clamped 300..500)
  +5  = reset_hi
  +6  = 0
  +7  = 0
```

`x2Active` and `x2Reset` are per-pair thresholds. Defaults observed:
`active ≈ 0.05-0.25 mm`, `reset ≈ 1.5-2.5 mm`. The thresholds are
**relative to the main key's upstroke point** — `(x2Active - main.upstroke)`.

**Why this likely matters for the phantom-RDT bug:** without `fc 03`,
the firmware never receives proper per-pair RDT registration. Our
RDT pairs activate via Type-4 remap entries + RtpAuthority/Download
packets, which mostly works — but on profile-switch the firmware's
internal pair-state is half-configured, and stale entries can bleed
through. The phantom-fires-on-release symptom user observed
2026-05-22 lines up exactly with this.

**Action**: implement `BuildCreateRdtPacket(pairs, thresholds)` in
`Packets.cs`, wire it into `BuildFullProfilePackets` for RDT-mode
syncs, test on A75 Pro 0x0017. If it fixes the phantom, this is
the answer. If not, deeper investigation needed.

**Caveat**: the threshold values (`x2Active`, `x2Reset`) come from a
per-pair UI in the official tool that we don't currently expose.
We'd need either reasonable defaults (e.g. active=0.1mm,
reset=2.0mm) or a per-pair editor in our UI.

---

## P0: per-model dialect dispatch (we likely get it wrong for parts of the lineup)

The bundle dispatches per-key wire format based on **type bytes** and
**firmware version**:

```js
// type bytes Q.A.e = identity-response bytes 4/5/6
if      (Q===0x0B && A===0x01 && e===0x01) precision=1, oldOpenHighPrecision=false  // G65 (one variant)
else if (Q===0x0B && A===0x04 && e===0x01) precision=1, oldOpenHighPrecision=false  // A75 ANSI
else if (Q===0x0B && A===0x04 && e===0x04) precision=2, oldOpenHighPrecision=false  // A75 Ultra
                                           keyboard_type = 756
// per-model firmware-version cutoffs that PROMOTE old → new high-precision:
else if (model === A75base    && fw >= 0x23) precision=2, oldOpenHighPrecision=true
else if (model === A75Pro     && fw >= 0x11) precision=2, oldOpenHighPrecision=true
else if (model === A75iso     && fw >= 0x19) precision=2, oldOpenHighPrecision=true
```

`fw` is `a` in the JS — decoded from the identity response (probably
byte index 25 based on `E.getUint8(25)` calls in the same block,
though `a` itself is set elsewhere).

### Three cases unpack to:

| precision | oldOpenHighPrecision | Packet ID | Bytes/key | Scale | Keys/pkt | Total pkts |
|---:|:---:|:---:|:---:|:---:|:---:|:---:|
| 1 | false | 0xB6 | 1 | mm × 10 | 59 + 8 | 3 |
| 2 | **true** | 0xB6 | 1 | mm × 100 | 59 + 8 | 3 |
| 2 | false | **0xFD** | **2** | mm × 200 | 30 × 4 + 6 | 5 |

### What our `Driver/Packets.cs` actually implements

Single path: case **(2, true)** — `0xB6` packets, 1 byte/key, mm × 100.

**Where this is correct** ✅:
- A75 Pro on firmware ≥ 0x11 (covers the verified 0x17 and the dropped
  0x09 path). Matches user's A75 Pro 0x0017.
- A75 base on firmware ≥ 0x23.
- A75 ISO on firmware ≥ 0x19.

**Where this is BROKEN** ❌:
- **G65 / A75 ANSI on legacy firmware (and likely G75 / G60)** → they
  want `precision=1` (mm × 10). We send mm × 100. The firmware
  interprets our 0.5mm AP as 5.0mm — every key effectively won't
  trigger. CLAUDE.md says G65 was "tested on v0.48" so either that
  test was on a different code path or the bug was masked.
- **A75 Ultra (TypeCode 756) and A75 Master (TypeCode 757)** → they
  want `precision=2, oldOpenHighPrecision=false` (0xFD packets, 2
  bytes/key, mm × 200). We send 0xB6 / 1 byte / mm × 100. **The
  firmware silently rejects or mis-interprets the packets.** This is
  likely why we have zero verified hardware reports on A75 Ultra /
  Master despite users on those models.

**Action**: per-firmware sensitivity packet dispatch in `Packets.cs`.
The `FirmwareCapabilities` record needs a `Precision` enum field
(Legacy / OldHighPrec / NewHighPrec), set during `KeyboardSpecs`
parsing. `BuildPacketKeyPoint` becomes a dispatcher. Real
cross-keyboard correctness fix — but should land alongside read-back
verification (Pull* variants) so we can confirm the bytes commit.

---

## P0: two protocol dialects for per-key AP/DS/US

The bundle defines TWO complete sets of `sendActionPointData` /
`sendDownStokeData` / `sendUpStrokeData` builders, dispatched by:

```js
A.precision === 2 && !A.oldOpenHighPrecision
  ? newDialect  // 0xFD packets, 2 bytes/key, mm × 200
  : oldDialect  // 0xB6 packets, 1 byte/key, mm × 100 or × 10
```

### Old dialect (what `Driver/Packets.cs` currently implements)

```
sendActionPointData(B, C):
  E[0] = 0xB6
  E[1] = 0x01     // 0x04 for DS, 0x05 for US
  E[2] = 0
  E[3] = C        // packet number 0/1/2
  // 59 keys per packet × 2 + 8 in packet 2 = 126 keys
  // 1 byte per key value: mm × 100 if oldOpenHighPrecision, else mm × 10
```

This matches our current code (mm × 100 after the AP wire-scale fix in
commit `23646cf`). The `× 10` legacy branch is what we ripped out as
part of the 0.09 drop.

### New dialect (what we DON'T have)

```
sendActionPointData(B, C):
  E[0] = 0xFD
  E[1] = 0x01     // 0x04 for DS, 0x05 for US
  E[2] = C        // packet number 0..4 (5 packets total)
  // 30 keys per packet × 4 + 6 in packet 4 = 126 keys
  // 2 bytes per key value (lo + hi), mm × 200 — half-millimetre precision
```

`mm × 200` means a 16-bit value covering 0..512 for 0..2.56mm, with
0.005mm steps. That's 40× finer than the old format's 0.01mm step.

**Which firmwares prefer which dialect** is gated by
`precision === 2 && !oldOpenHighPrecision`. We don't yet know how
`precision` is set — likely from a spec response byte we currently
ignore. Need to capture identity-packet bytes from a newer firmware
(A75 Ultra 0x55, A75 Master 0x55, A75 ANSI 0x27) to determine the
mapping.

**Action**: add high-precision builders alongside the existing ones,
gate behind a capability check that reads the precision byte from
spec response. **Don't switch unilaterally** — A75 Pro 0x0017 has been
verified working on the old dialect, no need to migrate that path.

---

## P1: RGB / lighting — DEEP DIVE

### Full mode catalog (color1..color26)

Names from the English locale block (`color1:"..."` etc.) in the bundle.
Each mode has a 1-based code that goes into the wire packet's B[4]
position.

| Code | Mode | Notes |
|---:|---|---|
| 1  | Lighting turn off | All LEDs off |
| 2  | Rotate Marquee | Marquee rotates around perimeter |
| 3  | Wave Spectrum | Diagonal colour wave |
| 4  | Surf to the right | Horizontal wave L→R |
| 5  | Breath | Pulse single colour |
| 6  | Center Surfing | Wave from centre outward |
| 7  | Spectrum | Static rainbow gradient |
| 8  | Ripple | Ripple from key press |
| 9  | Always light | Static colour (no animation) |
| 10 | Light by press | LEDs off until key pressed |
| 11 | Serpentine to the center | Snake wave converging |
| 12 | Colorful fountain | Random colour fountain |
| 13 | Laser Key | Laser line from key press |
| 14 | Glowing Fish | Fish swims across |
| 15 | Surfing Cross | Cross-pattern wave |
| 16 | Heart | Heart shape pulsing |
| 17 | Traffic | Traffic-light cycle |
| 18 | Gluttonous Snake | Snake game pattern |
| 19 | Raindrops | Random fading drops |
| 20 | Turbo mode light | Highlights Turbo-bound keys |
| 21 | **Custom light** | Per-key static colours (see below) |
| 22 | Stars | Twinkle pattern |
| 23 | Surfing down | Vertical wave top→bottom |
| 24 | Repeat Surfing | Surf back-and-forth |
| 25 | Random Fountain | Random colour bursts |
| 26 | Dance of Demons | Frantic colour chaos |

### `sendLedModeData` — set the active mode

```
B[0] = 0xAE
B[1] = 0x01
B[2] = 0x00
B[3] = (mode index in colour_array, usually 0)
B[4] = mode_code (1..26 from the table above)
B[5] = ??? (one of brightness/speed/colour_select)
B[6] = ??? (another of brightness/speed/colour_select)
B[7] = 0
```

Definition (from the JS bundle):
```js
sendLedModeData:(B,C,E,a,r)=>{
  const t=new Uint8Array(63);
  return t[0]=174, t[1]=1, t[2]=0,
         t[3]=B, t[4]=C, t[5]=E, t[6]=a, t[7]=r, t
}
```

Call site:
```js
event_set_led_mode(0, c, C, B, 0)
// where c = mode_code (post-precision dispatch), C and B come from
// the colour-panel UI (brightness slider + speed slider, but exact
// mapping needs a WebHID capture to confirm)
```

`apiSaveColourSolution(D, p.value, Number(s.value), ...)` is the
entry point — `D` is the current mode index, `p.value` is one slider
value (suspected brightness), `s.value` is the other (suspected
speed). The 4th and 5th apiSaveColourSolution params (the picked
colour for monochrome modes like Breath / Always light) propagate
to wire bytes 5/6/7 in some order — needs WebHID capture for the
exact assignment.

### `sendTurboLedModeData` — multi-packet stream

Used for modes that need per-key colour data (currently observed for
Turbo mode = code 20). Each packet carries up to 13 keys × 4 bytes
(slot+r+g+b). Multiple packets chained until a `0xFF` terminator.

```
B[0] = 0xAE
B[1] = 0x01
B[2] = mode (= 20 for Turbo)
B[3] = 0x00
B[4] = (mode-specific param)
B[5] = 0x06
B[6] = (mode-specific param)
B[7] = 0xFF
B[8..] = packed [slot0, r0, g0, b0, slot1, r1, g1, b1, ...]
         terminated with 0xFF byte after last entry
```

JS source:
```js
sendTurboLedModeData:(B,C,E,a)=>{
  const r=new Uint8Array(63);
  r[0]=174, r[1]=1, r[2]=B, r[3]=0,
  r[4]=C, r[5]=6, r[6]=a, r[7]=255,
  E.push(0,0,0);  // padding
  let t=0;
  for(let I=0;I<E.length;I++)
    t++,
    I%4===0 ? r[8+I]=E[I] : r[8+I]=+("0x"+E[I]);
  return t===0 ? r[8]=+"0xff" : r[t+5]=+"0xff", r
}
```

### `sendSetLedData` — X60-only

```
B[0] = 0xC2
B[1] = 0x20
B[2..5] = 4 bytes
```

Only called when `keyboard_type === 603` (the X60 model — TypeCode
603). Almost certainly LED strip control on the X60's underlight.
Skip for the main lineup.

### `buildPkt_custom_led_mode_select` — Custom Light (mode 21) packing

```js
// Builds the per-key colour stream for mode 21 (Custom light).
// Q is a 2D array of {color: "#rrggbb", value: slotIdx} cells.
for (let c=0; c<Q.length; c++)
  for (let m=0; m<Q[c].length; m++) {
    let a = Q[c][m].color ? Q[c][m].color.slice(1,3) : 0  // r hex
    let r = Q[c][m].color ? Q[c][m].color.slice(3,5) : 0  // g hex
    let t = Q[c][m].color ? Q[c][m].color.slice(5,7) : 0  // b hex
    I.push(128 + Q[c][m].value, a, r, t)  // 0x80|slot, r, g, b
  }
// I is then chunked and sent via sendTurboLedModeData
```

So Custom Light wire encoding per-key entry is **4 bytes**:
`[0x80 | slotIndex, R, G, B]`. The 0x80 OR sets a high bit on slot —
firmware uses this to distinguish "user-coloured" slots from
inactive ones in the same stream.

### Capability gating

```js
A.version_feature_switch.color
```

The `color` feature flag on `version_feature_switch` gates whether the
RGB UI tabs are shown at all. Some keyboards report `color = false`
and the official tool hides the entire Colour panel — likely the
G65/G60 family which have no per-key RGB.

### Localised UI strings (English)

```
color_title:      "Lighting settings"
color_brightness: "Lighting brightness"
color_speed:      "Lighting speed"
color_bg:         "Lighting colour"
ledstrip_title:   "LED Strip Settings"
ledstrip_brightness, ledstrip_speed, ledstrip_color: (X60-only)
```

### Localised UI strings (Chinese, for the i18n roadmap)

```
color_title:      "灯效设置"
color_brightness: "灯效亮度"
color_speed:      "灯效速度"
color_bg:         "颜色"
```

### What we can build right now without hardware

Even without a WebHID capture confirming the exact param mapping for
bytes 5/6 of `sendLedModeData`, we can:

1. Land the mode-code enum and the wire packet builder
2. Ship a minimal Colour tab UI: a mode picker (dropdown of the 26
   modes), a single colour swatch, brightness + speed sliders
3. Map "set mode" → `sendLedModeData(0, mode_code, brightness, speed, 0)`
   as a first guess
4. Persist last-used mode + colour + sliders in `Settings`
5. Gate on `version_feature_switch.color` analogue (we don't have
   that flag yet but A75 Pro definitely supports RGB so we can
   unconditionally show the UI on supported models)

The first-pass param mapping (5=brightness, 6=speed) might be wrong —
a hardware capture from the official tool will lock it down. Worst
case is one swapped byte and an easy follow-up commit. Best case is
it works.

---

## P1: knob / dial wire format

`sendKnobEventData(B, C, E)`:
```
a[0] = 0xC1, a[1] = 0x03
a[2] = C, a[3] = E, a[4] = 0
// then by keyType of the remapItem:
// type 0: a[5] = keyCmd, a[7] = keyCode
// type 1: a[5] = keyCmd, a[6] = keyCode
// type 2: a[5] = keyCmd, a[6] = keyCode
// type 3: a[5] = keyCmd
```

Models with a physical knob (A75 Ultra, A75 Master) can remap the
knob's CW/CCW/click actions via this packet. We don't expose knob
remap today.

**Action**: roadmap item, gated on having a knob-equipped keyboard
to test against.

---

## P1: firmware-side multi-profile management

The keyboard apparently stores multiple profiles internally and can
switch between them via wire:

| Function | Wire | Purpose |
|---|---|---|
| `sendProfileLen(B)` | `[0xA2, B, …]` | set/query profile count |
| `sendDelProfileData(B)` | `[0xAB, B, …]` | delete firmware-side profile slot B |
| `sendChangeProfileIndex()` | `[0xFB, 0x05, idx, id0, id1, …]` | switch active firmware-side profile |

Today our app treats profiles as host-side JSON files that get fully
pushed to the keyboard on switch. A firmware-side multi-profile model
would let us:
- Switch profiles instantly (no full re-sync)
- Have profiles persist across host disconnect (kbd remembers last
  active even when disconnected from PC)
- Match the official tool's behaviour exactly

`profileIndex` appears 17+ times in the bundle — heavily used.

**Action**: roadmap item, significant scope. Would change the entire
profile sync architecture. Worth scoping but not urgent — host-side
push works fine for our use case.

---

## P1: read-back capability ("Pull" variants)

| Function | Wire |
|---|---|
| `sendPullActionPointData()` | `[0xFD, 0x07, 0x01, …]` |
| `sendPullDownStokeData()` | `[0xFD, 0x07, 0x03, …]` |
| `sendPullUpStrokeData()` | `[0xFD, 0x07, 0x04, …]` |

Request firmware to send back current per-key AP/DS/US values. The
response packets presumably mirror the SET packets in format (0xFD
01/04/05 with the same packet-number layout, 2-byte values).

We currently can't read per-key state from the firmware — we treat
the host-side profile JSON as authoritative. Read-back would let us:
- Detect "user edited via official tool then opened our app" (state
  drift between firmware and our cached JSON)
- Import the firmware's actual state as a new profile
- Roundtrip-verify a sync actually committed (read back, compare,
  warn on mismatch)

**Action**: roadmap item, useful diagnostic but not a feature gap.

---

## P1: factory reset wire packet

`sendResetKeyboardData`:
```
B[0] = 0xAA  // single byte, rest zero
```

Simpler than I expected — just `aa 00 …`. Our app's "Reset all keys"
button currently fakes this by sending AP=2.0, DS=0, US=0 across all
126 slots (9 packets × ack). A single `aa 00` is presumably faster
and resets everything (remaps, pairs, RGB, etc.) at once.

**Action**: small wire-level cleanup. Verify on hardware that `aa 00`
actually resets ALL state (not just per-key sensitivity), then
replace our reset implementation with it. Low risk, low effort.

---

## P2: protocol functions confirmed unchanged

These match our existing implementations byte-for-byte:

| Function | Wire | Our impl |
|---|---|---|
| `sendCommonData` | `[0xB5, 0, 0x1E, 1, 0, 0, 1, turbo, rt, 0, lw\|rdt, rtmatch]` | `BuildCommonSwitchPacket` |
| `sendClearRTPData` | `[0xAA, 0, 1, …]` | `BuildClearRtpUpperPacket` |
| `sendClearRtpData(mode=2)` | `[0xFC, 0x0A, mode, …]` | `BuildClearRtpPacket` |
| `sendRTPAuthorityData(B)` | `[0xA7, B, 0, 0x2B, 1, …]` | `BuildPacketRTPAuthority` |
| `sendCreateLwData(B)` | `[0xFC, 0x01, 0, count, m0, t0, 0, 0, …]` | `BuildCreateLwPairsPacket` |
| `sendLwReplaceData(B)` | `[0xFC, 0x0B, B, …]` | `BuildLastWinReplacePacket` |
| `sendIdentityData` | `[0xA0, 0x02, …]` | `IDENTITY_PACKET` |

---

## P0: complete identity-packet byte map (NEW from response parser)

The firmware's response to `sendIdentityData` (request `[0xA0, 0x02]`)
comes back as a 0xA0 packet whose full byte layout was extracted from
the bundle's `case 160:` handler. **Significantly richer than what
`Driver/KeyboardSpecs.cs` currently parses** — RGB state, current
profile index, RTMatch state, and AutoMatchMode are all here.

```
Byte  Field                          Notes
----  -----------------------------  ----------------------------------
[0]   0xA0                           Packet ID (matched by case 160)
[1]   0x02                           Sub-id (C)
[2]   mode                           Must be 0 for a spec response
[3]   (reserved)                     Read but unused
[4]   typeBytes[0]                   Model identifier triple
[5]   typeBytes[1]                   Combined → TypeCode via lookup
[6]   typeBytes[2]
[7]   firmware low byte              Together: "0.<hi><lo>" string
[8]   firmware high byte             e.g. fw 0x0017 = "0.0023" formatted
[15]  turbovalue                     0/1 — Turbo enabled
[16]  rtvalue                        0/1 — Rapid Trigger enabled
[18]  rtdvalue                       0/1 — RDT enabled
[19]  lwvalue                        0/1 — LW enabled
[20]  profileIndex *                 Active firmware-side profile slot
[22]  colormodel *                   Current RGB mode code (1..26)
[23]  colorspeed *                   RGB animation speed
[24]  brightness *                   RGB brightness
[30]  rtMatchValue                   0/1 — RT match enabled
[31]  autoMatchModeIndex             0..N — AutoMatch mode
[32]  lw_replace                     0/1 — LW Replace enabled
[25]  (some value used as `a`)       Source of firmware-version cutoff
                                     in the precision dispatch table
```

`*` = only meaningful when `precision === 2 && !oldOpenHighPrecision`
(i.e. NewHighPrec firmware — A75 Ultra / A75 Master). On older
dialects these bytes might be reserved or carry different meaning.

**What our `KeyboardSpecs` currently parses**: [4..6, 7, 8, 15, 16,
18, 19, 30, 31, 32]. **What we're missing**: [20] profileIndex,
[22] colormodel, [23] colorspeed, [24] brightness.

**Action**: extend `KeyboardSpecs` to read bytes 20/22/23/24 when
present. Surfaces the firmware's current RGB state in our UI without
needing to track host-side state separately.

---

## P0: full firmware-response inbound packet handler map

The bundle's main `received_report_handle` switch (extracted from the
`case 160:` and surrounding cases) is the source of truth for what
the firmware sends and how it's interpreted. Map below.

| ID  | Sub | Direction | Meaning |
|-----|-----|-----------|---------|
| 0xA0 | 0x02 | in  | Spec response (see byte map above) |
| 0xA2 | 0x?? | in  | Profile-length echo (after sendProfileLen) |
| 0xA7 | -    | in  | RTPAuthority ACK (silent) |
| 0xA8 | -    | in  | RTPAuthorityDownload ACK (silent) |
| 0xAA | -    | in  | Reset / ClearRTPUpper ACK (silent) |
| 0xAE | 0x01 | in  | LED mode response (Z C handler) |
| 0xB5 | -    | in  | CommonSwitch echo (re-derives turbo/RT/LW/RDT from B[7,8,10]) |
| 0xB6 | -    | in  | AP/DS/US ACK (low-prec; silent except link state) |
| 0xB7 | -    | in  | **Low-prec keystroke depth stream** (3 chunks: 59+59+8 keys, 1 byte/key at mm × 10 OR × 100 depending on oldOpenHighPrecision) |
| 0xC1 | -    | in  | Knob event ACK (silent) |
| 0xFB | -    | in  | Profile-index change ACK (silent) |
| 0xFC | 0x06 | in  | LW pair table read-back (response to sendPullLw `fc 05`) |
| 0xFC | 0x08 | in  | RDT pair table read-back (response to sendPullRdt `fc 07`) |
| 0xFC | 0x0A | in  | ClearRtp ACK (silent) |
| 0xFD | 0x01..0x05 | in | AP/DS/US writes / tracking enable ACKs (silent) |
| 0xFD | 0x06 | in  | **High-prec keystroke depth stream** (5 chunks: 30+30+30+30+6 keys, 2 bytes/key at mm × 200) |
| 0xFD | 0x08 | in  | AP read-back response (5 chunks × 30 keys, mm × 200) |
| 0xFD | 0x0A | in  | Downstroke read-back response |
| 0xFD | 0x0B | in  | Upstroke read-back response |

**Request → response subcommand pattern**: pull-back responses are
`request_sub + 1` (e.g. AP request `fd 07 01` → response `fd 08`).

### High-precision keystroke tracking (`0xFD 0x06`) — full format

```
B[0] = 0xFD
B[1] = 0x06
B[2] = chunk index (0..4)
B[3..] = 30 keys × 2 bytes (lo, hi) at mm × 200 raw
         chunk 4 only carries 6 keys
```

Deadzone: raw < 40 → treated as 0 (40 raw at mm × 200 = 0.20 mm).
Cycle complete signal fires after chunk 4. Our existing
`HidStreamListener.ParseHighPrecision` mostly matches this; worth a
re-read against the JS to confirm chunk count + dead-zone threshold.

### Low-precision keystroke tracking (`0xB7`) — confirmed

```
B[0] = 0xB7
B[1] = 0x00 (always 0)
B[2] = chunk index (0, 1, 2)
B[3] = unused?
B[4..] = 59 keys × 1 byte
         chunk 2 only carries 8 keys (last 8 of 126)
```

Scale depends on `oldOpenHighPrecision`:
- true (OldHighPrec) → byte / 200 * 100 = percent of 2 mm max
- false (Legacy) → byte / (bg * 10) * 100 where bg ≈ 3.8 (full travel)

---

## P0: NEW pull-back packets (sendPullLw, sendPullRdt)

Adding to the Pull* inventory documented above:

| Function | Wire | Response (inbound) |
|---|---|---|
| `sendPullLw()` | `[0xFC, 0x05]` | `[0xFC, 0x06, ...]` LW pair table |
| `sendPullRdt()` | `[0xFC, 0x07]` | `[0xFC, 0x08, ...]` RDT pair table |

Round-trip lets the host learn the firmware's current LW + RDT pair
tables — useful for the "user edited via official tool" detection
roadmap item, AND for verifying our `sendCreateLwData` / new
`sendCreateRdtData` writes actually committed.

---

## P0: full RGB packing for Custom Light (mode 21)

The Custom Light mode (`color21`) uses `sendTurboLedModeData` with a
per-key colour stream. Wire encoding per-key:

```
4 bytes per coloured key:
  [0x80 | slotIndex, R, G, B]
```

Build flow (from `buildPkt_custom_led_mode_select`):
```js
for each row, for each cell:
  const r = color ? parseInt(color.slice(1,3), 16) : 0   // RR
  const g = color ? parseInt(color.slice(3,5), 16) : 0   // GG
  const b = color ? parseInt(color.slice(5,7), 16) : 0   // BB
  bytes.push(0x80 | slot, r, g, b)
// chunk and send via sendTurboLedModeData
```

The `0x80` high bit marks "user-coloured" — firmware uses it to
distinguish styled slots from defaults in the same packet stream.

---

## P0: full apiXxx surface (the official tool's public API)

Extracted as the complete list of `api*(` patterns in the bundle.
Useful as a "what features does the official tool have" reference.

**Profile management**:
- `apiProfileCreateNew`, `apiProfileDelete`, `apiProfileDuplicate`,
  `apiProfileReset`
- `apiProfileChangeName`, `apiProfileChangeRTMatch`,
  `apiProfileChangeTurboAndRP`, `apiProfileChangeBySlider`
- `apiProfileRtp` (RTP per-profile settings)
- `apiImportProfile`, `apiLoadProfiles`, `apiGetAllProfiles`,
  `apiGetAllProfileNames`
- `apiDelProfile` (firmware-side delete)

**Remap**:
- `apiRemapNew`, `apiRemapCreateMap`, `apiRemapDelete`,
  `apiRemapDeleteMap`, `apiRemapDuplicate`
- `apiRemapChangeName`
- `apiRemapImport`, `apiRemapLoadProfile`, `apiRemapResetAllKeys`,
  `apiRemapSaveToKeyboard`
- `apiRemapGetDefaultKey`, `apiRemapGetKeyDesc`, `apiRemapGetKeys`,
  `apiRemapGetProfile`, `apiRemapGetProfileNames`,
  `apiRemapGetProfiles`

**Colour / RGB**:
- `apiSaveColourSolution`, `apiSaveCustomColourSolution`
- `apiGetAllColours`, `apiGetColours`, `apiLoadColours`
- `apiGetAllLedStrips`, `apiGetLedStripColors`, `apiLoadLedStrips`
  (X60-only LED strip)

**RTMatch / knob**:
- `apiRTMatch`
- `apiSetKnobData` (wraps `sendKnobEventData(Q, A, e)` directly)

---

## Next steps (ordered by suspected value)

1. **Implement `BuildCreateRdtPacket` + wire into `BuildFullProfilePackets`**
   for RDT-mode syncs. Test on A75 Pro 0x0017. **If this fixes the
   phantom-RDT bug observed 2026-05-22, that's a confirmed P0 fix.**
2. **Capture identity-packet bytes from a newer firmware** (A75 Ultra
   0x55, A75 Master 0x55) to determine the `precision` byte location.
   Without that we can't dispatch correctly.
3. **Wire-trace the official tool** with a sniffer for RGB / knob / 
   firmware-side profile commands to confirm parameter meanings before
   building UI for them.
4. **Add `aa 00` reset packet** as a small refactor of the existing
   "Reset all keys" button. Low risk.
5. **Read-back capability** (Pull* variants) as a diagnostic tool —
   maybe a `--print-firmware-state` CLI flag that dumps what the
   keyboard actually has stored.

---

## Bundle re-derivation

To re-extract from the same source:

```
mkdir -p tools/captures/keybord-net-cn
cd tools/captures/keybord-net-cn
curl -sLO https://drunkdeer.keybord.net.cn/drunk/js/index.QC8Mvgui.js
curl -sLO https://drunkdeer.keybord.net.cn/drunk/js/vendor.CQzYxF6f.js
# Function inventory:
grep -oE "send[A-Z][a-zA-Z0-9]+Data" index.QC8Mvgui.js | sort -u
# Specific function body (minified, lookup by name):
grep -oE "sendActionPointData:.{0,500}" index.QC8Mvgui.js | head -1
```

The Vite chunk-hash filenames (`QC8Mvgui`, `CQzYxF6f`) will rotate when
the site rebuilds. If the URLs 404, fetch `index.html` first and parse
the new chunk paths out of the `<script type="module">` tag.
