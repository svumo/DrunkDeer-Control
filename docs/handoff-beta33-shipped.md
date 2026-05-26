# Handoff — Beta.33 shipped, awaiting USBPcap of factory-reset LW activation

**Date**: 2026-05-26 evening
**Branch**: `main` (clean apart from pre-existing CRLF shimmer on `docs/keyboard-protocol.md` + the untracked handoff docs)
**Last tag**: `v2.4.1-beta.33` — prerelease, exe attached at https://github.com/svumo/DrunkDeer-Control/releases/tag/v2.4.1-beta.33
**Two active threads**: gen-2 OEM Last Win activation (tester B), and Kun-switch 0.01mm RT precision (老林). Both relayed via Daniel on Discord.

## TL;DR for the next agent

Today's session shipped seven betas (27 → 33) covering: gen-2 OEM actuation sync, gen-2 OEM Last Win pair table, Kun-switch slider precision, gen-2 FuncBlock master-bit flip, a WebHID echo-filter bug, a slider-value display rounding bug, and a Known Issues note about sub-0.1mm RT. Gen-2 actuation sync is *fully working* on tester B's hardware. Gen-2 Last Win is *almost* working — beta.32 made the master-bit flip succeed, beta.31/32 produced SOCD behaviour on the wrong slots (W and S instead of A and D, because tester B's keyboard had pre-existing remap corruption from beta.28's bad 0x09 writes), and *after he did a full factory reset*, LW now doesn't activate at all.

**Next session is gated on one USBPcap from tester B**: the official driver activating LW + a single A↔D pair from a freshly-reset keyboard. The capture will show what setup packets the official driver sends from a clean state that we're missing — almost certainly the 0x55 0x09 per-pair RTP writes (we dropped these in beta.30 because the addresses didn't fit a clean pattern across two prior captures), and possibly other init.

**Don't speculatively re-ship 0x09 writes at the addresses we have**. They're what corrupted tester B's keyboard in the first place. Wait for the fresh capture.

## Read these first (don't re-derive)

1. **`docs/handoff-beta26-diagnostic-shipped.md`** — yesterday's state (gen-2 detection bugs, beta.21-26).
2. **`docs/gen2-wire-format-confirmed.md`** — the wire format. Updated 2026-05-26 with correct response data offset (8, not 10) and the corrected `key_mode` byte (0x00, not 0x01).
3. **`memory/project_gen2_sync_commit_missing.md`** — has been *replaced in spirit* by `project_gen2_actuation_fixed_beta27`. Worth re-reading because it documents the failure mode of the "missing commit opcode" red herring.
4. **`memory/project_gen2_firmware_vid_shift.md`** — older context for the OEM VID 0x19F5 path.
5. **`memory/feedback_discord_messages.md`** — Daniel's preferred Discord-relay style (~5 lines, no theory).
6. **Commits `2933048` (beta.27) → `9c347c4` (beta.33)** — each has a detailed rationale in the message. `git show <hash>` for the full reasoning.

## What changed in each beta tonight

| Beta | Tag | Hash | Thread | What it shipped |
|---|---|---|---|---|
| .27 | `v2.4.1-beta.27` | `2933048` | gen-2 actuation | `key_mode` byte 0x01 → 0x00, target active profile slot (was hardcoded 0). Verified by tester B — AP sync actually changes hardware behaviour for the first time since beta.12 |
| .28 | `v2.4.1-beta.28` | `deee4f8` | gen-2 LW | Added 0x55 0xA5 LW pair table writes + 0x55 0x09 per-pair RTP writes. Pair table at addr 0x0100, byte-identical to official driver. 0x09 used hardcoded addresses 0x086F + dir × 6 from usb3.pcapng |
| .29 | `v2.4.1-beta.29` | `8facc01` | Kun precision | `ActuationDrawer.SetSliderRanges` gains `dsUsMinMm` param. Per-dialect: Legacy 0.1mm, OldHighPrec/NewHighPrec 0.01mm. Tick frequency unchanged at 0.1mm so labels look the same |
| .30 | `v2.4.1-beta.30` | `a946ea6` | gen-2 LW | Dropped 0x09 per-pair RTP writes — the addresses varied across captures (0x086F/0x0875 for A↔D, 0x084E/0x0872 for W↔S) so the scheme is runtime-allocated, not fixed. Beta.28's hardcoded addresses were corrupting tester B's keyboard's remap region |
| .31 | `v2.4.1-beta.31` | `ccf4f6e` | gen-2 LW | FuncBlock master-bit flip via read-modify-write. 0x55 0x05 read at addr 0x0040 + 0x0078 (64 bytes total), modify byte 8 of primary chunk (bit 3, 0x08), 0x55 0x06 write-back. Preserves other Func state. Beta.31's read failed every time because of a bug shipped in beta.32 |
| .32 | `v2.4.1-beta.32` | `852cf15` | gen-2 LW | Reject 0x55-prefixed payloads in `Gen2WebHidChannel.PassesFilter`. Chrome's WebHID loops outgoing 0x55 requests back through the input endpoint as echoes; the polling loop was picking up the echo as the "response" and dropping the real 0xAA response on the next cycle. Unblocked beta.31's FuncBlock read |
| .33 | `v2.4.1-beta.33` | `9c347c4` | Kun precision | Slider value display format `0.0` → `0.0#`. 0.0 rounded sub-0.1 values up to 0.1 ("0.05" displayed as "0.1 mm"). 0.0# shows "0.05" correctly while keeping "0.5" / "2.0" the same. Applied to ActuationDrawer + RdtPairDrawer. Added Known Issues entry explaining sub-0.1mm only matters on Kun switches |

## Current state by tester

### Tester B — gen-2 OEM A75 Pro (VID 0x19F5)

**What works**: connection (beta.26 tight WebHID picker), actuation sync (beta.27 profile slot + key_mode fixes), FuncBlock master-bit flip (beta.32 read fix), LW pair table writes byte-identical to official driver, Known Issues window.

**What doesn't work**: LW behaviour activation on a freshly-reset keyboard. Beta.32 + factory reset = pair table written + master bit on + SOCD silent.

**The smoking gun** from `debug (17).log`:
- FuncBlock byte was already `0x0e` (master ON) when our app first probed — factory reset didn't clear FuncBlock
- Our pair-table writes succeeded byte-identical to the official driver
- No SOCD activation, no key remapping ("all keys work as usual")

**Theory**: the 0x55 0x09 per-pair RTP writes we dropped in beta.30 are actually **required**. In tester B's previous sessions (debug 11, 13, 14, 16) SOCD did fire — because beta.28's bad 0x09 writes had filled the per-pair RTP region with *something* non-zero. The firmware found data there and tried to use it (wrong values, hence wrong-key behaviour). After factory reset the region is genuine zero, so the firmware finds nothing and the SOCD transition never fires.

**Why we can't just re-add 0x09 writes**: addresses varied across the two captures we have. 0x086F+0x0875 worked for A↔D in usb3.pcapng. 0x084E+0x0872 worked for W↔S in usb4.pcapng. No clean mapping from slot-index, HID-code, or pair-index. The addresses are firmware-managed.

### Tester A — same gen-2 hardware fingerprint as B but Chrome WebHID still doesn't enumerate mi_01

Hasn't tested anything since the beta.22-26 saga. Beta.26's tight picker filter is supposed to be the diagnostic. Untested on his machine. See `memory/project_gen2_chrome_webhid_enumeration_regression.md`.

### 老林 — Kun-switch user (A75 Pro ANSI, firmware 0x16 = OldHighPrec)

**What works**: confirmed happy with beta.33. Can now drag the slider to 0.01mm and see the actual value (display formatter was rounding sub-0.1 up to 0.1, masking that the floor had already been lowered in beta.29). The Known Issues entry explains the Kun-switch context.

**Status**: closed.

### sliqx — A75 Ultra (gen-1) "arrow keys mapped wrong" + "can't use keys when LW on"

**Status**: open, blocked on his reply. Sent him a clarifying message yesterday asking what "arrow keys mapped wrong" actually means (visual layout vs remap corruption vs label mismatch) plus whether "can't use keys with LW" is the SOCD behaviour-as-designed vs whole-keyboard-silent. No response yet.

**Don't ship a fix for him until he clarifies.** The default LW seed (A↔D, W↔S, ←↔→, ↑↔↓) is auto-applied when toggling LW in our app — most likely candidate for confusion if he's reading SOCD behaviour as "broken keys."

## First task for the next session

**Wait for tester B's USBPcap of the official driver activating LW + A↔D pair from a freshly-reset keyboard.**

The instructions I drafted at the end of this session's Discord output (also visible in transcript):

```
Could you do one more USBPcap capture? Same setup as before, but
critically from your freshly-reset state:

1. Start with the keyboard in the state it's in right now —
   factory reset, no app interaction.
2. Open the official drunkdeer website.
3. Start USBPcap.
4. In the official site, enable Last Win.
5. Add ONE pair — A↔D again.
6. Click Save to keyboard.
7. Stop USBPcap and send the file.
```

Expected capture filename when it arrives: probably `usb5.pcapng` or similar in `C:\Users\skdes\Downloads\`. Previous captures are `usb.pcapng`, `usb2.pcapng`, `usb3.pcapng`, `usb4.pcapng`. They are NOT deleted — keep them for comparison.

### What to look for in the new capture

1. **The full opcode sequence from clean state.** Especially anything that runs BEFORE the 0xA5 pair table writes. In our existing captures (usb3, usb4) the keyboard was already in a configured state, so any "init" packets the official driver sends on a fresh keyboard may be invisible to us.
2. **Where the 0x55 0x09 writes target.** A↔D in this capture should land at *some* address. If it matches usb3.pcapng (0x086F + 0x0875), the 0x09 region is fixed-addressed for A↔D specifically — and we can ship beta.34 with those addresses. If it lands at a third pair of addresses, the allocation scheme is something more dynamic and we'd need more data.
3. **Whether 0x55 0x07 / 0x55 0x08 writes appear.** Those opcodes showed up in `usb.pcapng` (read-only session) as 40-OUTs each, and we suspect they're the per-key remap table. If they appear in the LW save flow, they may be related to the SOCD activation.
4. **Whether the official driver does any extra FuncBlock-byte changes** beyond byte 8 (master). The capture's FuncBlock writes had bytes that don't change between LW-off/LW-on — but the firmware may be sensitive to other bits we haven't decoded.

Use `tshark` (already at `/c/Program Files/Wireshark/tshark.exe`). Helpful commands from this session:

```bash
# Quick opcode counts on the OUT endpoint, filtered to device addr 10 (DrunkDeer)
"/c/Program Files/Wireshark/tshark.exe" -r "<file>" -Y "usb.endpoint_address == 0x04 && usbhid.data" \
  -T fields -e usbhid.data 2>/dev/null | awk '{print substr($1, 1, 4)}' | sort | uniq -c | sort -rn

# Full packet dump for one opcode (replace 55a5 with target)
"/c/Program Files/Wireshark/tshark.exe" -r "<file>" -Y "usb.device_address == 10 && usbhid.data" \
  -T fields -e frame.number -e frame.time_relative -e usb.endpoint_address -e usbhid.data \
  | awk '$4 ~ /^55a5/ {print}'

# Find the device address for VID 0x19F5
"/c/Program Files/Wireshark/tshark.exe" -r "<file>" -Y "usb.idVendor == 0x19f5" \
  -T fields -e usb.device_address | sort -u
```

### After analysing the capture

- **If the missing piece is the 0x09 per-pair RTP at the SAME addresses as usb3.pcapng for A↔D**: ship beta.34 that re-adds 0x09 writes — but only for the *exact* HID-code pairs the captures cover (A↔D, W↔S). Anything else gets the writes skipped until we have a capture for it. This is ugly but safer than guessing.
- **If the addresses are different again** — and especially if they suggest a per-slot allocation scheme — we may need a bigger investigation. Possibly ask tester B for captures of *several* different pair configs in sequence to see how allocations interact.
- **If there's a setup packet (e.g., a 0x07/0x08 write to a specific slot)** we never send: implement that. Add to `PacketsGen2.cs` with the same defensive read-modify-write pattern we used for FuncBlock.

### Don't do without more data

- **Don't speculatively send 0x09 packets to fixed addresses again.** That corrupted tester B's keyboard once already (beta.28 → required factory reset). Daniel was patient about it; don't burn that twice.
- **Don't add code that writes to addresses in the 0x0800-0x08FF range** without a wire-level capture showing the official driver writing there. That region appears to overlap the per-key remap table on gen-2 OEM.
- **Don't promote to stable v2.4.2** until both tester A and tester B are working end-to-end.

## Repo state at end of session

`git status` shows:
- Tracked changes: only `docs/keyboard-protocol.md` (pre-existing CRLF shimmer — not yours, don't touch)
- Untracked: `docs/handoff-beta12-shipped.md`, `docs/handoff-beta20-shipped.md`, `docs/handoff-beta26-diagnostic-shipped.md`, and this file

Main is at `9c347c4` (beta.33). Beta.27 → .33 all tagged + released as prereleases on GitHub.

## Communication style for Daniel — refresher

- Discord forwarding messages: ~5 lines, no theory/background. He relays verbatim, sometimes editing in his own style.
- Terse responses preferred. No emojis.
- He pushed back tonight when I added a UI feature without asking first (the Kun "info note") — accepted my recommendation when I argued against it, then later asked me to add it to Known Issues instead. **Default: ask before adding UI elements, even small ones.**
- "fix all then" / "go for it" / "ship it" = move fast, no more confirming.
- He'll occasionally cut me off mid-tool-call with a new message that supersedes what I was doing. Honour it.
- See `memory/feedback_discord_messages.md` and `memory/feedback_check_memory_before_correcting_third_parties.md` for the durable rules.

## Suggested skills for the next session

- **`verify`** — once a fix is shipped, verify the full happy-path SOCD behaviour on Daniel's own hardware (his A75 Pro works with our app already) AND get tester B's behavioural confirmation before promoting to stable.
- **`simplify`** — at some point the gen-2 OEM sync flow in `WpfApp/Profile/ProfileManager.cs SyncOemGen2` will be ready to consolidate. Daniel's had this on the deferred-cleanup wishlist since beta.12. Don't do it before LW is working end-to-end though.
- **`handoff`** — at end of next session, recurse on this. The pattern is working.
- **`graphify`** / **`grill-me`** — not useful here.

## Release recipe reminder (for promoting to v2.4.2 stable, eventually)

Same as the bottom of `docs/handoff-beta26-diagnostic-shipped.md`. The 6-step process. Watch out for the asset-upload 404 — happened once tonight, recovered by deleting the draft and using `gh release create` without the asset arg, then `gh release upload --clobber` separately. If you do that, the release shows up tagged-but-empty for a few seconds, then the asset attaches. Use the bash polling loop pattern I used in this session to confirm asset state before announcing the release.
