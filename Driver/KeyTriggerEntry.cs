namespace Driver;

// Per-key 8-byte record written via the gen-2 0x55 0xA1
// WriteKeyTriggerChunk command. Encodes actuation point, rapid-trigger
// press / release thresholds, plus a few firmware-managed fields we leave
// at zero on first-cut sync.
//
// Bit layout (from deerios DrunkDeerSDK protocol/structs.yaml KeyTriggerEntry;
// also see docs/gen2-wire-format-confirmed.md for the capture-derived proof):
//
//   byte 0  bits 0-3: switch_type   bits 4-7: 0xA (constant on encode)
//   byte 1  bits 0-3: key_mode      bits 4-7: priority
//   byte 2..3  LE9 actuation        (stored = value − 1; bit 0 of byte 3 = 9th bit)
//   byte 3  bits 1-2: release_precision   bits 3-4: press_precision
//   byte 4..5  LE9 rt_press         (stored = value − 1; bit 0 of byte 5 = 9th bit)
//   byte 5  bits 1-7: press_deadzone
//   byte 6..7  LE9 rt_release       (stored = value − 1; bit 0 of byte 7 = 9th bit)
//   byte 7  bits 1-7: release_deadzone
//
// All distances are stored in units of 0.01mm. The +1 bias means a
// stored value of 0x3B (59) on the wire represents 60 = 0.60mm displayed.
//
// Verified against capture frame 7 (steady-state) and frame 19 (changed
// record): encoding (actuation=60, rt_press=15, rt_release=50) produces
// `a0 01 3b 00 0e 00 31 00`, and (actuation=138, rt_press=15, rt_release=50)
// produces `a0 01 89 00 0e 00 31 00`. Both match the wire bytes exactly.
public static class KeyTriggerEntry
{
    public const int BYTE_SIZE = 8;

    // Firmware defaults observed in the user's capture across every key.
    // Used when the source profile leaves a value at 0 (slider untouched).
    public const int DEFAULT_RT_PRESS = 15;
    public const int DEFAULT_RT_RELEASE = 50;

    // Encodes one 8-byte record into `dst` at the given offset.
    // Inputs are display values in 0.01mm units (the +1 bias is applied
    // here, so callers pass the value they want the user to see).
    //
    // - actuation: 0.01mm; firmware accepts 1..511 after subtracting 1.
    //   Slider range on the OEM is roughly 0.10mm..3.30mm = 10..330.
    // - rtPress: 0.01mm; firmware accepts 1..511. Slider 0.01..3.10 = 1..310.
    // - rtRelease: 0.01mm; ditto.
    //
    // Values <= 0 are coerced to the firmware default so an untouched
    // gen-1-era profile (which has no concept of RT press/release) doesn't
    // accidentally zero the firmware's RT zones.
    public static void Encode(byte[] dst, int offset, int actuation, int rtPress, int rtRelease)
    {
        if (actuation <= 0) actuation = 60;
        if (rtPress   <= 0) rtPress   = DEFAULT_RT_PRESS;
        if (rtRelease <= 0) rtRelease = DEFAULT_RT_RELEASE;

        // Clamp to the 9-bit LE9 storage range (stored + 1 must fit in 9 bits).
        if (actuation > 512) actuation = 512;
        if (rtPress   > 512) rtPress   = 512;
        if (rtRelease > 512) rtRelease = 512;

        int actStored = actuation - 1;
        int pressStored = rtPress - 1;
        int releaseStored = rtRelease - 1;

        // byte 0: switch_type=0 (low nibble) + 0xA constant in high nibble.
        dst[offset + 0] = 0xA0;

        // byte 1: key_mode=1 (low nibble) + priority=0 (high nibble).
        dst[offset + 1] = 0x01;

        // bytes 2..3: actuation LE9. byte 2 = lower 8 bits, byte 3 bit 0 = 9th bit.
        dst[offset + 2] = (byte)(actStored & 0xFF);
        dst[offset + 3] = (byte)((actStored >> 8) & 0x01);  // 9th bit only; precision bits stay 0

        // bytes 4..5: rt_press LE9.
        dst[offset + 4] = (byte)(pressStored & 0xFF);
        dst[offset + 5] = (byte)((pressStored >> 8) & 0x01); // press_deadzone bits 1-7 stay 0

        // bytes 6..7: rt_release LE9.
        dst[offset + 6] = (byte)(releaseStored & 0xFF);
        dst[offset + 7] = (byte)((releaseStored >> 8) & 0x01); // release_deadzone bits 1-7 stay 0
    }

    // Convenience: encode N records into a fresh `byte[N * BYTE_SIZE]` buffer.
    // Caller is responsible for value ranges. `count` is the firmware slot
    // count (128 on A75 Pro per the capture; 126 physical keys + 2 padding).
    public static byte[] EncodeAll(int count, System.Func<int, (int actuation, int rtPress, int rtRelease)> source)
    {
        var buffer = new byte[count * BYTE_SIZE];
        for (int i = 0; i < count; i++)
        {
            var (a, p, r) = source(i);
            Encode(buffer, i * BYTE_SIZE, a, p, r);
        }
        return buffer;
    }
}
