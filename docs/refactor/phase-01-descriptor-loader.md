# Phase 1 — Descriptor loader + record types

**Status**: pending (gated on Phase 0)
**Hardware**: A75 Pro 0x0017 for sanity-check at end
**Estimated effort**: half a day
**Dependencies**: Phase 0 (needs `Driver/Descriptors/` directory to exist)

## Goal

Add the C# code that loads `descriptors/keyboards/*.json` + `descriptors/layouts/*.json` from disk, parses them into typed records, and exposes a resolver API. **Runs alongside the existing `FirmwareCapabilities.Resolve` path.** Does not replace anything yet. Does not change any sync behavior.

The point is to validate the schema against the C# type system and prove the loader works before any dispatch site depends on it.

## Deliverables

1. **Record types** matching ADR-0001 and ADR-0002 schemas. Live in `Driver/Descriptors/`:
   - `Descriptor.cs` — top-level model record (modelName, identity, layout reference, firmwareVariants)
   - `FirmwareVariant.cs` — per-variant record (version, protocolFamily, wireFormat, capabilities, evidence registry, then either `opcodes` for gen-1-alphabet OR `envelope/subCommands/memoryMap/keyTriggerEntry` for extended-gateway)
   - `Layout.cs` — layout record (name, keyCount, keyNames array)
   - `EvidenceEntry.cs` — `{kind, owner?, path?, date, scope[]}`
   - Whatever supporting enums and nested records the schema requires

2. **Loader** at `Driver/Descriptors/DescriptorLoader.cs`:
   - `LoadAll(string descriptorsRootPath)` → returns `IReadOnlyList<Descriptor>`
   - Reads every `keyboards/*.json` + the layouts they reference
   - Validates JSON against the schema (use `System.Text.Json` + record deserialization; JSON Schema validation can come later)
   - Throws a typed `DescriptorLoadException` with file path + line/column on parse failures

3. **Resolver** at `Driver/Descriptors/DescriptorResolver.cs`:
   - `Resolve(int typeCode, ushort firmwareVer) → (Descriptor, FirmwareVariant)?`
   - Mirrors the API shape of `FirmwareCapabilities.Resolve` so substitution at dispatch sites is mechanical later
   - For now: same input/output semantics, no extra behavior

4. **Wiring in `KeyboardManager`** — load descriptors at startup, store them, log how many loaded. Do NOT route any dispatch through them yet.

5. **Embedded resource or filesystem path** for `descriptors/`. Two options — pick based on what's easiest:
   - Embedded: `descriptors/` becomes an MSBuild `EmbeddedResource` glob in `Driver.csproj`. Loader reads from `Assembly.GetManifestResourceStream`. Self-contained, no path issues.
   - Filesystem: `descriptors/` ships next to the exe via `<Content Include="descriptors\**" CopyToOutputDirectory="PreserveNewest" />`. Easier to debug, hot-reloadable.
   - Recommendation: **filesystem** during development for debuggability. Switch to embedded resources before release if path lookup is fragile.

## Schema → C# type sketch

```csharp
namespace Driver.Descriptors;

public sealed record Descriptor
{
    public required int SchemaVersion { get; init; }
    public required string ModelName { get; init; }
    public required string InternalName { get; init; }
    public string? SiteModelKey { get; init; }
    public required Identity Identity { get; init; }
    public required LayoutRef Layout { get; init; }
    public required IReadOnlyList<FirmwareVariant> FirmwareVariants { get; init; }
}

public sealed record Identity
{
    public required int TypeCode { get; init; }
    public required IReadOnlyList<byte> TypeBytes { get; init; }  // length 3
    public required IReadOnlyList<int> Pids { get; init; }
}

public sealed record LayoutRef
{
    public required int KeyCount { get; init; }
    public required string KeyNamesRef { get; init; }
    // Resolved at load time; not in JSON.
    public IReadOnlyList<string>? KeyNames { get; init; }
}

public abstract record FirmwareVariant
{
    public required string Version { get; init; }
    public ushort? VersionNumeric { get; init; }
    public required string DisplayLabel { get; init; }
    public required IReadOnlyList<EvidenceEntry> VerifiedOn { get; init; }
    public abstract string ProtocolFamily { get; }
}

public sealed record Gen1AlphabetVariant : FirmwareVariant
{
    public override string ProtocolFamily => "drunkdeer-gen1-alphabet";
    public required Gen1WireFormat WireFormat { get; init; }
    public required Capabilities Capabilities { get; init; }
    public required IReadOnlyDictionary<string, OpcodeSpec> Opcodes { get; init; }
}

public sealed record ExtendedGatewayVariant : FirmwareVariant
{
    public override string ProtocolFamily => "drunkdeer-extended-gateway";
    public required GatewayWireFormat WireFormat { get; init; }
    public required IReadOnlyDictionary<string, SubCommandSpec> SubCommands { get; init; }
    public required MemoryMap MemoryMap { get; init; }
    public required KeyTriggerEntrySchema KeyTriggerEntry { get; init; }
    public required Capabilities Capabilities { get; init; }
}
```

The `JsonConverter` for `FirmwareVariant` dispatches on the `protocolFamily` field. `System.Text.Json` supports this via `[JsonPolymorphic]` + `[JsonDerivedType]` attributes since .NET 7.

## Test surface

Phase 1 should land with at least these tests (a `Driver.Tests/` project — create it as part of this phase if it doesn't exist):

1. `LoadAll` produces a non-empty list for the current `descriptors/` directory.
2. The A75 Pro descriptor's `firmwareVariants` count matches the JSON's count (4 at time of writing).
3. The OEM variant deserializes into `ExtendedGatewayVariant`, the other three into `Gen1AlphabetVariant`. Polymorphism works.
4. Layout reference resolution: `LayoutRef.KeyNames` is populated after load and has length 126.
5. `DescriptorResolver.Resolve(750, 0x0017)` returns the A75 Pro 0x0017 variant.
6. `DescriptorResolver.Resolve(750, 0x9999)` (unknown firmware) returns the closest variant or null with a documented fallback rule.
7. `DescriptorResolver.Resolve(99999, 0)` (unknown TypeCode) returns null.

## Acceptance criteria

- All tests above pass.
- `dotnet build` clean. No new warnings.
- Startup log line: `Descriptors loaded: 1 model, 4 variants` (or whatever count is current).
- App launches and detects A75 Pro 0x0017 normally — the new code path is loaded-but-unused.
- No behavior change to the user.

## Risks and rollback

- **Risk**: JSON schema mismatch between ADR-0001/0002 and what `A75Pro.json` actually contains. **Mitigation**: this phase will find them by definition; fix the JSON or the C# records to match before merging.
- **Risk**: `EmbeddedResource` path drift breaks dev-vs-published runs. **Mitigation**: filesystem path during dev, defer embedded mode.
- **Rollback**: revert the commit. No dispatch site depends on the new code yet, so revert is safe.

## When this is done

1. Mark Phase 1 `DONE ✓ <PR-link> <date>` in [README.md](README.md).
2. Delete this file.
3. Promote phase-2 from `later-phases.md` into its own detailed file.
