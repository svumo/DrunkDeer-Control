# Session handoff — 2026-05-27 — Refactor plan grill

Companion to [docs/2026-05-27-session.md](2026-05-27-session.md) (RGB lighting work, earlier in the day). This session was a long grill-with-docs run designing the next major refactor's north star, schema, and worked example. The chat got long; this doc compresses it so the next session can continue cold.

## TL;DR

The next major refactor's organising principle is now defined: **a data-driven driver where each `(keyboard model, firmware version)` is described by a descriptor record. Adding a new keyboard = drop a descriptor in the catalog. No per-keyboard C# code.**

Schema is locked in two ADRs, validated against a worked example for A75 Pro spanning four firmware variants (including the gen-2 OEM extended-gateway case). Migration plan, the driving skill, and remaining keyboards are open.

## North star

See [memory/project_drunkdeer_refactor_goal.md](../../.claude/projects/C--Users-skdes-Documents-github/memory/project_drunkdeer_refactor_goal.md) (auto-loaded).

Short version: descriptors are data; the driver becomes their interpreter. Refactor success is measured by legibility ("I can read it without an LLM") which downstream delivers 10-min keyboard onboarding, contributor-friendliness, and correctness across the whole matrix.

## Locked decisions — read these before doing anything

| Artifact | What it locks in |
|---|---|
| [CONTEXT.md](../CONTEXT.md) | Glossary. Gen-1/Gen-2 site vs firmware (orthogonal!), Descriptor, Protocol families, Wire-format dialects, Trust tiers (legacy). |
| [docs/adr/0001-descriptor-schema.md](adr/0001-descriptor-schema.md) | One-file-per-model, structured opcodes (not template strings), separate layout files, evidence registry per feature, JSON, no inheritance. |
| [docs/adr/0002-protocol-families.md](adr/0002-protocol-families.md) | Two-shape schema dispatched on `protocolFamily` per variant: `drunkdeer-gen1-alphabet` (opcodes map) vs `drunkdeer-extended-gateway` (envelope + sub-commands + memoryMap + keyTriggerEntry). |
| [descriptors/README.md](../descriptors/README.md) | Directory layout, "how to add a new keyboard" outline. |
| [descriptors/layouts/A75Pro.json](../descriptors/layouts/A75Pro.json) | 126-key layout shared across A75 Pro firmware variants. |
| [descriptors/keyboards/A75Pro.json](../descriptors/keyboards/A75Pro.json) | The worked example. Four variants: 0x0017 (gen-1, hardware-verified), 0x0009 (gen-1, inherited), gen2-pending-friend-A (family unresolved), oem-1.7 (extended-gateway, USBPcap-verified). Demonstrates that the schema accommodates both protocol families. |

User decisions that are settled and should not be relitigated:

- Refactor lives in DrunkDeer-Control repo. **Not** structured around `deerios/DrunkDeerSDK` (loose parallel collab, not architectural).
- Platform decision (Avalonia / Tauri / stay-WPF) deferred until after the protocol core is descriptor-driven. Premature otherwise.
- Artifacts stay at canonical locations (`CONTEXT.md` root, `docs/adr/`, `descriptors/`). **No** `refactor/` junk-drawer directory.

## Landmines — corrections the previous session made the hard way

These will trip a fresh agent if not surfaced upfront. Listed in roughly the order they bit us.

1. **The dialect-dispatch refactor is already half-built.** [Driver/FirmwareCapabilities.cs:298](../Driver/FirmwareCapabilities.cs) has the `WirePrecision` enum, [FirmwareCapabilities.cs:178](../Driver/FirmwareCapabilities.cs) has the per-model dispatcher, [Packets.cs:924](../Driver/Packets.cs) has `BuildKeyPointBatch` consuming it. The live sync path ([Packets.cs:1148](../Driver/Packets.cs)) uses the dispatcher. The "old path" `BuildPackets` at [Packets.cs:951](../Driver/Packets.cs) has **zero callers** — it's dead code. Delete it; don't refactor around it.

2. **There is no upstream `index.json` catalog.** The user remembered "index.json" from the official web driver; what actually exists are per-model JSON files lazy-loaded as Webpack chunks (`BLA75.json`, `K60.json`, `K65.json`, `K68.json`, `K68_JIS.json`, `K68_Ultra_UK.json`, `K75.json`, `K75M.json`, `K80.json`, `X60P.json`, `full_keyboard.json`). Mapping between upstream model names and the codebase's marketing names is unresolved. The `siteModelKey` field in descriptors exists to record this when we figure it out.

3. **The gen-2 site got rewritten in the 5 days before this session.** `tools/captures/keybord-net-cn/index.QC8Mvgui.js` (Vite/Vue, extracted 2026-05-22) is no longer the current upstream. The new bundle is Next.js — see the friend-A HAR in `~/Downloads/drunkdeer.keybord.net.cn (1).har` showing `_next/static/chunks/...` and `webpackChunk_N_E`. Static-analysis tooling built against the Vite source needs updating. The wire protocol itself is almost certainly unchanged (firmware doesn't care about JS frameworks).

4. **HAR files do not contain HID/WebHID wire bytes.** HARs only capture HTTP/WebSocket traffic. WebHID is a separate browser API that does NOT appear in HAR exports. To capture wire bytes you need USBPcap/Wireshark at the kernel level (example: `tools/captures/firmware-flash-v2.3.4.pcapng`). The friend-A HAR is useful for finding the upstream catalog chunks and the current bundle hash — NOT for protocol verification.

5. **Three protocol families exist, not two.** The earlier `docs/keyboard-protocol-gen2.md` writeup only covered the easy case (gen-2 firmware = same opcodes, 65-byte HID instead of 64). The OEM gen-2 firmware (VID `0x19F5`, A75 Pro firmware 1.7, USBPcap-verified 2026-05-25) speaks a completely different command alphabet: the `0x55 / 0xAA` extended gateway with envelope structure, transport-computed checksums, memory-addressed profile blocks, and 8-byte `KeyTriggerEntry` records. Source of truth: [docs/gen2-wire-format-confirmed.md](gen2-wire-format-confirmed.md). Implementation: [Driver/PacketsGen2.cs](../Driver/PacketsGen2.cs). The schema's `protocolFamily` field discriminates these.

6. **The user vibe-coded the entire project solo with Claude.** This has two consequences:
   - **Code comments are LLM-generated rationalisations**, not user intent. Treat them as evidence about what the code does, not as authority on why it was done. Trace decisions through the code itself.
   - **The user does not have a deep mental model of their own codebase.** When you ask them to choose between technical schema/implementation options, they will often say "you decide" or "I have no idea." That's an honest answer — make the call yourself, justify briefly, save it durably (ADR or CONTEXT.md). Don't keep asking. (Captured in [memory/feedback_decide_technical_when_user_defers.md](../../.claude/projects/C--Users-skdes-Documents-github/memory/feedback_decide_technical_when_user_defers.md).)

7. **Use the `AskUserQuestion` tool, not markdown choice blocks.** Formatted markdown with "A) ... B) ... C) ..." doesn't render as clickable buttons. The user will type prose instead and the response will be ambiguous on which option they picked. (Captured in [memory/feedback_use_askuserquestion_tool.md](../../.claude/projects/C--Users-skdes-Documents-github/memory/feedback_use_askuserquestion_tool.md).)

8. **The `firmware-support-matrix.md` doc says "byte-identical protocol across firmwares."** That was written before the 2026-05-22 keybord.net.cn extraction. It is **wrong**. [docs/protocol-findings-keybord-net-cn.md](protocol-findings-keybord-net-cn.md) supersedes it for the dialect question, and [docs/gen2-wire-format-confirmed.md](gen2-wire-format-confirmed.md) supersedes it for the extended-gateway case. The matrix doc should be updated/retired as part of the refactor.

## Open work items — the agenda for next session(s)

### 1. Migration plan — written, at [docs/refactor/](refactor/)

Lives in `docs/refactor/` as a directory of phase files (not a single doc), so each phase can be tracked / promoted / deleted independently:

- [docs/refactor/README.md](refactor/README.md) — status table, working rules, when-to-re-grill guidance. Read first.
- [docs/refactor/phase-00-restructure.md](refactor/phase-00-restructure.md) — **next action**. Mechanical file moves to break Driver/'s flat 21-file dir into subdirectories (Detection / Transport / Protocol/Gen1Alphabet / Protocol/ExtendedGateway / Descriptors / Domain / Diagnostics). No logic changes. Required so Phase 1 lands in a clean home.
- [docs/refactor/phase-01-descriptor-loader.md](refactor/phase-01-descriptor-loader.md) — C# record types matching ADR-0001/0002, loader, resolver. Runs alongside existing FirmwareCapabilities; doesn't replace anything yet.
- [docs/refactor/later-phases.md](refactor/later-phases.md) — phases 2 through 8, stubbed. Promoted to their own files when activated.

Hardware gating, working rules ("one phase = one PR", "A75 Pro 0x0017 must keep working after every phase"), and re-grill criteria are documented in the README. Don't re-derive them.

### 2. Build the skill that drives the refactor — original ask

After the plan exists, write a small `~/.claude/skills/drunkdeer-refactor-step/SKILL.md` (~80 lines) that:

- Opens `docs/refactor-plan.md`
- Finds the next unchecked phase
- Runs it on a `refactor/foundations` worktree
- Reports back

This is the skill the user originally asked for in this session. **Do not build it before the plan exists** — it would just be a vague "figure out what to do next" wrapper without a plan to drive.

### 3. Resolve friend-A's VID/PID

The descriptor variant `gen2-pending-friend-A` in [descriptors/keyboards/A75Pro.json](../descriptors/keyboards/A75Pro.json) is parked with `protocolFamily: null` pending this. Action items are listed inline in that variant.

Practical first step: get friend-A to run `Get-PnpDevice -PresentOnly | Where-Object FriendlyName -like '*DrunkDeer*' | Format-List` on Windows. VID `0x352D` → `drunkdeer-gen1-alphabet`; VID `0x19F5` (or other OEM VID) → `drunkdeer-extended-gateway`.

### 4. Ultra/Master question — won't fix from code alone

Per [FirmwareCapabilities.cs:202-216](../Driver/FirmwareCapabilities.cs) and [docs/protocol-findings-keybord-net-cn.md](protocol-findings-keybord-net-cn.md): the JS bundle dispatches A75 Ultra (TypeCode 756) and A75 Master (757) to `NewHighPrec` (0xFD packets, 2 bytes/key, mm × 200). Current code conservatively routes them to `OldHighPrec` because user has no Ultra/Master hardware to verify. Result: Ultra/Master users have a 2.0mm AP cap when they could have 3.3mm, and possibly other features mis-rendered.

Cannot be fixed from this codebase alone. Needs either: an Ultra/Master owner running a debug build that uses `OverridePrecision = NewHighPrec`, or a USBPcap from such an owner doing a sync on the official tool. Park as a tier-1 user-action item; not a coding task.

## Suggested skills for the next session

- **No skill at the start** — pick up the agenda in this doc directly. Don't re-invoke `grill-with-docs` unless a genuine new design fork appears.
- **`grill-with-docs`** — re-invoke ONLY if a new architectural decision surfaces (e.g. "should the descriptor loader be its own C# project or live in Driver/?"). The grill is heavy; don't run it for small questions.
- **`verify`** (when phase 1-3 ship) — run the actual app after each migrated dispatch site to confirm A75 Pro 0x0017 still works. Critical because the user's primary keyboard going dead is the worst possible regression.
- **`code-review`** — after each phase PR, run code-review on the diff. The refactor is large; small reviews catch problems early.
- **`handoff`** — at the end of the next session, write another handoff doc to `docs/2026-05-28-session.md` (or whatever date). Keep the chain.

Skills to **avoid**:
- **`improve-codebase-architecture`** — would re-derive what this session already locked in. Skip.
- **`init`** — CLAUDE.md exists. Skip.

## How to pick up cold

1. Read this doc.
2. Read [CONTEXT.md](../CONTEXT.md) (~5 min).
3. Skim [docs/adr/0001-descriptor-schema.md](adr/0001-descriptor-schema.md) and [docs/adr/0002-protocol-families.md](adr/0002-protocol-families.md) (~5 min each).
4. Open [descriptors/keyboards/A75Pro.json](../descriptors/keyboards/A75Pro.json) and read the four variants. That's the concrete artifact everything else describes.
5. Confirm the auto-memory files loaded (they're in `~/.claude/projects/C--Users-skdes-Documents-github/memory/`).
6. Start on item 1 of the agenda — write `docs/refactor-plan.md`.

Do not start by re-grilling the user on what the goal is. The goal is settled (data-driven descriptor-based driver). If you find yourself re-deriving this, re-read this doc.

## Out-of-scope reminders

These were explicitly considered and rejected this session — don't suggest them again without new information:

- "Should we use Avalonia / Tauri / web for the UI?" — Deferred until protocol core is descriptor-driven.
- "Should we structure the refactor around the DrunkDeerSDK collaboration?" — No. Loose parallel collab. Build DrunkDeer-Control for its own users.
- "Should planning artifacts live in a `refactor/` directory?" — No. Permanent artifacts (CONTEXT.md, ADRs, descriptors/) stay canonical.
- "Should we inherit between firmware variants in the descriptor schema?" — No, per ADR-0001. Self-contained variants even at the cost of redundancy. Extractor tooling produces the redundancy, humans rarely hand-author.
- "Should opcodes be encoded as template strings?" — No, per ADR-0001. Structured objects with named fields.
- "Should we publish descriptors upstream as an `index.json`?" — Out of scope. Maybe a follow-up after the refactor.
