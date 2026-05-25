# Gen-2 OEM Wire Format — Confirmed

**Date confirmed**: 2026-05-25
**Source**: USBPcap capture from affected user (VID `0x19F5` / PID `0xFB5C`,
A75 Pro OEM relabel, firmware 1.7). Capture file:
`C:\Users\skdes\Downloads\newestthing.pcapng` (82 packets, single
actuation-slider drag isolated by `usb.device_address` filter).

**Validated against**: [deerios DrunkDeerSDK protocol
YAML](https://github.com/deerios/DrunkDeerSDK) — `protocol/base_messages.yaml`
and `protocol/structs.yaml`. Local clone at
`C:\Users\skdes\Documents\github\DrunkDeerSDK` (sibling folder, not in this
repo; gitignored defensively).

This doc replaces the speculative pieces in
[docs/keyboard-protocol-gen2.md](./keyboard-protocol-gen2.md). The transport
notes in [docs/gen2-oem-investigation.md](./gen2-oem-investigation.md) still
apply.

## TL;DR

1. Transport from beta.20 is correct: 64-byte HID reports, no Report ID,
   `sendReport(0, raw_64_bytes)`. **Do not touch transport.**
2. The reason the firmware was silent: we send legacy gen-1 packets (e.g.
   `0xB6` family for actuation writes). OEM firmware on `VID 0x19F5` only
   responds to the **`0x55` extended-gateway family**. Same hardware, different
   command alphabet.
3. Per-key actuation write = `0x55 0xA1` (`WriteKeyTriggerChunk`),
   chunked at 56 bytes payload per packet, 18 full chunks + 1 short trailer
   per 1024-byte profile.
4. Identity read = `0x55 0x04` (`ReadBaseBlock`) at address 0, length 32.
   Returns the active profile index + global state.

## Transport (unchanged from beta.20)

- `VendorId == 0x19F5`, current confirmed `ProductId == 0xFB5C`
- HID descriptor does **not** declare a Report ID. Chrome WebHID requires
  `sendReport(0, raw_64_bytes)` — the prepended-ID fallback we tried in
  beta.19 is wrong for this descriptor.
- OUT endpoint observed: `0x04` (interrupt OUT)
- IN endpoint observed: `0x82` (interrupt IN)

## Wire format — `0x55` extended gateway

All `0x55` commands share an envelope. Sub-command byte selects the
operation. Checksum covers the variable-length inner envelope and the
transport layer must compute it before sending.

### Read request (8-byte envelope)

```
offset  size  field
  0      1    0x55                    // request magic
  1      1    sub_cmd                 // 0x04 ReadBaseBlock, 0xA0 ReadKeyTrigger, etc.
  2      1    0x00
  3      1    checksum                // = (length + addr_lo + addr_hi) & 0xFF
  4      1    length                  // bytes to read, 1..56 per response
  5      2    address (u16, LE)
  6-63   ..   zero-pad to 64
```

### Write request (8-byte envelope + up-to-56-byte payload)

```
offset  size  field
  0      1    0x55                    // request magic
  1      1    sub_cmd                 // 0xA1 WriteKeyTriggerChunk, 0x06 WriteFuncBlockChunk, 0x0E WriteActiveProfile, etc.
  2      1    0x00
  3      1    checksum                // = (length + addr_lo + addr_hi + is_last + sum(data)) & 0xFF
  4      1    length                  // bytes of `data` (1..56)
  5      2    address (u16, LE)
  7      1    is_last                 // 0x01 on the final chunk of a stream, else 0x00
  8      N    data                    // N == length, max 56
  ..     ..   zero-pad to 64
```

### Response (shared for all `0x55` ops)

```
offset  size  field
  0      1    0xAA                    // response magic
  1      1    sub_cmd                 // mirrors request
  2      1    0x00
  3      7    reserved
 10     56    data                    // for reads; ignored for write acks
```

The transport layer must skip the 7 reserved bytes when extracting read
data — the payload starts at offset 10, not 3.

## Confirmed checksum match against the capture

Frame 7 OUT (first slider-drag packet):

```
55 a1 00 f5 38 00 00 00  [a0 01 3b 00 0e 00 31 00] × 7  ... padded
```

- `length = 0x38` (56)
- `address = 0x0000`
- `is_last = 0x00`
- `sum(data)` = sum of one 8-byte record × 7 = (0xA0+0x01+0x3B+0x0E+0x31) × 7 = 283 × 7 = 1981
- `(56 + 0 + 0 + 0 + 1981) & 0xFF = 2037 & 0xFF = 0xF5` ✓

## `WriteKeyTriggerChunk` (`0x55 0xA1`) full-profile write sequence

Single actuation-slider change → entire 128-entry × 8-byte table (1024 bytes)
re-sent across **19 packets**:

| Packet # | length | address | is_last | notes |
|---:|---:|---:|---:|---|
| 1   | 56  | 0x0000 | 0x00 | first full chunk |
| 2   | 56  | 0x0038 | 0x00 | |
| ... | ... | ...    | 0x00 | step `+0x38` each |
| 18  | 56  | 0x03B8 | 0x00 | last full chunk |
| 19  | 16  | 0x03F0 | **0x01** | trailer: 2 records of real data + zero pad |

Total: 18 × 56 + 16 = 1024 bytes = 128 × 8.
A75 Pro has 126 keys, so the last 2 entries are firmware-reserved padding.
The is_last=1 flag on packet 19 is what signals the firmware to commit the
profile.

## Per-key record format (8 bytes per key)

From deerios `structs.yaml` `KeyTriggerEntry`. Field offsets are within the
8-byte entry.

```
byte 0
  bits 0-3:  switch_type    (firmware-internal; on encode write 0)
  bits 4-7:  0xA            (always 0xA on encode, ignored on decode)

byte 1
  bits 0-3:  key_mode
  bits 4-7:  priority

bytes 2..3 (LE9, value = stored + 1)
  actuation                 (units: 0.01mm; valid range maps to 0.2..3.8mm)
  byte 3 bit 0 = 9th bit of actuation
  byte 3 bit 1-2 = release_precision
  byte 3 bit 3-4 = press_precision

bytes 4..5 (LE9, value = stored + 1)
  rt_press                  (rapid trigger press-side actuation; units 0.01mm)
  byte 5 bit 0 = 9th bit of rt_press
  byte 5 bit 1-7 = press_deadzone

bytes 6..7 (LE9, value = stored + 1)
  rt_release                (rapid trigger release-side actuation; units 0.01mm)
  byte 7 bit 0 = 9th bit of rt_release
  byte 7 bit 1-7 = release_deadzone
```

### Verified decode of the captured slider drag

Before (steady-state record from frames 7 / 11 / 15 / ...):

```
a0 01 3b 00 0e 00 31 00
  switch_type=0, _hi=0xA, key_mode=1, priority=0
  actuation_raw=0x3B (59) → display = 60 (0.60mm)
  rt_press_raw=0x0E (14) → display = 15 (0.15mm)
  rt_release_raw=0x31 (49) → display = 50 (0.50mm)
  all deadzones / precisions = 0
```

After (frame 19, record #5 only — single byte differs from steady state):

```
a0 01 89 00 0e 00 31 00
  actuation_raw=0x89 (137) → display = 138 (1.38mm)
```

So the user moved one actuation slider from **0.60mm → 1.38mm**. Confirms
the field layout, unit scale, and bias-of-1 encoding.

## Comparison to gen-1 (`0xB6` family)

Gen-1 firmware (`VID 0x352D`, all current models we've seen) uses:

```
0xB6 0x01 0x00 ...  WriteActuationPointStandard   ← what BuildPacketKeyPoint emits
0xB6 0x04 0x00 ...  WriteDownstrokePointStandard
0xB6 0x05 0x00 ...  WriteUpstrokePointStandard
0xB6 ...            ACK echo
```

These three separate writes (one per AP / DS / US dimension) are replaced
on gen-2 by a single bit-packed `KeyTriggerEntry` written via
`WriteKeyTriggerChunk` (`0x55 0xA1`). The data model is denser; the
transport envelope is fatter.

This means **the gen-2 path needs its own packet builders**. We can't
re-use `Packets.BuildPacketKeyPoint` and friends — the opcode, layout,
and chunking are all different.

## Next steps (informing the beta.21 work)

1. Add a `Driver/PacketsGen2.cs` with builders:
   - `BuildGen2ReadBaseBlock(byte length = 32)` — for identity probe
   - `BuildGen2WriteKeyTriggerChunkSequence(KeyTriggerEntry[128], int profileIndex)`
     returning `IEnumerable<byte[]>` (19 packets per profile)
   - Shared envelope helper + checksum.
2. New `Driver/KeyTriggerEntry.cs` mirroring deerios's struct (encode/decode
   the 8-byte record).
3. In `KeyboardManager.cs` gen-2 fallback path:
   - On identity probe, send `BuildGen2ReadBaseBlock`. Response prefix
     `AA 04 00 ...` → parse profile index, mark detection successful.
   - On sync, convert our existing `ProfileItem.Keys[].AP/DS/US` (gen-1
     three-dimensional model) into the gen-2 `KeyTriggerEntry` (actuation
     + rt_press + rt_release packed bits). The mapping is **not 1-to-1**
     — AP maps cleanly to `actuation`, but our DS/US (downstroke/upstroke)
     don't have direct equivalents in the gen-2 model. First-pass safe
     default: leave `rt_press` and `rt_release` at the firmware-default
     values (`15 / 50` per the capture) and only push `actuation`.
4. Verify against ProtocolAnalyzer when feasible (deerios's tool — see
   `[[reference_deerios_protocolanalyzer]]` memory entry).
