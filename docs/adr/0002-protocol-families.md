# ADR 0002 — Protocol families in descriptors

**Status**: Accepted (2026-05-27). Amends [ADR-0001](0001-descriptor-schema.md).

**Context**

ADR-0001 designed the descriptor schema assuming each firmware variant has a single shape of `opcodes` field: a flat map from operation name to byte sequence + structured fields. This works for the gen-1 alphabet (`0xA0` identity, `0xB5` common switch, `0xB6` per-key, `0xFC` pair tables, etc.) and for the gen-2 transport-only variant of the gen-1 alphabet (same opcodes, 65-byte HID reports with required `0x04` prefix).

It does **not** work for the gen-2 OEM extended-gateway protocol, documented in [docs/gen2-wire-format-confirmed.md](../gen2-wire-format-confirmed.md) and implemented in [Driver/PacketsGen2.cs](../../Driver/PacketsGen2.cs). That protocol is structurally different:

- Single envelope format wrapping all operations: `[0x55, sub_cmd, 0x00, cs, length, addr_lo, addr_hi, is_last, data...]`
- Operations are sub-commands *within* the envelope, not independent opcodes
- Checksums computed by the transport layer per request
- Data is memory-addressed (per-profile = 1024 bytes at `profileIndex × 0x400`)
- Per-key data is structured 8-byte records (`KeyTriggerEntry` per `deerios/DrunkDeerSDK::structs.yaml`), written in 56-byte chunks

Discovered during the 2026-05-27 grill session by inspecting `PacketsGen2.cs`, after the user correctly recalled that gen-1 and gen-2 firmware use different opcodes — contradicting the earlier `docs/keyboard-protocol-gen2.md` writeup which only covered the easier transport-difference case.

**Decision**

Each firmware variant in a descriptor carries a top-level `protocolFamily` field that selects one of two (currently) shapes for the variant's wire-format specification:

- `"protocolFamily": "drunkdeer-gen1-alphabet"` — variant has an `opcodes` field per ADR-0001's structured-opcodes design. Used by gen-1 firmware (VID `0x352D`, 64-byte HID) and gen-2-transport-only firmware (VID `0x352D` newer, 65-byte HID with Report ID `0x04`). The `hidReportSize` and `hidReportIdRequired` fields in `wireFormat` distinguish those sub-cases.

- `"protocolFamily": "drunkdeer-extended-gateway"` — variant has an `envelope`, `subCommands`, `memoryMap`, and `keyTriggerEntry` field instead of `opcodes`. Used by gen-2 OEM firmware (VID `0x19F5 / 0x352D` OEM relabels, 64-byte HID, no Report ID). Operations are sub-command bytes within the shared envelope.

The two shapes are completely separate. There is **no shared abstraction** that flattens them into one. Rationale: the protocols are genuinely different. Trying to unify would either bloat the schema with optional fields that only make sense per family, or force a lossy abstraction. Per-family shapes keep the schema honest.

**Concrete shape for `drunkdeer-extended-gateway`**:

```jsonc
{
  "protocolFamily": "drunkdeer-extended-gateway",
  "wireFormat": {
    "hidReportSize": 64,
    "hidReportIdRequired": false,
    "envelope": {
      "requestMagic": "0x55",
      "responseMagic": "0xAA",
      "fields": [
        { "name": "magic",     "offset": 0, "type": "u8" },
        { "name": "subCmd",    "offset": 1, "type": "u8" },
        { "name": "_reserved", "offset": 2, "type": "u8", "value": 0 },
        { "name": "checksum",  "offset": 3, "type": "u8", "_computedBy": "transport" },
        { "name": "length",    "offset": 4, "type": "u8" },
        { "name": "address",   "offset": 5, "type": "u16le" },
        { "name": "isLast",    "offset": 7, "type": "u8", "_writeOnly": true },
        { "name": "data",      "offset": 8, "type": "u8[]", "_writeOnly": true, "_lengthFromField": "length" }
      ],
      "responseDataOffset": 8,
      "checksumSpec": {
        "read":  "(length + addr_lo + addr_hi) & 0xFF",
        "write": "(length + addr_lo + addr_hi + is_last + sum(data)) & 0xFF"
      }
    }
  },
  "subCommands": {
    "readBaseBlock":         { "subCmd": "0x04", "_purpose": "identity probe; data[0] = active profile index" },
    "writeKeyTriggerChunk":  { "subCmd": "0xA1", "_purpose": "per-key actuation, 56-byte chunks, 1024 bytes/profile" },
    "writeLwPairs":          { "subCmd": "0xA5" }
    /* ... */
  },
  "memoryMap": {
    "keyTriggerRegion": {
      "perProfileSize": 1024,
      "addressByProfileIndex": "profileIndex * 1024",
      "chunkSize": 56,
      "chunkCount": "18 full + 1 trailer (16 bytes)",
      "slotCount": 128,
      "recordSize": 8
    }
  },
  "keyTriggerEntry": {
    "byteSize": 8,
    "fields": [
      { "name": "switchType",        "byte": 0, "bits": "0-3" },
      { "name": "_constHi",          "byte": 0, "bits": "4-7", "value": "0xA" },
      { "name": "keyMode",           "byte": 1, "bits": "0-3" },
      { "name": "priority",          "byte": 1, "bits": "4-7" },
      { "name": "actuation",         "bytes": "2-3", "encoding": "LE9", "biasOf": 1, "units": "0.01mm" },
      { "name": "releasePrecision",  "byte": 3, "bits": "1-2" },
      { "name": "pressPrecision",    "byte": 3, "bits": "3-4" },
      { "name": "rtPress",           "bytes": "4-5", "encoding": "LE9", "biasOf": 1, "units": "0.01mm" },
      { "name": "pressDeadzone",     "byte": 5, "bits": "1-7" },
      { "name": "rtRelease",         "bytes": "6-7", "encoding": "LE9", "biasOf": 1, "units": "0.01mm" },
      { "name": "releaseDeadzone",   "byte": 7, "bits": "1-7" }
    ]
  }
}
```

**Consequences**

- The descriptor interpreter is now actually two interpreters dispatched on `protocolFamily`. That's appropriate — these are different protocols, not different parameterizations of one protocol.
- ADR-0001's "one schema fits all" framing is amended; both flavors are first-class.
- `descriptors/keyboards/A75Pro.json` needs to be revised: existing gen-1-firmware variants get `protocolFamily: "drunkdeer-gen1-alphabet"`; a new OEM variant needs adding for VID `0x19F5 / 0xFB5C / firmware 1.7` with `drunkdeer-extended-gateway`. The `gen2-pending` placeholder variant is ambiguous until we get the friend's VID — could be either family.
- Schema migration: descriptors without a `protocolFamily` field default to `drunkdeer-gen1-alphabet` (backwards-compat with ADR-0001 v1 descriptors). Bump to `schemaVersion: 2` once any descriptor uses `drunkdeer-extended-gateway`.
- Future protocol families (if DrunkDeer ships another variant) are accommodated by adding another value to the enum and another per-family schema shape. The dispatch key stays the same.

**Alternatives considered**

- *Force-fit OEM into the gen-1 schema by treating sub-commands as opcodes*: rejected. Loses the envelope/checksum structure, makes the keyTrigger record encoding invisible, breaks anyone reading the descriptor to understand the wire protocol.
- *Two separate top-level descriptor types (KeyboardGen1.json, KeyboardOemGen2.json)*: rejected. A single keyboard model (A75 Pro) can have variants spanning both protocol families. Forcing the user to choose a "type" at the model level breaks that.
- *Defer the OEM case until later*: rejected per user decision 2026-05-27 ("Revise schema for protocol families now"). Building on a schema that can't accommodate the OEM case would mean a bigger migration later.
