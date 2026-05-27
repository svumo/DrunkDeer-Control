# Phase 0 — Restructure `Driver/` into subdirectories

**Status**: next
**Hardware**: none
**Estimated effort**: 1-2 hours (mechanical)
**Dependencies**: none

## Goal

Replace the flat 21-file `Driver/` directory with a subdirectory structure that groups files by concern. **No logic changes.** No new functionality. No refactoring of method signatures. Pure file moves + `namespace` updates + `using` updates.

The point is to make subsequent phases navigable. The descriptor loader (Phase 1) lands in `Driver/Descriptors/`; that location should already exist when we get there.

## Current state

Flat directory with 21 source files, mixing detection / transport / protocol / data / diagnostics concerns:

```
Driver/
├── DebugLogger.cs
├── Driver.csproj
├── FirmwareCapabilities.cs       (protocol + capability resolver)
├── Gen2KeyboardChannel.cs        (transport, OEM gen-2)
├── Gen2WebHidChannel.cs          (transport, WebView2 path)
├── HidDeviceExtensions.cs        (transport, gen-1)
├── HidStreamListener.cs          (transport, gen-1)
├── IGen2Channel.cs               (transport interface)
├── IGen2WebHidTransport.cs       (transport interface)
├── KeyboardLayout.cs             (data, visual layout)
├── KeyboardLayoutResolver.cs     (detection)
├── KeyboardManager.cs            (detection, the main entry point)
├── KeyboardModels.cs             (data, catalog)
├── KeyboardSpecs.cs              (protocol, parse identity response)
├── KeyMatrixSlotMap.cs           (data, slot indexing)
├── KeyTriggerEntry.cs            (data, gen-2 OEM record format)
├── Packets.cs                    (protocol, gen-1 alphabet)
├── PacketsGen2.cs                (protocol, extended-gateway)
├── Profile.cs                    (domain model)
├── RawInputReceiver.cs           (transport, Windows raw input)
└── RgbEffectCatalog.cs           (data, RGB modes)
```

## Target state

```
Driver/
├── Driver.csproj
│
├── Detection/                    # Device enumeration + identification
│   ├── KeyboardManager.cs
│   ├── KeyboardSpecs.cs
│   ├── KeyboardLayoutResolver.cs
│   └── KeyboardModels.cs
│
├── Transport/                    # HID I/O, both gen-1 and gen-2 OEM
│   ├── HidDeviceExtensions.cs
│   ├── HidStreamListener.cs
│   ├── RawInputReceiver.cs
│   ├── Gen2KeyboardChannel.cs
│   ├── Gen2WebHidChannel.cs
│   ├── IGen2Channel.cs
│   └── IGen2WebHidTransport.cs
│
├── Protocol/
│   ├── Gen1Alphabet/             # protocolFamily: drunkdeer-gen1-alphabet
│   │   ├── Packets.cs            (TODO Phase 4: split this file further)
│   │   └── FirmwareCapabilities.cs
│   └── ExtendedGateway/          # protocolFamily: drunkdeer-extended-gateway
│       ├── PacketsGen2.cs
│       └── KeyTriggerEntry.cs
│
├── Descriptors/                  # Phase 1 lands here. EMPTY for Phase 0.
│   └── (created in Phase 1)
│
├── Domain/                       # Cross-cutting data types
│   ├── Profile.cs
│   ├── KeyboardLayout.cs
│   ├── KeyMatrixSlotMap.cs
│   └── RgbEffectCatalog.cs
│
└── Diagnostics/
    └── DebugLogger.cs
```

## Rationale

- **Detection / Transport / Protocol / Domain / Diagnostics** is the obvious top-level split for a HID-talks-to-keyboards library. Each subdirectory's purpose is one sentence.
- **Protocol gets nested** because we have two protocol families per ADR-0002. Sub-folders make the split visible in the file tree. The interpreter (Phase 5) will live alongside `ExtendedGateway/`.
- **`Descriptors/` is created empty** even though Phase 1 fills it. Creating the directory in Phase 0 means Phase 1's PR is purely additive (new files in an existing directory) — easier to review.
- **`Domain/` for cross-cutting types** because `Profile.cs`, `KeyboardLayout.cs`, `KeyMatrixSlotMap.cs`, `RgbEffectCatalog.cs` are referenced from both Detection and Protocol. They don't belong in either.
- **`FirmwareCapabilities.cs` goes in `Protocol/Gen1Alphabet/`** because the file is currently entangled with gen-1 dispatch (`ResolvePrecision` only routes within that family). Once Phase 1's descriptor loader lands, the `FirmwareCapabilities` file becomes a legacy bridge — appropriate that it lives where the legacy lives.

## Namespace changes

Current namespace: `Driver` (flat). New namespaces map to subdirectories:

| Subdirectory | New namespace |
|---|---|
| `Driver/Detection/` | `Driver.Detection` |
| `Driver/Transport/` | `Driver.Transport` |
| `Driver/Protocol/Gen1Alphabet/` | `Driver.Protocol.Gen1Alphabet` |
| `Driver/Protocol/ExtendedGateway/` | `Driver.Protocol.ExtendedGateway` |
| `Driver/Descriptors/` | `Driver.Descriptors` |
| `Driver/Domain/` | `Driver.Domain` |
| `Driver/Diagnostics/` | `Driver.Diagnostics` |

Callers from `WpfApp/` will need `using` updates. Most existing files use `using Driver;` — they'll need to add the specific sub-namespaces they reference.

**Alternative considered and rejected**: keep the flat `Driver` namespace, only do file moves. Rejected because IntelliSense / `using` autocompletion is the main daily benefit of the restructure — the namespace hierarchy is what makes "where does X come from" answerable from a file's `using` list.

## Step-by-step execution

1. Create the empty subdirectories in `Driver/`.
2. For each subdirectory, move its files in. Update each file's `namespace Driver;` declaration to the matching new namespace.
3. Run `dotnet build DrunkDeerDriver.sln`. It will produce a list of `CS0246` (type not found) and `CS0234` (namespace not found) errors in `WpfApp/`.
4. Walk through each error: add the appropriate `using Driver.X;` to the failing file.
5. Re-build until clean. Run the app (`dotnet run --project WpfApp/WpfApp.csproj`) and verify keyboard detection + a profile push still work on A75 Pro 0x0017.
6. Commit as **one** commit. Title: `refactor: restructure Driver/ into subdirectories (Phase 0)`. No logic changes in the diff.

## Acceptance criteria

- `dotnet build` succeeds with zero warnings new to this PR.
- App launches.
- A75 Pro 0x0017 is detected.
- A profile push lands without errors.
- `git log --stat` on this commit shows ~21 file moves + matching `using` updates. **Nothing else.** If there's a logic change in the diff, the PR is wrong.

## Risks and rollback

- **Risk**: namespace updates break a serialization path (e.g. settings file uses fully-qualified type names). **Mitigation**: search the codebase for `"Driver."` strings before moving. Likely none, but worth checking.
- **Risk**: the `[XmlSerializable]` or `JsonConverter` attribute on a moved type breaks the wire format of a saved profile. **Mitigation**: re-test loading an existing profile file after the restructure.
- **Rollback**: `git revert` the single commit. Restructure is pure mechanical — revert is safe.

## Out of scope for this phase

These come later. Do not bundle them into Phase 0.

- Splitting `Packets.cs` into smaller files (Phase 4).
- Splitting `MainWindow.xaml.cs` or `KeyboardPerformanceView.xaml.cs` (Phase 8, the existing roadmap.md plan).
- Deleting `BuildPackets` dead code (Phase 3).
- Renaming `PacketsGen2` to something clearer (Phase 5 will pick this up — the new name probably involves "ExtendedGateway").
- Adding test projects (Phase 8).

## When this is done

1. Mark Phase 0 `DONE ✓ <PR-link> <date>` in [README.md](README.md) status table.
2. Delete this file (history is in git).
3. Move on to Phase 1.
