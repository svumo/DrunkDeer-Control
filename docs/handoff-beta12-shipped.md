# Handoff — Beta.12 shipped, awaiting user validation

**Date**: 2026-05-24
**Branch**: `main`
**Last tag**: `v2.4.1-beta.12` (just shipped, awaiting user logs)
**Situation**: forwarded beta.12 to two affected users via Discord, waiting on their next debug logs

## TL;DR for the next agent

We burned through betas 4–12 trying to make DrunkDeer Control work on a specific OEM-relabeled A75 Pro hardware batch (VID `0x19F5` / PID `0xFB5C`, two confirmed users). Beta.12 is the **architectural fix**: embedded WebView2 + WebHID for gen-2 OEM devices, gen-1 unchanged. Now we wait.

**Don't re-litigate the "should gen-1 use WebHID too?" decision.** Daniel asked, we decided no — the UX (picker prompt for every user), perf (postMessage round-trips are 5-10x slower than HidStream.Write), and dependency surface (WebView2 Runtime becomes hard requirement) costs aren't worth it for a problem gen-1 doesn't have. The two paths converge at `IGen2Channel` abstraction so the codebase isn't actually inconsistent.

## The full story lives in three docs — read these first

1. **[gen2-oem-investigation.md](./gen2-oem-investigation.md)** — beta-by-beta investigation record. Hardware fingerprint, per-collection breakdown, what each beta proved/ruled out.
2. **[keyboard-protocol-gen2.md](./keyboard-protocol-gen2.md)** — gen-2 wire protocol notes from the official web driver JS bundle (`%TEMP%/gen2-driver/index.js`, still cached locally).
3. **Beta.12 commit `868322e`** (`git show 868322e`) — detailed commit message walks through every new file and the architecture.

These together contain everything the next session needs. Don't re-derive.

## What beta.12 actually does (high level)

Gen-2 OEM keyboards route through [Driver/Gen2WebHidChannel.cs](../Driver/Gen2WebHidChannel.cs), which forwards to [WpfApp/WebHid/WebHidTransport.cs](../WpfApp/WebHid/WebHidTransport.cs) — a singleton that hosts a hidden `Window` with a `Microsoft.Web.WebView2` control. The WebView2 loads an embedded HTML page ([WpfApp/WebHid/WebHidBridgeHtml.cs](../WpfApp/WebHid/WebHidBridgeHtml.cs)) that uses `navigator.hid` for actual device I/O. C# ↔ JS over JSON-encoded `postMessage`.

**First launch UX**: detection fails through all HidClass.sys paths → `KeyboardManager.Gen2WebHidConsentNeeded` fires → [WpfApp/WebHid/WebHidConsentDialog](../WpfApp/WebHid/WebHidConsentDialog.xaml) pops → user clicks Continue → WebView2 window appears with a "Show device picker" button → user clicks → Chromium picker → user selects keyboard → window auto-hides 1.5s later → keyboard works. Permission persists in `%LocalAppData%\DrunkDeer Control\WebView2`.

**Subsequent launches**: silent reconnect via `navigator.hid.getDevices()`, no UI shown.

**Gen-1 keyboards** (VID `0x352D`): completely unchanged path. WebView2 still initializes in background but never consulted. Smoke-tested locally on Daniel's A75 Pro ANSI — 42-packet sync wrote successfully, zero regressions.

## Working tree state at handoff

`git status` shows ONE uncommitted change:

```
 M WpfApp/App.xaml.cs
```

This is the start of the deferred v2.4.2 cleanup pass: it reverts the force-verbose-logging block to opt-in `--verbose-log`. I started the cleanup, then Daniel paused me to wait for beta.12 user feedback. **Beta.12 release was built BEFORE this change was made**, so the published beta.12 binary still force-verbose-logs.

If we proceed to v2.4.2, this change is fine to keep. If we need to ship beta.13 instead (because beta.12 fails and we need another diagnostic round), revert this with `git checkout -- WpfApp/App.xaml.cs` to keep the verbose-logging on.

## What to do next, by outcome

### Outcome A: "It works" (most likely if WebHID was the right call)

1. Pop the victory message in Discord. Both users have been very patient.
2. **Run the deferred v2.4.2 cleanup pass**:
   - **Critical**: Gate the gen-2 fallback chain on `device.GetMaxOutputReportLength() == 65` in [Driver/KeyboardManager.cs](../Driver/KeyboardManager.cs)::`FindKeyboardCore`. Currently the gen-2 fallback (HidD probe → WebHID detection → consent event) runs for ANY DrunkDeer device whose standard probe fails — including gen-1 with transient failures, adding ~7s of pointless WebHID-reconnect attempts. Daniel flagged this as the real concern. The doc [keyboard-protocol-gen2.md:32-34](./keyboard-protocol-gen2.md) confirms `MaxOutputReportLength==65` is the canonical gen-2 indicator (cleaner than VID-matching since it auto-handles future OEM variants).
   - Force-verbose-logging revert already in working tree — see "Working tree state" above.
   - Gate Strategy A–J diagnostic probes in `KeyboardManager.cs::TryAlternativeIdentityProbes` behind a `--diagnose` CLI flag. Keep the code — invaluable if a new OEM variant surfaces — just don't auto-fire them on every detection failure.
   - Bump csproj to `2.4.2`, tag `v2.4.2`, ship as **stable** (not prerelease) so the in-app updater promotes it to all users.
   - Update [../CLAUDE.md](../CLAUDE.md) "Known Issues" section to mark the gen-2 OEM investigation closed.
   - Update memory `project_gen2_firmware_vid_shift.md` to mark case closed with the WebHID solution noted.
3. **Before tagging v2.4.2**, Daniel explicitly asked to verify gen-1 detection still works on his machine through the **unplug/replug** path — not just the cold-start path the smoke test already covered. Get him to plug-replug his A75 Pro and confirm detection still completes in ~2s (not ~7s, which would mean the WebHID-on-gen-1 issue is still present).

**The cleanup pass + v2.4.2 promotion is ~1 hour of work.**

### Outcome B: Some specific WebHID failure mode

Read the new log carefully — beta.12's WebHidTransport logs are specific:
- `WebHidTransport: WebView2 initialized (userData=...)` confirms WebView2 came up
- `WebHidTransport: bridge ready` confirms the JS bridge loaded
- `gen-2 WebHID: no previously-permitted device found (needs user consent via picker)` is the expected first-time message
- `gen-2 WebHID spec packet: [...]` shows what the device returned (if anything)
- `WebHidTransport: picker connected VID=0x... PID=0x...` confirms the picker flow worked

Common possible failure modes:
- WebView2 Runtime not installed → `EnsureReadyAsync` throws → log shows the exception
- Chromium picker doesn't show the keyboard → would CONTRADICT users' earlier confirmation that drunkdeer.keybord.net.cn works for them → revisit that assumption
- Picker works but sendReport fails → JS bridge error in log
- Identity sent but no response → suggests protocol bytes wrong for THIS OEM firmware (the gen-2 JS bundle uses VID `0x352D` filters; OEM `0x19F5` may run modified firmware)

### Outcome C: WebHID flat-out doesn't work

Unlikely given users' confirmation that the official web driver picker shows their keyboard. Possible nonetheless. Next move would be a much bigger architectural change — rebuild as a Tauri/Electron wrapper around the keyboard's existing web driver page. ~1-2 week effort. **Don't initiate without explicit go-ahead from Daniel.**

## Important context for the next session

- **Daniel's testing pace**: fast iterative questions, prefers tight answers, no emojis. Memory has `feedback_discord_messages.md` for forwarding-to-tester message style: keep them to ~5 lines.
- **Two confirmed affected users**: original tester (forwarded via Daniel's Discord) and "Дмитрий" (Cyrillic Windows username, Win11 22631). Same hardware fingerprint exactly: Serial `2024-04-25` — it's an manufactured batch, not one-offs.
- **The release flow**: see [../CLAUDE.md](../CLAUDE.md) "Cut a Release" section. Commit → tag → `dotnet publish` single-file → rename to `DrunkDeer-Control.exe` (hyphen) → `gh release create`. The in-app auto-updater hits `releases/latest/download/DrunkDeer-Control.exe` so the asset name MUST stay hyphenated.

## First task for next session

1. `cd c:\Users\skdes\Documents\github\DrunkDeer-Control-rdt` then `git status` — verify working tree matches handoff (one uncommitted edit to `WpfApp/App.xaml.cs`).
2. Check Daniel's most recent message for the user log (`debug (13).log` or similar in `~/Downloads/`).
3. If log present: proceed per Outcome A/B/C above.
4. If no log yet: don't do anything — Daniel said "just waiting to hear back from beta release 12". No need to push the cleanup pass without that signal.

## Suggested skills

No specific skill is required to wait. When the log arrives:

- **Outcome A** (WebHID works): cleanup pass is straightforward code work — no skill needed. Release flow documented in CLAUDE.md.
- **Outcome B** (specific WebHID failure): diagnostic work, no skill needed.
- **Daniel verifying unplug/replug detection on gen-1** before promoting: the **`verify`** skill could help drive a real-app interaction sequence.
- **Code-cleanup pass after Outcome A**: **`simplify`** is the right skill — there are now three gen-2 detection paths layered in `KeyboardManager.cs` (existing HidD probe + RawInputReceiver + WebHID) and some can probably be collapsed.

## Pragmatic note

A previous handoff exists at `%TEMP%/handoff-gen2-investigation-20260524-025403.md` from earlier today (mid-investigation, before WebHID). This handoff supersedes it — that handoff's "Outcome A" path was about beta.8; we're now five betas further along with the WebHID architecture in place.
