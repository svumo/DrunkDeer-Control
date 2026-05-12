namespace Driver;

// received_report_handle with a0
// on_event_keyboard_open
public sealed record KeyboardSpecs
{
    public string FirmwareVersion { get; set; } = string.Empty;
    public int? TurboValue { get; set; }
    public int? RapidTrigger { get; set; }
    public int? RapidTriggerPlus { get; set; }
    public int? LastWinValue { get; set; }
    public int? KeyboardType { get; set; }

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
            FirmwareVersion = string.Format("0.{0}{1}", packet[8], packet[7]);
            TurboValue = packet[15];
            RapidTrigger = packet[16];
            RapidTriggerPlus = packet[18];
            LastWinValue = packet[19];
            KeyboardType = GetKeyboardType(packet);
        }
        else
        {
            DebugLogger.Log($"  KeyboardSpecs: header bytes 1/2 = 0x{packet[1]:x2}/0x{packet[2]:x2} (expected 0x02/0x00)");
        }
    }

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
