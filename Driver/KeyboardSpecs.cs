namespace Driver;

// received_report_handle with a0
// on_event_keyboard_open
public sealed record KeyboardSpecs
{
    public string FirmwareVersion { get; set; } = string.Empty;

    // Numeric form of the firmware version, suitable for ordered comparisons
    // and capability lookups. Combines packet[8] (high byte) and packet[7]
    // (low byte) of the spec response — same source as `FirmwareVersion`,
    // just decoded as a number instead of formatted as a string. 0 when
    // the spec response was invalid / unparseable.
    //
    // Concrete known values:
    //   0x0009 — A75 Pro factory floor (older units)
    //   0x0017 — A75 Pro newer factory firmware (V2.3.4 bundle / current)
    //   0x0027 — A75 ANSI (V2.3.4)
    //   0x0023 — A75 ISO  (V2.3.4)
    //   0x0055 — A75 Ultra / A75 Master (V2.3.4)
    // See docs/keyboard-protocol.md §7 and config.ini in the official updater
    // bundle for the full table.
    public ushort FirmwareVersionNumeric { get; set; }
    public int? TurboValue { get; set; }
    public int? RapidTrigger { get; set; }
    public int? RapidTriggerPlus { get; set; }
    public int? LastWinValue { get; set; }
    public int? KeyboardType { get; set; }
    public bool? RTMatch { get; set; }
    public byte? AutoMatchMode { get; set; }
    public bool? LastWinReplace { get; set; }

    // Active profile slot index, populated for gen-2 OEM keyboards from the
    // first data byte of the ReadBaseBlock response (offset 8). Used by the
    // OEM sync path to target the correct profile region (each profile is
    // 1024 bytes; address = ActiveProfileIndex × 1024). Defaults to 0 for
    // gen-1 keyboards where the concept doesn't apply.
    //
    // Verified 2026-05-26: tester B's gen-2 OEM A75 Pro reports
    // ActiveProfileIndex=1 — and the official driver writes its slider drags
    // to addr=0x0400 (= 1 × 1024). Beta.21..26 hardcoded 0 here, so every
    // sync wrote to a memory slot the firmware never read from.
    public byte ActiveProfileIndex { get; set; }

    public KeyboardSpecs(byte[] packet)
    {
        if (packet.Length < 1)
        {
            DebugLogger.Log($"  KeyboardSpecs: empty packet");
            return;
        }
        if (packet[0] != 0xa0)
        {
            DebugLogger.Log($"  KeyboardSpecs: rejected, packet[0]=0x{packet[0]:x2} (expected 0xa0)");
            return;
        }
        if (packet[1] == 0x02 && packet[2] == 0x00)
        {
            // Byte offsets below mirror the official driver's JS spec-response parser;
            // see docs/keyboard-protocol.md section 7 for the full byte map.
            FirmwareVersion = string.Format("0.{0}{1}", packet[8], packet[7]);
            FirmwareVersionNumeric = (ushort)((packet[8] << 8) | packet[7]);
            TurboValue = packet[15];
            RapidTrigger = packet[16];
            RapidTriggerPlus = packet[18];
            LastWinValue = packet[19];
            KeyboardType = GetKeyboardType(packet);
            if (packet.Length > 32)
            {
                RTMatch = packet[30] != 0;
                AutoMatchMode = packet[31];
                LastWinReplace = packet[32] != 0;
            }
        }
        else
        {
            DebugLogger.Log($"  KeyboardSpecs: header bytes 1/2 = 0x{packet[1]:x2}/0x{packet[2]:x2} (expected 0x02/0x00)");
        }
    }

    // Resolves the firmware-capability profile for this keyboard.
    // Returns Verified / Beta / Unknown based on the (TypeCode, firmware)
    // combination. See FirmwareCapabilities.cs for the resolution table
    // and tier semantics.
    public FirmwareCapabilities GetCapabilities() =>
        FirmwareCapabilities.Resolve(KeyboardType, FirmwareVersionNumeric);

    // Type-byte triples (packet[4], packet[5], packet[6]) -> firmware TypeCode.
    // Canonical map lives in Driver/KeyboardModels.cs (sourced from dd-index.js);
    // see docs/keyboard-protocol.md for the full extracted table. We delegate
    // to KeyboardModels.FindByTypeBytes so the type map stays in one place.
    // The two legacy aliases below predate the canonical triples and are kept
    // for backwards compatibility with older firmware revisions that still
    // emit (15,1,1) for G65 and (11,1,1) for A75.
    // (11, 4, 2) => 751: A75 UK / FR / DE share the same firmware TypeCode;
    // the locale variants are distinguished elsewhere (see KeyboardModels.cs).
    private static int? GetKeyboardType(byte[] packet)
    {
        if (packet[4] == 15 && packet[5] == 1 && packet[6] == 1) return 65; // legacy G65
        if (packet[4] == 11 && packet[5] == 1 && packet[6] == 1) return 75; // legacy A75
        return KeyboardModels.FindByTypeBytes(packet[4], packet[5], packet[6])?.TypeCode;
    }
}
