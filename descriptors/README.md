# `descriptors/` — data-driven keyboard catalog

Each file in this directory describes a DrunkDeer keyboard model and its firmware variants. The driver loads these at runtime and dispatches per-keyboard behavior off the descriptor data, not off code branches.

## Layout

```
descriptors/
├── README.md              ← this file
├── schema.json            ← JSON Schema for descriptor validation (TODO)
├── keyboards/
│   ├── A75Pro.json        ← one file per model, with all firmware variants inside
│   └── ...
└── layouts/
    ├── A75Pro.json        ← 126-key visual layout, referenced from descriptor.layout.keyNamesRef
    └── ...
```

## Schema

Schema is defined and motivated in [docs/adr/0001-descriptor-schema.md](../docs/adr/0001-descriptor-schema.md). Read that first.

Current version: `schemaVersion: 1`.

## Adding a new keyboard

1. Capture a HAR of `drunkdeer.keybord.net.cn` doing the right things on the keyboard's hardware (or a USB packet capture if HID-level bytes are needed).
2. Identify the upstream model JSON the site loaded for that keyboard (`./K68_Ultra_UK.json`, etc).
3. Copy `descriptors/keyboards/A75Pro.json` as a template; fill in identity, layout, firmware variants.
4. Run the validator (TODO: `dotnet run --project tools/Descriptors -- validate`).
5. Submit PR with the descriptor + at least one entry in the variant's `verifiedOn` evidence list.

No C# changes should be required to add a keyboard once the descriptor interpreter is in place.

## Terminology

See [/CONTEXT.md](../CONTEXT.md) for definitions of *Descriptor*, *Gen-1/Gen-2 site*, *Gen-1/Gen-2 firmware*, *Wire-format dialect*.
