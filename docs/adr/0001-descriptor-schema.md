# ADR 0001 — Data-driven descriptor schema for keyboard support

**Status**: Accepted (2026-05-27)

**Context**

The DrunkDeer-Control codebase currently dispatches per-keyboard behavior through scattered switch statements (`FirmwareCapabilities.ResolvePrecision`, `KeyboardModels.FindByTypeBytes`, `KeyboardLayoutResolver`) and per-dialect builder methods (`Packets.cs::BuildPacketKeyPoint{Legacy,HighPrec}`). Adding a new keyboard or firmware requires touching multiple C# files and getting the dispatch sites in sync.

The refactor goal (see `MEMORY.md::goal-data-driven-driver`) is to restructure the driver so each (keyboard model, firmware version) is described by a **descriptor record**, and the driver becomes an interpreter of those records. Adding a new keyboard = drop a descriptor file in, no C# changes.

This ADR locks in the descriptor schema's structural choices. The schema will iterate as we learn (`schemaVersion: 1` exists for this purpose), but these choices are load-bearing enough to merit an ADR.

**Decision**

1. **One descriptor file per keyboard model**, located at `descriptors/keyboards/{ModelName}.json`. All firmware variants for that model live inside that file as an array. Rationale: matches the user's mental model ("A75 Pro is a thing, gen-1 and gen-2 are firmware on it"); reduces file count; keeps related variants visually adjacent for review.

2. **Variants are fully self-contained, no inheritance.** Rejected the `inheritsFrom` shortcut despite the redundancy cost. Rationale: the user vibe-coded the project and needs to read variants without resolving inheritance chains. Most descriptors will be auto-extracted, so redundancy is cheap to produce. Diffing tools work cleanly on flat JSON. If a future schema version genuinely benefits from inheritance, we can introduce it then.

3. **Opcodes are structured objects with named fields**, not template strings. Each opcode entry has the form `{ "opcode": "B6", "subOp": "01 00", "fields": [...], "chunking": [...] }`. Rationale: validatable via JSON Schema; programmatically generatable from extracted data; no template parser needed at runtime; easier for tooling (Claude, future contributors) to read and modify.

4. **Layout key-name arrays live in separate files** under `descriptors/layouts/{LayoutName}.json`, referenced by `keyNamesRef` from the descriptor. Rationale: 126-element arrays bloat descriptor files; some keyboards share layouts (e.g. A75 ANSI vs A75 ISO are largely the same); single source of truth for each layout.

5. **Per-feature evidence registry** (`verifiedOn: [{kind, ...}]`) rather than a single `tier` field. Each capability and opcode entry can carry its own evidence list, kinds being `hardware`, `har`, `usbpcap`, `js-static`. Rationale: this is the user's Verified/Beta/Unknown problem solved by construction. Per-feature trust matches reality — RGB may be verified while RDT is unverified on the same firmware.

6. **`siteModelKey` field preserved** even when null, to be populated when upstream-name → our-model mapping is resolved. Rationale: minimal cost, needed for future catalog-refresh tooling that scrapes the keybord.net.cn per-model JSON chunks.

7. **JSON, not JSONC/JSON5/YAML**. Rationale: best tooling support, simplest validation pipeline, no parser ambiguity. Comments live in adjacent README/ADR documents.

**Consequences**

- One-file-per-model means model files will grow as firmware variants accumulate. Splitting to one-file-per-variant is a future migration if files become unwieldy.
- Self-contained variants will produce visibly redundant JSON. Acceptable; extractor tooling generates it, humans rarely hand-author full descriptors.
- Structured opcodes require a runtime interpreter that walks the structured fields to produce wire bytes. Worth the upfront cost; replaces the current per-builder code in `Packets.cs`.
- Per-feature evidence registry means the trust-tier UI gets more granular than today's single pill. May need a UI follow-up — out of scope for this ADR.
- `descriptors/` becomes a new top-level directory in the repo. Build process must ship it as a resource for the runtime to load.

**Alternatives considered**

- *Flat per-(typeCode, firmware) records*: rejected because firmware variants for one model share most fields; per-model nesting is the user's mental model.
- *Templated opcode strings* (`"B5 00 1E {turbo} {rt}"`): rejected because the parser cost outweighs the readability win, and JSON-Schema validation gets weird with string templates.
- *Inline layouts in descriptors*: rejected; bloats files and prevents layout sharing.
- *YAML / JSONC for descriptors*: rejected; less tooling, more parser ambiguity. Comments live in this ADR and a `descriptors/README.md` instead.
