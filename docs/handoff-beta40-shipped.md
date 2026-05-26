# Handoff — Gen-2 OEM Last Win / RDT / Remap (waiting on tester B's beta.40 reply)

**Date**: 2026-05-26 late evening
**Repo**: `c:\Users\skdes\Documents\github\DrunkDeer-Control-rdt`
**Branch**: `main` at commit `c6d6655` (beta.40)
**Latest release**: `v2.4.1-beta.40` https://github.com/svumo/DrunkDeer-Control/releases/tag/v2.4.1-beta.40 (prerelease, exe attached)

## TL;DR

Gen-2 OEM A75 Pro (VID `0x19F5`) LW activation is "almost there" but the LW-paired keys froze on beta.38 even though our packet bytes matched the official driver byte-for-byte. Diagnosis: we wrote **too many packets** (bulk 0x09 user-keymap region + actuation full-region + FuncBlock R-M-W) where the official driver only writes 9. Beta.40 swaps the bulk 0x09 write for targeted single-slot writes that mirror the official driver's pattern.

**Status now**: Discord message for tester B drafted but not sent (waiting on Daniel to relay). When tester B's debug log comes back:
- If LW-paired keys fire correctly → beta.40 is the working baseline. Move to RDT and remap verification.
- If still frozen → strip more "extra" writes (KeyTrigger full-region, FuncBlock R-M-W). See **Decision tree** below.

## What beta.40 changes vs beta.38

`git show c6d6655` for the full commit message and rationale.

Key diff: [WpfApp/Profile/ProfileManager.cs](../WpfApp/Profile/ProfileManager.cs) `SyncOemGen2` LW section.
- **Removed**: `BuildWriteUserKeyMatrixSequence` bulk 7-chunk 0x09 write (still exists in `Driver/PacketsGen2.cs` as a builder, just no longer called).
- **Added**: `_gen2ArmedSlots` (HashSet&lt;int&gt;) — per-`ProfileManager` tracker for "slots we wrote non-default user-keymap entries to last sync". Reset on disconnect via `InvalidateGen2SlotMap`.
- **Added**: targeted single-slot 0x09 writes per pair direction. Stale slots (armed last sync but not now) get a single-slot 0x09 write restoring their exact factory entry (`type, code1, code2`) from the slot-map readback.
- **Unchanged** (still fires every sync): the 19-chunk KeyTrigger region write (actuation sync), the FuncBlock R-M-W (LW master bit), and the 0xA1 per-pair LW-armed KeyTrigger commits.

The unchanged parts are the next-most-likely culprits if beta.40 doesn't fix it.

## Decision tree for tester B's beta.40 reply

### If LW works ("hold A → 'aaa', press D → 'ddd', release D → 'aaa'")

1. Ask him to test:
   - W↔S and arrow pairs (←↔→, ↑↔↓)
   - Adding/removing pairs mid-session (verify no ghost pairs in the official driver readback)
   - Per-key remap (Caps → Esc)
   - RDT pair (he already showed RDT recording correctly in usb7 — verify it now fires)
2. Once verified, this is the baseline for stable v2.4.2.
3. Open follow-ups (low priority):
   - Decide whether the bulk-write builder `BuildWriteUserKeyMatrixSequence` should be deleted entirely
   - Verify per-key actuation tweaks land on the right keys (the slot-map routing was added in beta.37; the diagnostic in debug 19 showed factory defaults at every probed slot before our sync, so we never had hard evidence the routing actually took)
   - Promote the gen-2 path docs from temp/private memory to `docs/`

### If keys are still frozen

The remaining suspects in our LW-on sync flow (still doing things the official driver doesn't):

1. **KeyTrigger full-region write** (19 chunks, addr `0x0000..0x03F0`, key_mode=0 for every slot). Writes happen BEFORE the LW-specific writes, then the 0xA1 commits overwrite key_mode=1 only for paired slots. Possibly putting the firmware in a transitional state.
2. **FuncBlock R-M-W** (4 packets). Reads the FuncBlock, sets/clears bit 3 of byte 8, writes back. The official driver's usb5 capture had NO FuncBlock writes (master bit was already set after factory reset).
3. **Speculative gen-1 mode toggles** (4 fire-and-forget packets at the end: `0xB5` CommonSwitch, `0xFC 0x0B` LastWinReplace, `0xFD 0x0C` AutoMatchMode, `0xB6 0x03` KeystrokeTracking). The OEM firmware likely ignores these but unconfirmed.

Strip these in order — KeyTrigger region first, FuncBlock second — and re-test. Add a feature gate (only do the actuation sync when AP/RT actually changed in the profile, not on every sync) since the OEM driver only does it when sliders move.

### If "partial" — some pairs work, some don't

Look at the debug log for any "slot lookup failed" warnings or non-zero `userKeyFail`/`activateFail` counts. Could indicate the slot map has gaps (e.g., a pair includes a HID code not in the firmware's default keymap).

### If a different bug shows up

Debug from log specifics. The diagnostic probes (slot-map dump, per-key KeyTrigger sample reads) are still in the sync flow, so the log is dense with useful state info.

## Critical technical reference

These were all derived during this session — not in the repo's tracked docs yet.

### Wire format (gen-2 OEM, decoded from JS bundle 2026-05-26)

The OEM driver JS (drunkdeer.keybord.net.cn, chunk 198 / page-3fa…js) exposes:
- `setUserKeyMatrix(data, profile, layer, slot)` → `sendData(9, 512*layer + 3*slot + 2048*profile, data)` — this is the **0x55 0x09** opcode
- `setKeyTrigger(data, profile, slot)` → `sendData(161, 1024*profile + 8*slot, data)` — this is the **0x55 0xA1** opcode

For LW/RDT activation:
- **0x55 0xA5 pair table**: 5 chunked writes at base addr 0, 256 bytes total. Pair entries are 6 bytes each: `[0x10, 0x00, mainKey_HID, 0x10, 0x00, triggerKey_HID]`. LW and RDT pairs share the same table.
- **0x55 0x09 user-keymap entry** (3 bytes per slot, addr `3*slot`):
  - `data[0]` = entry type: `0x10` standard HID, `0x94` (148) = "socd" / Last Win, `0x95` (149) = "oks" / Release Dual-Trigger, `0x93` (147) = "rs" (separate feature, not yet used)
  - `data[1]` = pair index (counts 0..2N-1 across all pair directions in the save batch)
  - `data[2]` = partner slot (the firmware slot of the OTHER key in the pair) **— this was the critical fix in beta.37; beta.34 had hardcoded 0x14 = slot 20 = "8" key, which is why every LW-paired key paired with 8**
- **0x55 0xA1 LW-armed KeyTrigger commit** (LW only — RDT does not need this per usb7.pcapng):
  - 8-byte payload: `[0xA0, 0x01, ap_lo, ap_hi, 0x09, 0x00, 0x09, 0x1C]`
  - byte 1 = `0x01` = key_mode=1 (LW-armed)
  - bytes 2-3 = user's current AP in LE9 (stored = display − 1, units 0.01mm)
  - bytes 4-5 = `0x09 0x00` (rt_press_raw=9 → 0.10mm; press_dz=0)
  - bytes 6-7 = `0x09 0x1C` (rt_release_raw=9 → 0.10mm; release_dz=14)

### Slot map (verified on tester B's A75 Pro OEM, factory-reset, firmware vOEM 1.7)

128-slot key matrix, sub-cmd `0x55 0x07` reads the default keymap (10 chunks × 56 bytes, last chunk 8 bytes = 512 bytes total per profile per layer). See [Driver/KeyMatrixSlotMap.cs](../Driver/KeyMatrixSlotMap.cs) for the parser.

Confirmed key→slot positions on his keyboard from debug 19:
- Esc=0, F1-F11=1-11, ~=12, 1-7=13-19, 8=20, 9-0=21-22, -=23, Tab=24, Q=25, W=**26**, E=27, R=28, T=29, Y=30, U=31, I=32, O=33, P=34, [=35, Caps=36, A=**37**, S=**38**, D=**39**, F=40, G=41, H=42, J=43, K=44, L=45, ;=46, '=47, Z=50, X=51, C=52, V=53, B=54, N=55, M=56, ,=57, .=58, /=59, Space=63, Left=**67**, Down=**68**, Right=**69**, Up=**70**, F12=72, ==73, Del=74, Bksp=75, ]=76, \=77, Home=78, End=79, PgUp=80, PgDn=81, Enter=83.

This mapping differs from our visual layout's `KeyIndex` numbering — A is at our `KeyIndex 64` but firmware slot 37. The slot map readback resolves this for every model dynamically.

### What the beta.36 diagnostic confirmed (debug 19)

1. Slot map readback works (74 standard HID keys parsed).
2. Captured slot numbers from usb5/6 (A=37, D=39, S=38, W=26) match the live slot map.
3. Our `KeyboardLayout.KeyIndex` numbering doesn't match firmware slots on gen-2 OEM (was 64/66/65/44 for A/D/S/W).
4. Arrow slots are at 67/68/69/70.
5. Every probed slot read back the firmware factory default `[a0 01 95 00 1d 00 1d 1e]` = AP 1.50mm, key_mode=1, rt_press=0.30mm, rt_release=0.30mm, release_dz=15. Note key_mode=1 in the factory default is unexpected (we'd assumed 0). Document but not blocking.

## Where everything lives

### Captures (USBPcap) — `C:\Users\skdes\Downloads\`
- `usb3.pcapng` — early A↔D LW activation (post-state, not fresh)
- `usb4.pcapng` — W↔S LW activation (post-state)
- `usb5.pcapng` — **A↔D LW activation, fresh factory reset** (canonical reference for LW)
- `usb6.pcapng` — A↔D + W↔S two-pair save, fresh factory reset (confirmed pair-index counter increments across pairs)
- `usb7.pcapng` — **D↔T RDT activation, fresh factory reset** (canonical reference for RDT)

### Debug logs — `C:\Users\skdes\Downloads\`
- `debug (19).log` — beta.36 diagnostic. Slot map dump, KeyTrigger probes, confirms slot-shift.
- `debug (20).log` — beta.37 result. Showed the "ghost pair" bug (stale slot 70/68 retained 0x94 entries after ↑↔↓ pair removed).
- `debug (21).log` — beta.38 result. Bulk write fixed ghost pairs but introduced the frozen-keys symptom. This is the log that motivated beta.40.

### Decoded OEM JS bundle — `C:\Users\skdes\Downloads\dd-oem-js\`
- `page-3fa4485638850e0d.js` (508KB) — the main app bundle. All HID logic. The `setUserKeyMatrix` / `setKeyTrigger` formulas live here. Search `sendDeviceData(85,` for raw 0x55 calls.
- `extra/*.js` — supporting webpack chunks (per-model layouts etc.)
- `K75.json` (extracted) — the K75 layout the A75 Pro uses (per the model registry `0x19F5_0xFB5C → layout:"K75"`). Has 82 visible-key visual positions; useful for naming slots but **does NOT define firmware slot numbers** (those come from the keymap readback).

### HAR — `tools/captures/keybord-net-cn/drunkdeer.keybord.net.cn.har`
Source of the JS bundle. Moved into the repo 2026-05-26; gitignored via the existing `tools/captures/` rule. Re-extract with the Python snippet from this session's history if needed (HAR JSON entries with `mimeType=application/javascript`).

### Memory (auto-loaded into future sessions) — `C:\Users\skdes\.claude\projects\c--Users-skdes-Documents-github-DrunkDeer-Control-rdt\memory\`

Existing relevant entries:
- `project_gen2_firmware_vid_shift.md` — VID 0x19F5 detection background.
- `project_gen2_actuation_fixed_beta27.md` — superseded by this session's findings; the "actuation fix" only routed Profile.Keys_Array[i] to firmware slot i, which we now know is the WRONG mapping on gen-2 OEM. Update or replace once beta.40 is confirmed.
- `project_gen2_sync_commit_missing.md` — old hypothesis, mostly obsolete.
- `project_gen2_chrome_webhid_enumeration_regression.md` — tester A's separate issue (WebHID picker), unrelated to LW.
- `feedback_discord_messages.md` — Daniel's communication style; obey this when drafting Discord text.

After beta.40 lands either way, this session's discoveries (slot map, wire format, pair index, partner-slot byte) deserve their own memory entries.

## Beta cadence this session

For full context: `git log --oneline 9c347c4..HEAD` (range from end-of-previous-session through beta.40):
- `9c347c4` v2.4.1-beta.33 — previous session's last (Kun-switch slider display)
- `da0cf34` v2.4.1-beta.34 — first LW wire-format attempt (hardcoded A↔D/W↔S slots, but byte 2 = 0x14 was wrong)
- `e180909` v2.4.1-beta.35 — diagnostic-only (slot map readback added, no behaviour change)
- `d39c18e` v2.4.1-beta.36 — diagnostic + full slot map dump at normal log level
- `487ed12` v2.4.1-beta.37 — dynamic slot routing + correct partner-slot byte (first real attempt)
- `36eb4a8` v2.4.1-beta.38 — bulk-write user keymap (fixed ghost pairs but introduced frozen-keys)
- `25ab00a` v2.4.1-beta.39 — added RDT + per-key remap via bulk write (never released; deleted from GH)
- `c6d6655` v2.4.1-beta.40 — **shipped** — targeted single-slot writes

The `v2.4.1-beta.39` git tag still exists with no GitHub release. Either leave it as a marker or delete with `git tag -d v2.4.1-beta.39 && git push origin :v2.4.1-beta.39` once beta.40 ships stable.

## Open issues / not yet done

- **RDT FuncBlock master bit unknown.** The usb7 capture had no FuncBlock writes when enabling RDT. Either RDT activates without a master bit, or it shares LW's bit, or the bit was already in the right state. Verify behaviourally with beta.40 once LW works.
- **The actuation sync remains in the LW-change path.** It writes the full 1024-byte KeyTrigger region every sync, even when only LW state changed. The official driver only writes when AP/RT sliders move. Possibly redundant; possibly contributing to the frozen-keys bug. Strip if beta.40 doesn't fix it.
- **Profile-switch behaviour.** Active profile = 0 in tester B's testing. If he switches to profile 1, the LW writes still go to profile 0 — verified the OEM driver does the same in captures, but worth a sanity check.
- **Documentation debt.** The slot-map / 0x09 wire format / 0xA1 commit format / `BuildLwArmedKeyTriggerWrite` payload aren't in `docs/keyboard-protocol.md` yet. Once beta.40 is confirmed, fold them in.
- **Memory entries.** This session's hard-won findings (gen-2 wire format, slot-map readback, beta-by-beta bug list) need a `project_gen2_wire_format_decoded` entry.

## Communication style — Daniel

See `feedback_discord_messages.md`. Key points:
- Discord messages to tester B: ~5 lines, no theory, no background, just the ask and steps. Daniel forwards verbatim or edits slightly.
- Terse responses preferred. No emojis.
- "fix all then" / "go for it" / "ship it" = move fast, no more confirming.
- He pushes back on speculative changes; ask before adding speculative scope. Especially relevant: in this session he reverted my over-eager actuation-sync remap to dry-run twice. Be conservative.
- He pushed back during this session against me drafting summaries that imply "we shipped X working" when X is actually still being tested. Don't claim success before the user confirms.

## Suggested skills

- **`verify`** — once tester B confirms beta.40 works, use `verify` to walk Daniel's own A75 Pro (gen-1) through a full sync to make sure we haven't regressed gen-1. SyncOemGen2 only fires for gen-2 OEM, so theoretically safe, but worth a smoke test before promoting to stable.
- **`simplify`** — `WpfApp/Profile/ProfileManager.cs` `SyncOemGen2` has grown into a 400-line method with multiple try blocks and lots of branched diagnostic logging. If beta.40 is the final shape, candidate for extraction into a small `Gen2SyncSession` class.
- **`handoff`** — recurse on this if beta.40 testing extends past the next session.
- **NOT useful**: `graphify`, `grill-me`, `improve-codebase-architecture` — too high-level for this bug-chase.

## First task for the next session

**Wait for Daniel to relay tester B's beta.40 debug log.** The Discord message is already drafted (in conversation history, last assistant message ending with "Per-key remap and RDT are also wired up the same way..."). If he hasn't sent it yet, paste it and ask him to send.

When the log arrives:
1. Open it from `C:\Users\skdes\Downloads\debug (NN).log` (probably 22 or 23 next).
2. Grep for `pair feature writes complete` and `pair table`/`user-keymap 0x09`/`0xA1 LW commits` counts.
3. Apply the decision tree above.

Do not start coding speculative beta.41 until you've read the log.
