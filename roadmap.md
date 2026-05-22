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
