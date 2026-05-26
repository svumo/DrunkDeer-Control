# Handoff — Beta.20 shipped, gen-2 diagnostics + deerios collab in flight

**Date**: 2026-05-25
**Branch**: `main`
**Last tag**: `v2.4.1-beta.20` (just shipped, awaiting user logs)
**Active threads**:
1. Gen-2 OEM detection on `VID 0x19F5 / PID 0xFB5C` — beta.20 should land it
2. One affected user confirmed they'll install Wireshark + USBPcap and try a capture (ground-truth for the wire format)
3. Collaboration with **deerios** on third-party `DrunkDeerSDK` — PR #1 open, more PRs queued

## TL;DR for the next agent

We're closing in on the gen-2 OEM saga. Beta.20 is the **eight beta in a row of WebHID-stack debugging** — the architecture (embedded WebView2 + `navigator.hid`) is proven correct, the wire-format details are what's left. Two independent users with identical hardware fingerprints (manufacturing batch, serial `2024-04-25`) are testing.

Don't be the agent that opens with "wait, why aren't we using Tauri?" — that was litigated and parked twice. The current architecture works; we're one or two wire-format details from done.

## Three docs you must read first

These together contain everything the next session needs. **Don't re-derive.**

1. **[docs/gen2-oem-investigation.md](./gen2-oem-investigation.md)** — beta-by-beta investigation record from betas 1–8 (pre-WebHID). Hardware fingerprint, per-collection breakdown of the OEM keyboard's HID descriptor, why HidSharp / HidD_* / Raw Input all failed.
2. **[docs/keyboard-protocol-gen2.md](./keyboard-protocol-gen2.md)** — gen-2 wire protocol notes from the official web driver JS bundle (cached locally at `%TEMP%/gen2-driver/index.js`).
3. **[docs/handoff-beta12-shipped.md](./handoff-beta12-shipped.md)** — prior handoff doc covering betas 4–12 (WebView2 architectural pivot). Some Outcome A/B/C language there is stale (the "if WebHID doesn't work" branch never fired — it did work).

Plus the commits below for the rest of the picture:
- Beta.13 (`308f138`) — `SetVirtualHostNameToFolderMapping` for secure-context origin → bridge ready
- Beta.16 (`3bd761a`) — close consent dialog on Continue (WPF modal Z-order issue, took 4 betas to figure out)
- Beta.17 (`8f8703b`) — picker visibility via anchor positioning + Win32 `SetForegroundWindow`
- Beta.18 (`ac32457`) — surface JS error field; keep WebView2 page alive post-pick
- Beta.19 (`51df4a5`) — `device.collections` enumeration; first attempt at no-Report-ID fallback (wrong direction)
- Beta.20 (`fcfa38d`) — `sendReport(0, raw_64_bytes)` matching the OEM's actual descriptor

## Where we are right now

### State of the gen-2 OEM saga

**Hardware**: Two confirmed users (`Дмитрий`, `child` — Windows usernames), identical fingerprint:
```
VID:     0x19F5
PID:     0xFB5C
Mfg:     'DrunkDeer'
Product: 'DrunkDeer A75 Pro'
Serial:  '2024-04-25'    ← same on both = one manufacturing batch
Release: 1.7
```

**What works as of beta.20**:
- WebView2 + WebHID bridge loads from a secure-context origin (`https://drunkdeer.local/index.html`)
- Bridge reports `webhid=True, secureContext=True`
- Consent dialog → user picks keyboard → picker resolves `ok=True`
- Device opens, `device.collections` enumerable from JS
- `sendReport(0, ...)` matches the OEM's no-Report-ID descriptor (the fix shipped in beta.20)

**What's not yet confirmed (pending user log)**:
- Whether the firmware actually echoes the identity packet back on this interface
- If yes, KeyboardType + Firmware decode + sync should just work
- If no, the response is going to a different collection (or this interface really is write-only) and we need a different read strategy

### The pending USBPcap capture

One of the two affected users **agreed to install Wireshark + USBPcap and run a capture** while using the official web driver at `drunkdeer.keybord.net.cn`. The exact ask sent was:

> Big ask, but I have a way to fully close this out. Install Wireshark from wireshark.org with Npcap and USBPcap checked. Plug in the keyboard, capture on USBPcap1, open drunkdeer.keybord.net.cn in Chrome, pick the keyboard, change an actuation point, save a profile. Stop capture, send the .pcapng.

If they come through, that capture **completely resolves** any remaining wire-format guesswork — it'll show byte-for-byte what Chromium sends to this hardware on the working path. **Run it through [deerios's `ProtocolAnalyzer`](https://github.com/deerios/DrunkDeerSDK)** to get NDJSON output with message-name decoding.

### What to do when the next log lands

| Log signature | Diagnosis | Action |
|---|---|---|
| `<- (gen2 webhid match, 64 bytes)` + `-> KeyboardType=750 Firmware=0.XX` | **Detection works** | Run cleanup pass (see "Outcome A" below), promote to v2.4.2 stable |
| `sendReport: ... falling back to sendReport(0, data) (64 bytes, no prefix)` then `READ FAILED (gen2 webhid): no matching response within 2000ms` | Send accepted, firmware silent | Firmware response probably arrives on a different collection. Try subscribing to ALL HID devices matching VID 0x19F5 in the JS bridge, not just the one picked. |
| `sendReport: ... JS error: Failed to write the report` (same as before) | Send still rejected | Descriptor disagreement beyond what we've parsed. Capture the user's `device.collections` log line, compare exactly. |
| Some new JS error | New failure mode | The error string is the answer — debug from there. |

## The deerios collaboration

`deers Ⓥ [DEER]` (Discord) maintains a third-party DrunkDeer SDK at **[github.com/deerios/DrunkDeerSDK](https://github.com/deerios/DrunkDeerSDK)** — private repo, Daniel has write access. Verified 2026-05-25 via `gh api .../collaborators/svumo/permission` returning `{"can_push": true, "permission": "write"}`.

**What deerios's SDK is**: pure protocol library (not an app). YAML-driven code-gen + a Wireshark-style `ProtocolAnalyzer` CLI that validates USBPcap captures against the spec. See [[project-deerios-collab]] memory entry for full context.

### Open PR (ours)

- **PR #1**: [Add SetLightingMode and the 26-mode catalog](https://github.com/deerios/DrunkDeerSDK/pull/1) — opened 2026-05-25, awaiting review. Adds the simple-mode-set call (`sendLedModeData`) that fills the gap between his existing `RgbKeyDataPacket` (per-key data) and `SetLightingOff`. Includes the 26 built-in animation modes as a documented catalog.

### What we have to give him, in priority order

| # | Contribution | What we have | Status |
|---|---|---|---|
| 1 | RGB mode catalog + `SetLightingMode` | `docs/protocol-findings-keybord-net-cn.md §P1` (26 modes + byte formats) | **PR #1 open** |
| 2 | 15 extra keyboard models | `Driver/KeyboardModels.cs` — 19 verified type-byte triples + PIDs + 126-slot layouts; his `models.yaml` has 4 (a75/a75_pro/a75_ultra/a75_master) | Drafted, not yet PR'd |
| 3 | OEM 0x19F5 / PID 0xFB5C hardware variant fingerprint + no-Report-ID descriptor workaround | Current beta.20 work | Hold until beta.20 confirmed working |
| 4 | Opcodes he doesn't have: `A2 n`, `AB n`, `C1 03`, `FB 05`, `FC 05/07`, `FD 07 0n` | `docs/keyboard-protocol.md §6` | Could be added incrementally |
| 5 | Bootloader protocol decode | `docs/firmware-flash-research.md` (decoded from a real capture) — he has the raw pcap, we have the decoded notes | Share informally on Discord rather than PR |

### What he has that we lack

| Thing | Where in his repo | Why we should care |
|---|---|---|
| `0x55` profile-block opcode family (ReadBaseBlock, Read/WriteFuncBlock, Read/WriteMacroChunk, Read/WriteKeyTrigger, WriteActiveProfile) | `protocol/base_messages.yaml` + `protocol/structs.yaml` | Newer firmware feature — fetch profiles directly from keyboard instead of localStorage. Source unclear; he discovered this independently. |
| `ProtocolAnalyzer` CLI | `DrunkDeer.ProtocolAnalyzer/` | Validates pcaps against protocol; we should use it on the affected-user capture when it arrives |
| Source-gen from YAML | `DrunkDeer.CodeGen/` | Different architectural approach to ours — worth understanding long-term |

### Known protocol divergence (don't change our code)

His `IdentityResponse` YAML disagrees with our `Driver/KeyboardSpecs.cs` on two byte offsets:

| Field | Our code | Deerios YAML |
|---|---|---|
| firmware_version | `(packet[8] << 8) \| packet[7]` u16-LE | `packet[10]` u8 |
| rapid_trigger_auto_match | `packet[30]` | `packet[34]` |
| auto_match_mode | `packet[31]` | `packet[35]` |
| last_win_replace | `packet[32]` | `packet[36]` |

His reference hardware is **A75 base** (type bytes `[0x0b, 0x04, 0x01]`). Our verified hardware is mostly A75 **Pro** (`[0x0b, 0x04, 0x03]`). May be a genuine per-model byte-layout divergence — both parsers could be right for their respective hardware. Saved as a "known divergence" in the deerios-collab memory entry. **Do NOT change our parser to match his offsets** — ours is empirically validated against the official firmware bundle's published versions (`0x09 / 0x17 / 0x27 / 0x55`).

### His near-term plans (from Discord)

- **2026-05-25/26**: VM setup for Ghidra-based RE on the `.bin` firmware file (with claude-mcp). If he gets even a partial decompile of the bootloader's HID handler, that resolves all the `_reserved` mystery fields on both sides.
- Key remapping is on his roadmap but not yet implemented (we have this already in `Driver/Packets.cs::BuildRemapPackets`).

## Working tree state at handoff

`git status` is clean except for:
- `docs/firmware-flash-research.md` — modified earlier (CRLF/line-ending shimmer, no content change)
- `docs/keyboard-protocol.md` — same, no content change
- `docs/protocol-findings-keybord-net-cn.md` — same, no content change
- `docs/handoff-beta12-shipped.md` — untracked (the prior handoff doc, never committed)
- `docs/handoff-beta20-shipped.md` — this file

None of these affect anything. The interesting work is on `main` already.

## What's NOT been touched (legitimately, don't change them)

- Gen-1 detection + protocol code (KeyboardSpecs, Packets, KeyboardModels, FirmwareCapabilities) — verified across multiple hardware + firmware versions
- Profile sync (PushCurrentProfile, all the BuildXxxPacket calls) — verified
- KeyboardView (recently rebuilt, working)
- Auto-updater / InstallationManager / canonical install — load-bearing
- `WpfApp/App.xaml.cs` `--firmware-too-old-demo` and `--keyboard-debug` CLI flags

## What to do next, by outcome

### Outcome A: beta.20 lands detection (most likely if firmware echoes back)

1. Pop a victory message in Discord to both users. Both have been very patient.
2. **Run the deferred v2.4.2 cleanup pass** (still pending from beta.12 handoff):
   - **Critical**: Gate the gen-2 fallback chain on `device.GetMaxOutputReportLength() == 65` in [Driver/KeyboardManager.cs](../Driver/KeyboardManager.cs)::`FindKeyboardCore`. Currently the gen-2 fallback runs for ANY DrunkDeer device whose standard probe fails — including gen-1 with transient failures, adding ~7s of pointless WebHID-reconnect attempts.
   - Revert force-verbose-logging in `App.OnStartup` (currently still on for beta.20 — search for `forcing Verbose=true (beta.20`).
   - Gate Strategy A–J diagnostic probes in `KeyboardManager.cs::TryAlternativeIdentityProbes` behind a `--diagnose` CLI flag. Keep the code, just don't auto-fire.
   - Bump csproj to `2.4.2`, tag `v2.4.2`, ship as **stable** (not prerelease).
   - Update [CLAUDE.md](../CLAUDE.md) "Known Issues" section.
3. **PR #2 to deerios**: 15-keyboard-model catalog port. Format already understood — same insertion pattern as PR #1 but in `protocol/models.yaml`.
4. Before v2.4.2 promotion, Daniel asked to verify gen-1 detection still works via **unplug/replug** path (not just cold-start). ~2s detection expected.

### Outcome B: send works but firmware silent (no inputreport)

Beta.20's send goes via `sendReport(0, 64_bytes)` and the descriptor accepts it. If the firmware doesn't respond, the most likely cause is **the response comes on a different HIDDevice than the one we picked**.

Currently the JS bridge subscribes `inputreport` only on `device` (the user-picked one). To check other collections:

```js
// In WebHidBridgeHtml.cs, after device is set:
const all = await navigator.hid.getDevices();
for (const d of all) {
  if (d.vendorId === device.vendorId && d !== device && !d.opened) {
    try { await d.open(); attachInputListener(d); }
    catch (e) { /* may be a Keyboard/Mouse usage = blocked by Chromium */ }
  }
}
```

Chromium WebHID blocks Generic Desktop Keyboard/Mouse collections. If the response comes on one of those, we're stuck again. But if it comes on a vendor sub-collection, this catches it.

### Outcome C: same error as beta.19 (`Failed to write the report`)

Means `device.collections` log doesn't match what we assumed. Pull the user's actual `device topology` log line and compare against the descriptor parse we did from beta.18 inventory data. May need a different payload size (e.g. 63 not 64).

### Outcome D: user installs Wireshark + sends pcap

**Highest-leverage outcome.** Run their pcap through deerios's `ProtocolAnalyzer`:

```
cd <deerios-sdk-clone>
dotnet run --project DrunkDeer.ProtocolAnalyzer -- --pcap <their-capture.pcapng> --firmware oem-19f5
```

NDJSON output shows every packet decoded. Any packet the analyzer marks as "unknown message" with structural_ok=false is a wire-format quirk of their firmware. The `summary` line shows firmware_fields snapshot.

This single capture **closes all remaining "unverified" entries** in our [docs/keyboard-protocol.md](./keyboard-protocol.md) for this hardware family.

## Important context for the next session

### Daniel's preferences (memory entries already capture this)
- Discord forwarding messages: ~5 lines, no theory/background (see `feedback_discord_messages.md`)
- Terse responses preferred. No emojis unless explicitly requested.
- Trust-but-verify: he'll often correct sloppy framings (recent: "unfixable" was wrong word, since we did fix it)

### Release flow
- See `CLAUDE.md` "Cut a Release" section. **The in-app auto-updater hits `releases/latest/download/DrunkDeer-Control.exe` so the asset name MUST stay hyphenated.**
- Always copy `DrunkDeer Control.exe` → `DrunkDeer-Control.exe` (hyphen) before `gh release create`.

### Memory entries that are now authoritative
- `MEMORY.md` index has been updated.
- `project_gen2_firmware_vid_shift.md` — was rewritten 2026-05-25 (no longer concludes "unfixable"; now reflects beta.13–20 WebView2 WebHID resolution path).
- `project_deerios_collab.md` — new entry covering the collaboration.
- `reference_deerios_protocolanalyzer.md` — new entry covering the USBPcap analysis tool.

### Things to NOT do
- **Don't open Tauri/Electron rewrite discussions.** It was litigated twice. Same WebView2 underneath = same wire-format issue = 6+ week rewrite for zero gain.
- **Don't auto-trust deerios's protocol claims without cross-checking against our extracted JS bundle notes.** The byte-offset divergence (firmware_version etc.) shows that even his YAML can have model-specific quirks.
- **Don't refactor the gen-2 fallback chain for code-cleanness while detection is in flight.** Wait for confirmation, then do the v2.4.2 cleanup pass.

## Suggested skills for the next session

When the next log / pcap arrives, the work is mostly diagnostic + small targeted edits — no specific skill required. But these would be useful in specific outcomes:

- **`verify`** — if you want to drive a real-app interaction sequence (e.g. Daniel verifying gen-1 detection unplug/replug before v2.4.2 promotion in Outcome A).
- **`simplify`** — for the v2.4.2 cleanup pass (Outcome A). Multiple overlapping gen-2 detection paths in `KeyboardManager.cs` can probably be collapsed once we know which one wins.

`graphify` is NOT useful here — the codebase is already well-documented in docs/.

## First task for next session

1. Open Discord, check for any new messages from the two affected users
2. If a log file is in `~/Downloads/debug (NN).log`: read it, follow the Outcome A/B/C/D table above
3. If a `.pcapng` is in `~/Downloads/`: process per Outcome D
4. If nothing: stay in standby. Maybe iterate on PR #2 (15-model port) to deerios — that's productive work that doesn't depend on user feedback
