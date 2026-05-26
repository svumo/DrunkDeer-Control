# Handoff — Beta.26 shipped (diagnostic build), awaiting tester log

**Date**: 2026-05-26 early morning
**Branch**: `main` (clean apart from pre-existing CRLF shimmer on docs/keyboard-protocol.md)
**Last tag**: `v2.4.1-beta.26` — prerelease, exe attached at https://github.com/svumo/DrunkDeer-Control/releases/tag/v2.4.1-beta.26
**Active thread**: same single beta tester from the beta.22 handoff (Discord, Daniel relays). User has gone to bed; the next session resumes when their beta.26 log arrives.

## TL;DR for the next agent

Five hotfixes shipped tonight (beta.22 → beta.26) trying to fix one tester's keyboard-not-connecting report. Each one fixed *a* problem but not *the* problem. Current state: beta.26 is a **diagnostic build** — the picker filter is tight (`vendorId: 0x19F5, usagePage: 1, usage: 0`) and the JS bridge logs `getDevices()` before the picker fires. The next log will be conclusive about whether Chrome WebHID on this machine still exposes mi_01 anywhere.

**Core unsolved problem**: Chrome WebHID's HID device enumeration silently changed between **17:43** (beta.21 worked on this hardware) and **19:00** (beta.22 broke on the same hardware) on 2026-05-25. Same user, same machine, same code's WebHID detection path, opposite outcome.

- **beta.21**: picker returned a single HIDDevice for mi_01 alone, `collections=[{usagePage:1,usage:0,in:[0],out:[0]}]`. sendReport(0,…) fallback fired, firmware replied `aa 04 00 be …`, detection succeeded.
- **beta.22+**: picker returns a merged 5-collection HIDDevice (Consumer Control / System Control / Mouse / Keyboard / Tablet) with **mi_01 entirely absent** from `device.collections`. Every sendReport gets rejected by Chrome ("Failed to write the report").

Most likely cause: WebView2 Runtime auto-updated on this tester's machine in that ~1 hour window. Either Chrome started merging multi-interface HID devices, or it started stripping Const-only collections (mi_01's descriptor marks both Input and Output as `Const, Var, Abs`). Either way mi_01 is invisible.

## Read these first (don't re-derive)

1. **`docs/handoff-beta22-shipped.md`** — pre-beta.23 state (beta.22 was the last build where we thought detection worked end-to-end).
2. **`docs/gen2-wire-format-confirmed.md`** — the wire format. Unchanged. Still authoritative.
3. **`memory/project_gen2_chrome_webhid_enumeration_regression.md`** — short summary of the regression (the one I'm describing here).
4. **`memory/project_gen2_firmware_vid_shift.md`** — older context for the OEM VID 0x19F5 path.
5. **Commits `1f5f7ff` (beta.23) → `11bc82b` (beta.26)** — each has a multi-paragraph rationale in the message. Read `git show <hash>` for full reasoning.

## What changed in each beta tonight

| Beta | Tag | Hash | What it tried | Result |
|---|---|---|---|---|
| .23 | `v2.4.1-beta.23` | `1f5f7ff` | Tight picker filter for OEM VID; collection-aware reconnect; `forget()` API | Drafted but never released — user reported "only 1 entry in picker" which would have made the tight filter hide that one entry too. Approach revised same-session. |
| .24 | `v2.4.1-beta.24` | `ade44af` | Bridge post-pick validator (forget if zero writable outputs); once-per-session consent in MainWindow; loose picker filter | Broke the consent loop ✅ — log confirmed the bad pick got forgotten and dialog stopped firing — but the keyboard still didn't connect because Chrome returned the merged kbd device. |
| .25 | `v2.4.1-beta.25` | `8930510` | Skip `HidSharp.Open()` + `Gen2KeyboardChannel.TryOpen` + diagnostic alt-probes entirely for VID 0x19F5 — keep mi_01 untouched on C# side | No effect — beta.25 user log showed picker still returns the same merged 5-collection device. Disproved the "our app locked mi_01" hypothesis. |
| .26 | `v2.4.1-beta.26` | `11bc82b` | Tight picker filter on `{usagePage:1, usage:0}` + pre-picker `getDevices()` diagnostic logging in bridge | **Just shipped, awaiting log.** Diagnostic build. |

## All five tester logs lined up

All in `C:\Users\skdes\Downloads\` (tester's machine):

| File | Build | Outcome |
|---|---|---|
| `debug (3).log` and `debug (6).log` (identical) | beta.21 | ✅ Connected. Picker → single-collection mi_01 device → sendReport(0,…) fallback → firmware replied. |
| `debug (2).log` | beta.22 | ❌ Loop. Picker → boot-keyboard interface (`usage:6`, `in:[]`/`out:[]`). sendReport(4,…) rejected. Reconnect silently re-bonded every probe → consent re-fired forever. |
| `debug (4).log` | beta.24 | ❌ Loop broken (forget + once-per-session worked) but no connection. Picker → merged 5-collection device, no mi_01 present. |
| `debug (5).log` | beta.25 | ❌ Same as .24 — picker still returns merged device without mi_01, even with our C# side not touching mi_01 at all. |
| *(awaiting)* | beta.26 | Will tell us whether `requestDevice` with `usagePage:1, usage:0` filter returns mi_01 (we win) or 0 devices (Chrome doesn't expose mi_01 anymore, we need a different approach). |

## What we expect from the beta.26 log

Grep for `pre-picker getDevices()` and `requestDevice returned` — the bridge now logs both:

```
JS warn: requestDevice: pre-picker getDevices() returned N permitted device(s): [...with collections...]
JS warn: requestDevice: calling navigator.hid.requestDevice with filter={...}
JS warn: requestDevice: requestDevice returned N device(s)
```

Decision table:

| Log signature | Diagnosis | Action |
|---|---|---|
| `pre-picker getDevices() returned 0 permitted device(s)` AND `requestDevice returned 1 device(s)` AND topology shows `usagePage:1, usage:0` | **Full win.** Chrome can still see mi_01, the tight filter forced the picker to show it, user picked it cleanly. | Ship as beta.27 stable promotion. Update CLAUDE.md known-issues to remove the WebHID exposure caveat. |
| `requestDevice returned 0 device(s)` (or `null`) | Chrome WebHID genuinely doesn't enumerate mi_01 on this user's machine anymore. Picker was empty. | Tighten loop on the *real* root cause: ask user to check WebView2 Runtime version (`reg query "HKLM\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients" /s`). Consider asking them to manually downgrade WebView2, or test on a fresh Windows VM. See "If the picker is empty" section below. |
| `pre-picker getDevices()` lists a permitted device with `usagePage:1, usage:0` in its collections | The beta.24 `forget()` didn't actually evict everything — Chrome still has the bad permission cached. | Add a `forget all VID-0x19F5 permissions` pass on bridge init. |
| `pre-picker getDevices()` is empty AND `requestDevice` returns the merged 5-collection device anyway | Filter is being ignored. | Investigate WebHID filter spec edge cases for `usage: 0` (Undefined). May need to fall back to filtering only on `usagePage:1` (broader). |
| `requestDevice returned 1 device(s)` AND topology has the merged 5-collection device anyway | Chrome's filter is matching on the merged device because it has a `usagePage:1` collection somewhere. The post-pick validator will catch this and forget — same loop as .25. | Try `requestDevice({})` with no filter and let the user pick from all HID devices. See if mi_01 appears as a separate row in that broader picker. |

## If the picker is empty (most likely outcome)

This is the hardest path. We've spent 26 betas getting WebHID to work for this hardware, and now Chrome's enumeration changed under us. Options in rough order of cost:

1. **Ask user to check WebView2 / Edge version**. If they auto-updated recently, see if rolling back to a specific Evergreen Bootstrapper build restores beta.21's enumeration. The Evergreen Bootstrapper is at https://developer.microsoft.com/microsoft-edge/webview2/ — older builds are at https://learn.microsoft.com/en-us/deployoffice/webview2-install (rarely backed up though).

2. **Try `requestDevice({})` (no filter) in a beta.27 diagnostic**. Shows every HID device on the system; if mi_01 is enumerable AT ALL, it should appear. User has to know which entry to pick (many would be noise: Razer mouse, Wacom, MSI lighting, etc).

3. **Native messaging or a tiny native helper**. Skip WebHID entirely. Bundle a small native exe or use a Chrome native-messaging extension that talks raw `WriteFile`/`ReadFile` to mi_01. The other 22 betas proved that raw Win32 HID can write to mi_01 (the firmware just doesn't respond to the gen-1 IDENTITY_PACKET protocol on most response paths) — but with the 0x55 0xA1 commands now known from the USBPcap capture, raw Win32 *might* work end-to-end. Worth re-testing with the gen-2 opcodes specifically.

4. **Document as a limitation**. Tester downloads our beta.21 prerelease build from the GitHub releases page and runs it. Beta.21 still works for the "just connect and use" path; later beta features (actuation sync, mode toggles) won't be available to them. Not ideal but unblocks them while we hunt for the real fix.

5. **Wait for Chrome to fix it**. Not actionable but worth noting that this might self-resolve in a future WebView2 update.

## Current code state — what's in the tree

`git status` is clean. Five new commits since the beta.22 handoff:

- `1f5f7ff` beta.23 — tight filter + collection-aware reconnect + forget() (later partially walked back)
- `ade44af` beta.24 — post-pick validator + once-per-session consent + loose picker filter
- `8930510` beta.25 — OEM hardware early-exit (skip standard + alt-probes for VID 0x19F5)
- `11bc82b` beta.26 — tight picker filter (restored) + pre-picker `getDevices()` logging

**What's NOT been touched (don't change without thinking):**
- Gen-1 detection + protocol (KeyboardSpecs, Packets, KeyboardModels, FirmwareCapabilities for non-OEM)
- Profile sync for gen-1 (`WriteFullProfile` path — unchanged)
- Auto-updater / InstallationManager / canonical install
- KeyboardView (recent rebuild, working)
- App CLI flags

## What's NOT changed in the WebHID send path since beta.21

This is important context for the "are we on the right page?" question Daniel asked tonight. Diff `git diff 6d8068c..HEAD -- WpfApp/WebHid/WebHidBridgeHtml.cs` — the `sendReport` function body is bit-for-bit identical to beta.21. The detection flow (`C# TryGen2WebHidDetection` → `channel.WriteAndPoll` → `transport.SendReportAsync` → `bridge sendReport`) is the same code. My five betas of changes are all *additive safety rails*:

- post-pick validator (catches bad picks before they loop)
- `forget()` API (cleans up bad permissions)
- collection-aware reconnect (skips bad permissions on launch)
- once-per-session consent (no more re-prompt loop)
- skip standard + alt-probes for VID 0x19F5 (cheaper, leaves mi_01 untouched)
- tight picker filter (beta.26, diagnostic)
- pre-picker `getDevices()` logging (beta.26, diagnostic)

None of these regress beta.21. If beta.21's tester had run beta.26, they'd have hit the same successful detection path.

## Known minor follow-ups (if you find time)

- JS `sendReport` still has a latent bug: when `declaredIds.length === 0`, the fallback comment claims "Chromium accepts any sendReport" but the beta.22 log proves Chromium rejects with "Failed to write the report". Should fall back to `sendReport(0, dataBytes)` in that case too. Doesn't help *this* tester (whose picked device has all reports stripped), but would be a strict improvement for future users. See `WpfApp/WebHid/WebHidBridgeHtml.cs` sendReport().
- The empty `bgu11kc7a.output` artifact in the worktree's claude task dir from tonight (the failed initial beta.24 release-create — HTTP 404 on asset upload). Already worked around; just noting.

## Communication style for Daniel — refresher

- Discord forwarding messages: ~5 lines, no theory/background. He relays verbatim.
- Terse responses preferred. No emojis.
- He pushed back hard when I over-confidently shipped beta.23 without thinking through the "only 1 entry in picker" report. Lesson re-learned: **read the user's report literally before designing fixes**.
- "fix" as a user message = "stop explaining, do the thing." Move fast.
- He'll correct sloppy framings ("regression" vs "Chrome behavior change", "user fault" vs "Chrome's WebHID is opaque"). Match his framing back.

## First task for next session

1. Open Discord, check for tester's beta.26 reply.
2. If a `debug (7).log` (or similar) lands in `~/Downloads/` — read it, grep for `pre-picker getDevices()` and `requestDevice returned`, follow the decision table above.
3. If the tester reports a behavior outcome without a log — ask for the log first.
4. If picker was empty: ask for their WebView2 Runtime version (registry path above), and offer them the beta.21 fallback link while you investigate.

Release recipe is unchanged — see `CLAUDE.md` "Cut a Release" section. Don't bump csproj without tagging in lockstep.

## Suggested skills for the next session

- **`verify`** — once the beta.26 log decides between the two outcomes, you'll either be promoting to stable (verify the full happy path on Daniel's hardware before tagging stable) or you'll be testing a fallback approach (verify the user's reproduction).
- **`simplify`** — if beta.26 confirms the fix and we promote to v2.4.2 stable, this is the time to fold the OEM gen-2 detection path into something tidier. Daniel's had this on the deferred-cleanup wishlist since beta.12. Scattered branches across `KeyboardManager.cs`, `ProfileManager.cs`, `Gen2WebHidChannel.cs` could collapse into one cohesive `Gen2OemDetection.cs`, and the alt-probes should be gated behind a `--diagnose` CLI flag (currently always-on, just noise for production users).
- **`graphify`** — not useful here. The codebase is well-documented inline and in `docs/`.
- **`handoff`** — at end of next session, recurse on this. The pattern is working.

## Release recipe reminder (for promoting to v2.4.2 stable when ready)

1. Bump `<Version>`, `<FileVersion>`, `<AssemblyVersion>` in `WpfApp/WpfApp.csproj`
2. Commit + push the bump on `main`
3. `git tag vX.Y.Z && git push origin vX.Y.Z`
4. `dotnet publish WpfApp/WpfApp.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true`
5. `cp "WpfApp/bin/Release/net8.0-windows/win-x64/publish/DrunkDeer Control.exe" "WpfApp/bin/Release/net8.0-windows/win-x64/publish/DrunkDeer-Control.exe"` (hyphen — release asset filename, don't change it)
6. `gh release create vX.Y.Z <path-to-exe> --title "..." --notes "..."` (omit `--prerelease` for stable)

Watch out: tonight the asset upload silently 404'd once. If `gh release view` shows `isDraft: true` and `assetCount: 0`, the asset failed to attach — delete the draft and re-run.
