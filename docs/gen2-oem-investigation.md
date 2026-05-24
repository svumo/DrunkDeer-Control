# Gen-2 A75 Pro OEM-Variant Investigation (2026-05-23 → 2026-05-24)

The complete record of an extended user-driven investigation into why a
specific A75 Pro keyboard reporting `VID=0x19F5 / PID=0xFB5C` would not
be detected by DrunkDeer Control, and what we learned along the way.

This document complements:
- [docs/keyboard-protocol-gen2.md](keyboard-protocol-gen2.md) — extracted gen-2 wire protocol from the official web driver
- [Driver/Gen2KeyboardChannel.cs](../Driver/Gen2KeyboardChannel.cs) — the production fix shipped in v2.4.1-beta.8
- [Driver/KeyboardManager.cs](../Driver/KeyboardManager.cs) — diagnostic probe sweep (Strategies A through J + production TryGen2Detection)

## TL;DR

A user (Discord) reported that DrunkDeer Control v2.4.0 didn't detect his A75 Pro. After 8 beta builds and 7 user log rounds, we determined:

1. His keyboard reports **VID `0x19F5` / PID `0xFB5C`** with USB strings `Mfg='DrunkDeer'`, `Product='DrunkDeer A75 Pro'`. **Not in the official drunkdeer.keybord.net.cn web driver's filter list either** — likely an OEM/whitelabel variant.
2. The keyboard works with Chrome's WebHID on the gen-2 site (confirmed by the user), so the HID protocol IS reachable on this hardware.
3. Standard HidSharp / `device.Open()` succeeds on the vendor interface (mi_01) and writes are *accepted by the firmware* (`HidD_SetOutputReport` returns OK), but reads via `HidStream.Read` and `HidD_GetInputReport` (with full access) return nothing.
4. The gen-2 firmware's response interface is a HID Keyboard or Mouse top-level collection that **Windows kernel-blocks user-mode applications from reading** — `device.Open()` on those collections throws `DeviceIOException: Unable to open HID class device`. **Admin doesn't lift this** (confirmed via explicit "Run as administrator" test).
5. Microsoft's documented escape hatch: open the handle with `dwDesiredAccess = 0` (no GENERIC_READ/WRITE). The handle is invalid for ReadFile but valid for the `HidD_*` family of control-transfer APIs, which Windows allows on blocked collections.
6. **v2.4.1-beta.8** implements this escape hatch as production code — Chrome's WebHID-equivalent path. Confidence at time of writing: ~50%, awaiting user validation.

## Hardware fingerprint

```
VID: 0x19F5
PID: 0xFB5C
Manufacturer string: "DrunkDeer"
Product string:      "DrunkDeer A75 Pro"
Serial:              "2024-04-25"
Release:             1.7
HID collections:     7
```

**Per-collection breakdown** (from beta.7 log):

| Path tail                    | InLen | OutLen | Descriptor / role                                                                                               |
|------------------------------|-------|--------|-----------------------------------------------------------------------------------------------------------------|
| `mi_01`                      | 65    | 65     | **Vendor interface.** Usage=Undefined, both Input and Output declared `Constant` (= reserved). Sole writable path. |
| `mi_00\kbd`                  | 9     | 2      | Standard keyboard. Open() fails with DeviceIOException (kernel-blocked).                                        |
| `mi_02&col01\kbd`            | 16    | 0      | Secondary keyboard. Open() fails (kernel-blocked). **Almost certainly the response channel.**                   |
| `mi_02&col02`                | 6     | 0      | Mouse (descriptor Usage=Mouse). Open() fails (kernel-blocked).                                                  |
| `mi_02&col03`                | 3     | 0      | Consumer/system controls. Opens fine. Too small for 63-byte response.                                           |
| `mi_02&col04`                | 3     | 0      | Consumer/system controls. Opens fine. Too small.                                                                |
| `mi_02&col05`                | 2     | 0      | System controls. Opens fine. Too small.                                                                         |

Only `mi_02&col01` has a 16-byte input — still smaller than a 63-byte spec response, but it could carry a chunked or partial response, or the keyboard might respond with a smaller acknowledgement format than gen-1.

## Beta lineage

| Build               | What it added                                                                         | What we learned                                                                                                                                                                                                                                                                                          |
|---------------------|---------------------------------------------------------------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| v2.4.1-beta.1       | Widened PID allowlist for VID 0x352D                                                  | Irrelevant — user is on VID 0x19F5, never matched.                                                                                                                                                                                                                                                       |
| v2.4.1-beta.2       | String-match fallback: any HID device with "drunkdeer" in mfr/product string + full HID inventory dump on probe failure | **Identified** the device — VID 0x19F5 / PID 0xFB5C with `Product='DrunkDeer A75 Pro'`. String-match succeeded.                                                                                                                                                                                          |
| v2.4.1-beta.3       | Padded HID writes to MaxOutputReportLength (65 bytes vs hardcoded 64)                 | Writes accepted at the HidStream API layer; IDENTITY read still times out.                                                                                                                                                                                                                               |
| v2.4.1-beta.4       | Full diagnostic build: forced verbose log, raw HID report descriptor dump, 5-strategy probe sweep, sibling-interface probing | **Crashed** on input-only sibling (`BuildPrefixedBuffer` IndexOutOfRangeException on `totalLen=0`). Captured the vendor interface descriptor showing "Constant"-flagged input/output — explained why ReadFile was returning nothing.                                                                     |
| v2.4.1-beta.5       | Crash fix + Strategy G (`HidD_SetOutputReport` via P/Invoke)                          | **Major breakthrough**: `HidD_SetOutputReport` returned OK on the vendor interface — the FIRST write any strategy got the firmware to accept. GetInputReport returned err 31 ("no data") because it doesn't wait for new data.                                                                          |
| v2.4.1-beta.6       | Strategy H: SetOutputReport + async HidStream.Read on the same device                 | Write succeeded again, async listener captured 0 reports. User confirmed gen-2 web driver at drunkdeer.keybord.net.cn DOES work for him → protocol IS reachable, our single-stream listener is on the wrong pipe.                                                                                       |
| v2.4.1-beta.7       | Strategy J: multi-collection parallel listener — open every VID/PID sibling, listen on all                                                       | Confirmed **3 of 7 collections fail to Open**: `mi_00\kbd`, `mi_02&col01\kbd`, `mi_02&col02` (mouse). Other 4 captured zero. Identified the wall: kernel-level HID Keyboard/Mouse collection protection. **Admin test (user re-ran as administrator) showed identical results — admin doesn't lift the block.** |
| v2.4.1-beta.8       | Production fix: `Gen2KeyboardChannel` wrapping `HidD_SetOutputReport` + zero-access `HidD_GetInputReport` polling; registry in `HidDeviceExtensions`; auto-routes WritePacket/WritePacketNoAck through channel when registered | **Awaiting user validation.** ~50% confidence (see below).                                                                                                                                                                                                                                               |

## Why we landed on the v2.4.1-beta.8 implementation

The Microsoft-documented escape hatch for the kbd/mouse collection block is:

> Open the device handle with `dwDesiredAccess = 0` (i.e. NO `GENERIC_READ` or `GENERIC_WRITE`). The kernel allows this open even on blocked collections. The resulting handle is **invalid for `ReadFile`/`WriteFile`** but **valid for the entire `HidD_*` family** (`HidD_SetOutputReport`, `HidD_GetInputReport`, `HidD_GetFeature`, etc.).

Chrome's WebHID on Windows uses this fallback (see `device/hid/hid_connection_win.cc` in Chromium source — it tries full access first, falls back through reduced access modes). HidSharpCore does not — `device.Open()` always uses `GENERIC_READ | GENERIC_WRITE` and surfaces `DeviceIOException` on failure with no retry.

So the fix has two pieces:

1. **A new I/O wrapper** ([Driver/Gen2KeyboardChannel.cs](../Driver/Gen2KeyboardChannel.cs)) that opens:
   - The vendor write interface (mi_01) with full access via P/Invoke `CreateFile`, for `HidD_SetOutputReport`
   - Every sibling collection with `dwDesiredAccess = 0`, for `HidD_GetInputReport` polling
   - `WriteAndPoll` sends via SetOutputReport, then loops `HidD_GetInputReport` across all read handles every ~10ms until either a non-stale matching response arrives or the timeout (default 1500ms) expires
   - `WriteNoAck` mirrors `HidStream.WritePacketNoAck` semantics

2. **Transparent routing in [Driver/HidDeviceExtensions.cs](../Driver/HidDeviceExtensions.cs)**:
   - A `ConcurrentDictionary<string, Gen2KeyboardChannel>` keyed by device path
   - `WritePacket` and `WritePacketNoAck` check the registry; if a channel is registered for the stream's device, both methods route through the channel instead of calling `stream.Write` / `stream.Read`
   - Gen-1 keyboards: byte-identical behaviour (no registered channel → falls through to existing path)

`KeyboardManager.FindKeyboard` adds a `TryGen2Detection` step that runs after the standard HidSharp probe times out. It builds a `Gen2KeyboardChannel`, sends the gen-1 identity bytes, polls for a `[0xA0 0x02 0x00 ...]` response, and on success registers the channel for the device path. From there, every `ProfileManager.PushCurrentProfile`, every `IsConnected().Ping()`, every keystroke-tracking listener — they all open a regular HidStream and call WritePacket extensions, which auto-route through the channel. **Zero changes needed anywhere else in the codebase.**

Cleanup is handled in two places:
- `KeyboardManager.KeyboardWithSpecs` setter: when the connected device changes, unregister/dispose the outgoing device's channel
- `KeyboardManager.Dispose`: belt-and-braces `ClearAllGen2Channels()`

## Confidence assessment

**Pro-fix (works as designed):**
- Chrome WebHID demonstrably works on his hardware
- `HidD_SetOutputReport` reached the firmware in beta.5 (return value = OK, no Win32 error)
- Microsoft documents the zero-access CreateFile escape hatch
- Protocol bytes are correct (extracted from JS bundle, byte-identical to gen-1)

**Con-fix (might not work):**
- `HidD_GetInputReport` returned err 31 ("no data") in beta.5 with full access — possible that with zero access it behaves identically because the kernel filters input reports out of "constant"-flagged descriptors regardless of access mode
- Chrome's WebHID might use a mechanism we haven't replicated — possibly `RegisterRawInputDevices` (RawInput API) or a parent USB-device handle (not per-collection HID handle) — rather than dwDesiredAccess=0 + GetInputReport
- The firmware may have an init sequence we haven't sent (handshake before identity)
- Polling cadence might race the response delivery

**Honest estimate:** ~50%. Outcome-binary — either it works completely (keyboard detected, sync works, gen-2 saga closed for this user and any future user on similar hardware) or it fails identically to beta.7 and we need to pivot.

## What to do if beta.8 doesn't work

1. **Inspect the beta.8 log first.** The diagnostic alt-probes still run if `TryGen2Detection` returns null, so the log will show:
   - Whether `Gen2KeyboardChannel.TryOpen` succeeded in opening the previously-blocked collections with zero-access
   - Whether `HidD_SetOutputReport` succeeded
   - Whether `HidD_GetInputReport` returned any data on any read handle
   - If GetInputReport returned data but it didn't match the expected `0xA0` header, what the actual bytes were
2. **If GetInputReport returns stale-but-nonzero data** — the polling filter might be over-aggressive. Relax `expectFirstByte` and accept any non-zero response, then parse and see.
3. **If GetInputReport consistently returns false** even after a successful SetOutputReport, that confirms the dwDesiredAccess=0 path doesn't help us read input. The next options are:
   - **`RegisterRawInputDevices`** (Windows Raw Input API) — apps can subscribe to raw HID input reports for keyboard/mouse/vendor usages without opening the device handle at all. Apps receive `WM_INPUT` messages with the raw report bytes. Documented and well-supported. Bigger refactor (WPF apps need a message loop hook) but tractable.
   - **Tauri / Electron WebHID wrapper** — rebuild the app as a desktop wrapper around a WebHID-using webpage. Inherits Chrome's HID privileges. Largest architectural change, but it's the only path that's *guaranteed* to work since WebHID is what we know works for this user.
4. **If the user has access to USB sniffing tools** (Wireshark + USBPcap), capturing the wire traffic while the gen-2 web driver works would definitively show what method Chrome is using. That's the surest path to picking the right implementation.

## What we should keep regardless of beta.8 outcome

The diagnostic infrastructure in [Driver/KeyboardManager.cs](../Driver/KeyboardManager.cs) (Strategies A–J + the multi-collection inventory dump + the raw report-descriptor dump) is genuinely valuable for any future "keyboard not detected" report. Productionization plan once the dust settles:

- Keep the diagnostic probe sweep + inventory dump + report-descriptor dump (always-on, fires only when probe fails — log overhead is negligible because it's exception-path code)
- Revert the force-verbose-logging in [WpfApp/App.xaml.cs:108-117](../WpfApp/App.xaml.cs#L108-L117) back to opt-in `--verbose-log` flag (verbose-on-by-default is fine for beta.4-beta.8 diagnostic builds but eats the 2 MB log cap on normal users' machines)
- This means any future user reporting "not detected" sends one log and we already have the topology + descriptors + probe results — no more 8-round iteration

## Related memory entries

- `[[project-gen2-firmware-vid-shift]]` — concise summary of this finding (saved across multiple updates as the investigation progressed)
- `[[feedback-discord-messages]]` — Daniel forwards beta links to the user via Discord; keep messages tight (~5 lines max)

## Reference commits

- `8d31996` — v2.4.0 baseline (where this user started)
- v2.4.1-beta.1 through v2.4.1-beta.8 tags — full beta lineage
- HEAD as of this writing: v2.4.1-beta.8 with the production gen-2 fix
