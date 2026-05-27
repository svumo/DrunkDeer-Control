# Later phases (2 through 8)

Phases beyond Phase 1, stubbed for orientation only. Each gets promoted to its own detailed file (`phase-XX-...md`) when the previous phase completes. Don't try to detail these speculatively — the right design for Phase 5 depends on what Phase 1 actually shipped.

## Phase 2 — Migrate first dispatch site

Switch `Packets.cs::BuildKeyPointBatch` from `WirePrecision` enum dispatch to descriptor-driven dispatch. The descriptor resolver from Phase 1 returns a `Gen1AlphabetVariant`; the variant's `opcodes` map carries the per-key opcode (`perKeyActuation` / `perKeyDownstroke` / `perKeyUpstroke`). The builder consumes the opcode spec instead of branching on the enum.

Acceptance: A75 Pro 0x0017 still works. Wire bytes captured before/after Phase 2 are byte-identical. (Use existing USBPcap tooling on user's keyboard or compare against a known-good capture.)

Dependencies: Phase 1.
Hardware: A75 Pro 0x0017.

## Phase 3 — Delete `Packets.cs::BuildPackets`

`Packets.cs::BuildPackets` (the older non-dispatching sync method) has **zero callers** as of 2026-05-27. Delete it. Pure cleanup PR. Doubles as a confidence-builder PR — small diff, no risk, ships first thing in the day.

If between now and execution someone *adds* a caller, do not auto-delete — investigate the new caller and either migrate it to `BuildFullProfilePackets` or document why the legacy path is needed.

Dependencies: none in principle, but easier to run after Phase 2 so the migrated path is the only sync path.
Hardware: none.

## Phase 4 — Migrate remaining gen-1-alphabet dispatch sites

After Phase 2 proves the pattern, walk through every other place that dispatches based on TypeCode / firmware / WirePrecision and migrate each one to consume the descriptor. Sites to expect:

- `Packets.cs::BuildCommonSwitchPacket` — uses `ProfileSettings`, no dispatch today; verify if descriptor needs to drive any byte positions.
- `Packets.cs::BuildCreateLwPairsPacket` / `BuildCreateRdtPacket` — pair table builders.
- `Packets.cs::BuildPacketRTPAuthority` + `BuildPacketRTPAuthorityDownload`.
- `Packets.cs::BuildRemapPackets`.
- `KeyboardLayoutResolver` — resolves model → layout. Replace with descriptor.Layout lookup.
- Probably more; grep for `WirePrecision` and `FirmwareCapabilities` usages and triage each one.

Strategy: one dispatch site per PR. Each PR migrates one site, re-verifies on hardware, ships. Pace: 2-3 PRs per session.

Dependencies: Phase 2.
Hardware: A75 Pro 0x0017 for each PR.

## Phase 5 — Build `drunkdeer-extended-gateway` interpreter

Add the second descriptor interpreter that handles `ExtendedGatewayVariant`. Reads `envelope`, `subCommands`, `memoryMap`, `keyTriggerEntry` from the variant and produces wire bytes. Replaces the procedural code currently in `Driver/Protocol/ExtendedGateway/PacketsGen2.cs` (post-Phase-0 path).

This phase needs more design thought than Phases 2-4 because the extended-gateway protocol is more structured (checksums, memory addressing, bit-packed records). The interpreter design call worth grilling: do we generate wire bytes from a "build per-key entry → write chunks → ack" pipeline, or expose lower-level primitives that match the sub-command semantics directly?

Verification: re-run the byte comparison against tester-B's `usb2.pcapng`. Phase ships when the interpreter produces byte-identical output to `PacketsGen2.BuildWriteKeyTriggerChunkSequence`.

Dependencies: Phase 1 (loader), Phase 2 (pattern established).
Hardware: tester-B USBPcap (already captured). No live hardware required for verification — pcap comparison is sufficient.

## Phase 6 — Write descriptors for remaining keyboards

The descriptor catalog currently has only `A75Pro.json`. Write descriptors for:

- A75 (TypeCode 75) — likely `drunkdeer-gen1-alphabet`, dialect Legacy on fw<0x23, OldHighPrec on fw≥0x23
- A75 UK / FR / DE (TypeCode 751)
- A75 Ultra (TypeCode 756) — `drunkdeer-gen1-alphabet`, dialect NewHighPrec per JS bundle, no hardware → `verifiedOn: [js-static]` only
- A75 Master (TypeCode 757) — same caveats as Ultra
- G65 (TypeCode 65) — Legacy dialect
- G75, G60, X60 — likely Legacy, low priority
- KG645U / KG650U / KG650 — currently stubs (tier: Unknown); needs reverse-engineering work before a descriptor is meaningful

Some of these can be lifted from `Driver/KeyboardModels.cs` + `Driver/FirmwareCapabilities.cs` directly (the data exists; just needs translation to the new schema). Others (Ultra/Master/G65) need extra info.

Open question: do we lift the upstream per-model JSONs from the gen-2 site's Webpack chunks to fill gaps? Probably yes for layout data, no for protocol (we don't trust the JS-derived bytes without hardware confirmation).

Dependencies: Phase 1 (schema must be live). Phases 2-5 not strictly required but make this phase more meaningful (descriptors that are loaded but not consumed are dead weight).
Hardware: partial. Owners who own each keyboard verify their own descriptor's accuracy. Catalog-completeness work doesn't need every owner; descriptors can ship as `tier: Beta` and get promoted to `Verified` when owners report success.

## Phase 7 — Delete legacy switch statements

Once every dispatch site is descriptor-driven and every keyboard has a descriptor, delete:

- `Driver/FirmwareCapabilities.cs::ResolvePrecision`
- `Driver/FirmwareCapabilities.cs::VerifiedTable`
- `Driver/FirmwareCapabilities.cs` — the file probably gets deleted entirely; its responsibilities have moved to the descriptor model
- `Driver/KeyboardLayoutResolver.cs` — replaced by descriptor.Layout lookup
- Any per-dialect builder method in `Packets.cs` that's no longer called
- The `WirePrecision` enum if no remaining caller references it

This is the most satisfying phase to ship — the refactor's payoff. Should be a single PR or small handful, with a giant deletion diff.

Risk: a hidden caller of one of the deleted APIs that we missed. Mitigation: grep aggressively before deleting; let the compiler tell you if you missed one.

Dependencies: Phases 2-6 complete.
Hardware: A75 Pro 0x0017 regression test after each deletion.

## Phase 8 — Onboard the existing roadmap.md 8-phase plan

The original 8-phase refactor in [roadmap.md](../../roadmap.md) was about god-object cleanup (Packets.cs decomposition, MVVM extraction, ProfileManager split, sync writer extraction, test project, etc.). With descriptors landing first, that plan now runs on a clean protocol layer.

Most of its phases survive as-written, but some get cheaper:

- "Packets.cs decomposition" is partly resolved by Phase 7 — the file shrinks significantly when legacy dispatch goes away. The remainder (build helpers, packet templates) can decompose into `Driver/Protocol/Gen1Alphabet/` sub-files cleanly.
- "Sync writer extraction" can target the descriptor interpreter, not the old per-builder methods. Cleaner extraction point.
- "MVVM extraction for KeyboardPerformanceView" is unchanged in scope.

Sequencing: do whichever phase blocks active feature work. The descriptor refactor doesn't force any particular order on the existing 8 — it just means each one starts from cleaner ground.

Dependencies: Phase 7 (or in parallel with 5-7, depending on which roadmap.md phase).
Hardware: per-phase (mostly UI work, mostly safe).
