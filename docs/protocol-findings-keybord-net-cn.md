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

## P1: RGB / lighting wire format

`sendLedModeData`:
```
B[0] = 0xAE, B[1] = 0x01, B[2] = 0
B[3..7] = 5 mode bytes (mode, brightness?, r?, g?, b?)
```

`sendSetLedData`:
```
B[0] = 0xC2, B[1] = 0x20
B[2..5] = 4 bytes (likely slot, r, g, b)
```

`sendTurboLedModeData`:
```
B[0] = 0xAE, B[1] = 0x01
B[2] = mode param, B[3] = 0
B[4] = byte, B[5] = 0x06, B[6] = byte, B[7] = 0xFF
B[8..] = packed key-list with 0xFF terminator
```

The "Colour" tab visible in the official UI screenshot maps to these
packets. We have **zero RGB support** in our app today.

**Action**: capture wire traces from official tool while setting each
RGB mode (off, always-on, breath, wave, etc.) to nail down what each
parameter means. Then add Packet builders + UI surface. This is
genuinely a feature add, not a bug fix.

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
