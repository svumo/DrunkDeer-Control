# Gen-2 firmware protocol notes

Extracted from `https://drunkdeer.keybord.net.cn/drunk/js/index.QC8Mvgui.js`
(downloaded 2026-05-24, file hash in bundle URL). The bundle is the official
web driver for the newer A75 Pro / A75 Ultra firmware line.

## TL;DR

**The gen-2 protocol is essentially identical to gen-1**, with one critical
wire-format difference: the HID output report is **65 bytes long** instead of
64. The extra byte is the HID Report ID (= `0x04`), which the gen-1 firmware
didn't explicitly require but the gen-2 firmware does.

That means almost all of the gen-1 implementation in
[`Driver/Packets.cs`](../Driver/Packets.cs) is reusable — the packet payload
bytes are the same. The only code change required is at the HID-write layer:
**pad the wire buffer to `MaxOutputReportLength`** instead of hardcoding 64
bytes. Implemented in `Driver/HidDeviceExtensions.cs::BuildOutputReport`.

## Device identification

| Property                       | Gen-1                          | Gen-2                                  |
|--------------------------------|--------------------------------|----------------------------------------|
| Vendor ID                      | `0x352D` (DrunkDeer)           | varies — `0x352D`, `0x19F5`, others    |
| Product ID                     | per model (see `KeyboardModels.cs`) | varies; new firmware sometimes shifts |
| USB Manufacturer string        | `"DrunkDeer"` (or similar)     | `"DrunkDeer"`                          |
| USB Product string             | `"DrunkDeer <model>"`          | `"DrunkDeer A75 Pro"` (observed)       |
| `MaxOutputReportLength`        | **64**                         | **65**                                 |
| `MaxInputReportLength`         | **64**                         | **65**                                 |
| HID Report ID prefix on writes | implicit / `0x04` (accepted)   | **required** `0x04`                    |

The single most reliable indicator that you're on gen-2 firmware is
`MaxOutputReportLength == 65`. The official gen-2 web driver does not
distinguish generations at the protocol layer; it sends the same Report-ID-4
packets to everything, which works on both gen-1 and gen-2 firmware. Our
`BuildOutputReport` follows the same convention — pad to whatever the
device's descriptor reports.

## Identity handshake

Identical to gen-1:

```
host -> kbd: [0x04 | 0xA0 0x02 0x00 ...zeros padded to MaxOutputReportLength]
kbd  -> host: [0x04 | 0xA0 0x02 0x00 ?? B5 B6 B7 fwLo fwHi ...rest...]
```

Where:
- `B5/B6/B7` are the TypeBytes triple identifying the keyboard model (same
  triples as gen-1, e.g. A75 Pro = `(11, 4, 3)`). See `KeyboardModels.cs`.
- `fwLo/fwHi` are the firmware version low/high bytes; displayed as
  `"0." + fwHi + fwLo`.
- Bytes 15, 16, 18, 19 are turbo / RT / RDT / LW toggle states.
- Bytes 30, 31, 32 are RTMatch / AutoMatchMode / LW-Replace (when present).

The byte offsets in `KeyboardSpecs.cs` work unchanged.

## Command opcodes (from gen-2 web driver JS)

All packets are 63 bytes of payload, prefixed with Report ID `0x04` on the
wire (total 64 or 65 bytes depending on the device's reported output size).

| Opcode      | Builder name                  | Purpose                                 |
|-------------|-------------------------------|-----------------------------------------|
| `A0 02 00`  | `sendIdentityData`            | Identity request (no model arg)         |
| `A0 02 04 r 0E n` | `sendRemapKeyData`     | Per-row remap entries                   |
| `A2 n`      | `sendProfileLen`              | Active profile count                    |
| `A7 r 00 2B 01` | `sendRTPAuthorityData`    | Per-row RTP authority preamble          |
| `A8 r 01 01 01 26 01 ... 2 n 01 00 6D 03 n` | `sendRTPAuthorityDownloadData` | RTP download trailer |
| `AA`        | `sendResetKeyboardData`       | Reset all keys to factory               |
| `AA 00 01`  | `sendClearRTPData`            | Clear all RTP/remap entries             |
| `AB n`      | `sendDelProfileData`          | Delete profile `n`                      |
| `B5 00 1E 01 00 00 01 t r 0 lwrdt rtm` | `sendCommonData` | Common switch (turbo/RT/RDT/LW/RTMatch) |
| `B6 01 00 ch ...` | `sendActionPointData`   | Per-key actuation point chunk           |
| `B6 03 00`  | `sendTrackingStopData`        | Disable keystroke tracking              |
| `B6 03 01`  | `sendTrackingStartData`       | Enable keystroke tracking               |
| `B6 04 00 ch ...` | `sendDownStokeData`     | Per-key downstroke threshold chunk      |
| `B6 05 00 ch ...` | `sendUpStrokeData`      | Per-key upstroke threshold chunk        |
| `C1 03 row col ...` | `sendKnobEventData`   | Per-key knob mapping                    |
| `FB 05 idx ...` | `sendChangeProfileIndex`  | Switch active profile                   |
| `FC 05`     | `sendPullLw`                  | Query LW pair table                     |
| `FC 07`     | `sendPullRdt`                 | Query RDT pair table                    |
| `FC 0A n`   | `sendClearRtpData`            | Mode-specific RTP clear                 |
| `FC 0B v`   | `sendLwReplaceData`           | Enable/disable LW Replace               |
| `FD 07 01`  | `sendPullActionPointData`     | Query per-key actuation points          |
| `FD 07 03`  | `sendPullDownStokeData`       | Query per-key downstroke                |
| `FD 07 04`  | `sendPullUpStrokeData`        | Query per-key upstroke                  |
| `FD 0C m`   | `sendRtModeDate`              | Set RT match mode                       |

These align with the opcodes already in `Packets.cs` (B6/FC/FD/etc.) — same
command set as gen-1, just with the report-size change.

## Per-key data chunking

Same as gen-1: chunks of either 59 keys (chunk index 0/1) or 8 keys (chunk
index 2-7), per the `C<2?59:8` / `C<2?C*59:118` logic in
`sendActionPointData`. Total = 2*59 + 6*8 = 166 slots, with only the first
126 corresponding to real key positions (matches the layout arrays in
`KeyboardModels.cs`).

## Actuation-point range

Constants from the gen-2 bundle (lines around the `bg`/`we`/`Xg`/`me` symbols):

- `bg = 3.3` — max action point (mm)
- `we = 0.2` — min action point (mm)
- `Xg = 3.1` — max downstroke / upstroke delta (mm)
- `me = 0.0` — min downstroke / upstroke delta (mm)

These differ from gen-1's `[0.2, 3.8]` AP range. If gen-2 keyboards reject
values outside `[0.2, 3.3]`, the dialect dispatch in
`FirmwareCapabilities.cs` may need a gen-2 entry. Verify on hardware before
shipping a clamp change — the existing dispatch already clamps to the
widest known dialect, so values in `[3.3, 3.8]` may simply be silently
clamped firmware-side without breaking anything.

## What's still unknown (verify on hardware)

- Whether the gen-2 firmware accepts the `0x04` Report ID prefix exactly,
  or whether the kernel must inject it from the descriptor (HidSharp's
  behaviour here may differ per Windows version)
- Whether all the gen-1 `Packets.cs` builders work unchanged on gen-2, or
  if some commands have subtly different byte layouts we haven't spotted
- Whether the 65-byte input report parsing in `KeyboardSpecs.cs` works
  correctly — current code does `response.Skip(1)`, which strips one byte
  from the front. That byte should be the Report ID `0x04` on both gen-1
  and gen-2, so existing parsing should be fine, but worth confirming
- Whether firmware update via this app is feasible for gen-2 keyboards
  (probably not — there's no obvious firmware-write opcode in the JS
  bundle)

## Reference

- Source bundle: `https://drunkdeer.keybord.net.cn/drunk/js/index.QC8Mvgui.js`
  (cached locally at `%TEMP%/gen2-driver/index.js` during reverse-engineering)
- Mock keyboard implementation (good for understanding response format):
  `index.js` class `Ns` (`mockKeyboardIdentify`, `mockKeyboardCommonResponse`)
- Connect flow: `qB` private method (~line 12564 in the unminified split)
- Input report handler: `$B` private method (~line 12609)
- Outgoing report write: `sendReport(reportId=4, data)` (~line 11621)
