# DrunkDeer Control — Roadmap

Forward-looking list of work not yet started. Items here are *ideas with
some shape*, not commitments — they need a real plan + design call before
implementation. The active plan for the next major refactor is tracked
separately in the maintainer's local plan store. When items here graduate
to "in progress" they should move into a real plan file and disappear
from this list.

---

## Internationalization (i18n)

**Chinese version of the app.** DrunkDeer's official tooling supports
both Chinese and English, and a non-trivial portion of the userbase is
Chinese-speaking. Today every UI string is hardcoded into XAML or
code-behind in English.

**Scope** (rough):
- Extract ~200-400 user-facing strings from `MainWindow`,
  `KeyboardPerformanceView`, every drawer/dialog, `Settings`, tooltips,
  banner copy, etc. into a `.resx` resource bundle.
- Wire XAML bindings via `{x:Static r:Strings.Key}` or a markup
  extension; same for any user-visible code-behind strings.
- Add a language toggle in Settings (or detect system locale on first
  launch with a one-time override prompt).
- Chinese translations need native-speaker review — literal translation
  of UX copy ("this software is unsupported", "your firmware is too
  old") gets tone wrong easily.
- Test CJK glyph rendering in custom controls (keycap labels, banner
  text, the long-form Known Issues entries).

**Sequencing**: lands more cleanly *after* the Phase 6-7 MVVM
extraction in the planned refactor — translating data-bindings is
easier than chasing scattered code-behind string literals.

**Soft-launch option**: just translate the firmware-too-old modal +
Known Issues window in advance, behind no Settings toggle, as a
gesture before the full localization framework lands. ~5-10 strings,
needs one native-speaker review pass.

**Translation source already exists — borrow from the official tool.**
The drunkdeer.keybord.net.cn JS bundle ships ~600 unique UI strings
professionally translated into 7 locales (en, cn, tw, jp, kr, fr,
de). For DrunkDeer-vocabulary strings (key names, RGB mode names,
button labels) we can lift these translations directly — they're
the source's own. Saves the native-speaker-review cost on the
bulk of the strings. See
[docs/protocol-findings-keybord-net-cn.md](docs/protocol-findings-keybord-net-cn.md)
"official tool's locale catalog" section for extraction pattern.

---

## Refactor (planned)

The next major refactor is documented in the maintainer's plan store —
8 phases sequenced to avoid colliding with active feature branches.
Gated on:
1. `feature/rdt-pairs` merging to main (firmware-too-old work, in flight).
2. Hardware verification of that merge.

Then the refactor begins on a long-lived `refactor/foundations`
worktree, with phases shipping as standalone PRs back to main. Phases
cover: Driver foundation + first test project, Settings persistence
cleanup, Packets.cs decomposition with golden-file protocol tests,
sync writer extraction, ProfileManager split, MVVM extraction for the
two god-controls (KeyboardPerformanceView and MainWindow), and final
cross-cutting cleanup + optional CI.

---

## Other deferred items

- **Real telemetry-worker CI/CD** — currently deployed manually via
  Wrangler. A GitHub Action that deploys on `telemetry-worker/` path
  changes would close the loop.
- **Firmware-changed banner demo flag** — the modal demo flag
  (`--firmware-too-old-demo`) lets the upgrade modal be tested without
  hardware. The equivalent for the in-app amber "firmware changed
  since last connect" banner would require faking `KeyboardManager`'s
  connected device, which is bigger than a CLI flag (HidDevice is
  abstract). Useful as part of Phase 4 of the planned refactor
  (DeviceSpecProber extraction) since that's where the seam would land.
- **Schema versioning for profiles** — Driver `Profile.cs` has no
  schema version field. Migrations between formats are currently
  forward-compat-by-luck via optional fields. Once we need a breaking
  format change, this becomes urgent.

---

## In-app firmware downgrade (unlock 3.3 mm AP on A75 Pro)

A75 Pro firmware ≥ 0x11 self-reports `oldOpenHighPrecision=true` and
gets capped at AP 2.0 mm. User testing 2026-05-22 confirmed downgrading
to a Legacy-dialect firmware (V2.3.1's A75 Pro fw 0x0008) gives the
full 3.3 mm AP range with feelable difference.

**Protocol fully decoded** 2026-05-23 from a USB packet capture during
a real `DrunkdeerUpdater.exe` flash. Findings live at
[docs/firmware-flash-research.md](docs/firmware-flash-research.md):

- Boot trigger: `[0x04, 0xa5, 0x01, 0x00 × 61]` — one HID output report
  on PID 0x2383 endpoint 0x03, device re-enumerates as PID 0x1101 in
  ~1.3 sec
- Boot-mode protocol: structured 64-byte commands on PID 0x1101
  endpoint 0x04 — `0x31` write chunk, `0x32` status, `0x33` finalize
  with 8-byte CRC/signature
- First write chunk has target flash address `0x0001D000` + length;
  subsequent chunks stream raw firmware bytes
- Captured pcap at `tools/captures/firmware-flash-v2.3.4.pcapng`
  (gitignored, ~14 MB)

**Critical unknown**: the 8-byte CRC in the finalize packet. We don't
know if it's deterministic from firmware bytes or extracted from a
known offset in the `.enc` file. Until that's pinned down, a
downgrade flash can't be safely synthesized — we'd brick a keyboard
on bad CRC.

**Proposed staged implementation** (each gated on user confirmation +
working keyboard between stages):

- **Stage 1**: byte-for-byte pcap replay against the SAME firmware
  version captured. Zero brick risk (we send exactly what the
  official tool sent). Validates the protocol works end-to-end from
  our code.
- **Stage 2**: version-swap replay (V2.3.1's `E15CF7C4.enc` for the
  Legacy-dialect downgrade). Requires figuring out the CRC
  derivation or extraction. Real brick risk if we get it wrong.
- **Stage 3**: parameterised flash with proper `.enc` parsing and
  CRC derivation. UI surface in our app (a button in Options or
  Known Issues).

**Why parked**: each stage requires careful safety review + recovery
testing. Not a "one session" task — needs dedicated focus with a
non-critical test keyboard or a clear recovery plan (the official
updater always works as the fallback). User decision 2026-05-23:
revisit in a future dedicated session.

**Until then**: A75 Pro users wanting 3.3 mm AP use the official
`DrunkdeerUpdater` to downgrade to V2.3.1's firmware. Our app already
detects Legacy dialect post-downgrade and surfaces the higher slider
range automatically.
