# Refactor plan

Living plan for the descriptor-driven refactor. Each phase is one file in this directory. Phases ship as standalone PRs back to `main`; each PR closes its phase's file (delete or mark `DONE`).

## North star

A data-driven driver where each `(keyboard model, firmware version)` is described by a JSON descriptor. Driver code becomes an interpreter of descriptors. Adding a new keyboard = drop a descriptor in the catalog. See [/CONTEXT.md](../../CONTEXT.md), [docs/adr/0001-descriptor-schema.md](../adr/0001-descriptor-schema.md), [docs/adr/0002-protocol-families.md](../adr/0002-protocol-families.md).

Session that produced this plan: [docs/2026-05-27-refactor-plan-session.md](../2026-05-27-refactor-plan-session.md).

## Status table

Phases run in numerical order unless a dependency arrow says otherwise. "Hardware" column flags whether the phase needs on-keyboard verification.

| # | Phase | File | Hardware | Status |
|---|---|---|---|---|
| 0 | Restructure `Driver/` into subdirectories (file moves only) | [phase-00-restructure.md](phase-00-restructure.md) | none | **next** |
| 1 | Descriptor loader + record types | [phase-01-descriptor-loader.md](phase-01-descriptor-loader.md) | A75 Pro 0x0017 | pending |
| 2 | Migrate first dispatch site (`BuildKeyPointBatch`) to descriptor | [later-phases.md#phase-2](later-phases.md#phase-2) | A75 Pro 0x0017 | pending |
| 3 | Delete `Packets.cs::BuildPackets` dead code | [later-phases.md#phase-3](later-phases.md#phase-3) | none | pending |
| 4 | Migrate remaining gen-1-alphabet dispatch sites | [later-phases.md#phase-4](later-phases.md#phase-4) | A75 Pro 0x0017 | pending |
| 5 | Build `drunkdeer-extended-gateway` interpreter | [later-phases.md#phase-5](later-phases.md#phase-5) | tester-B OEM (pcap exists) | pending |
| 6 | Write descriptors for remaining keyboards (G65, A75, Ultra, Master, etc.) | [later-phases.md#phase-6](later-phases.md#phase-6) | partial (see file) | pending |
| 7 | Delete legacy switch statements (`ResolvePrecision`, `KeyboardLayoutResolver`, etc.) | [later-phases.md#phase-7](later-phases.md#phase-7) | regression | pending |
| 8 | Onboard the existing `roadmap.md` 8-phase plan (MVVM, ProfileManager split, test project) | [later-phases.md#phase-8](later-phases.md#phase-8) | — | pending |

## Working rules

- **One phase = one PR.** Don't bundle. Bundling makes regressions hard to bisect.
- **Restructure (Phase 0) is mechanical.** File moves + namespace updates + nothing else. If you find yourself "improving while moving," stop and split.
- **Descriptor-related changes go on `refactor/foundations` worktree** until the descriptor loader is stable. After that, work directly on feature branches off `main`.
- **A75 Pro 0x0017 must keep working after every phase.** It's the user's keyboard. A regression there blocks the next phase.
- **Phases that ARE done get marked `DONE` in the status table above** with PR link and date. The detail file gets deleted (history is in git). Promoted phases from `later-phases.md` move to their own file when active.

## Hardware verification policy

- **Verified-on-hardware**: skdes confirms on A75 Pro 0x0017 after the PR lands.
- **Verified-by-pcap**: tester-B's USBPcap captures cover the OEM extended-gateway case. Re-verify against `~/Downloads/newestthing.pcapng` / `tester-B/usb2.pcapng` when phase 5 ships.
- **Unverifiable from this codebase**: A75 Ultra (TypeCode 756), A75 Master (757), some G75/G60 variants. These keyboards get descriptors marked `tier: Beta` with `verifiedOn: js-static` only. Hardware verification waits for owners to opt into testing.

## When to re-grill

The grill produced the schema and the north star. Don't re-grill unless:

- A new architectural fork appears (e.g. "should the descriptor loader be its own .csproj?" — yes, that should be grilled if it surfaces).
- Hardware testing reveals a fact that breaks ADR-0001 or ADR-0002 (e.g. another protocol family).
- You find yourself in a scope question that the existing artifacts don't answer.

Most "what should I do next" questions are answered by reading the status table above.
