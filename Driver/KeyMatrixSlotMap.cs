using System.Collections.Generic;

namespace Driver;

// Maps firmware key slots to their HID usage codes — built from the
// keymap returned by `BuildReadDefaultKeyMatrixSequence`. The firmware's
// per-key data regions (KeyTrigger at 1024×profile + 8×slot, LW per-pair
// RTP at 3×slot) are addressed by *firmware slot*, not by visual layout
// position. Without this map we can't reliably write to the right key on
// keyboards where the firmware slot order differs from our visual layout.
//
// Captured evidence (gen-2 OEM A75 Pro, tester B's keyboard,
// usb3/usb5/usb6.pcapng):
//   HID 0x04 (A) → firmware slot 37
//   HID 0x07 (D) → firmware slot 39
//   HID 0x16 (S) → firmware slot 38
//   HID 0x1A (W) → firmware slot 26
//
// In our visual layout (`KeyboardLayout.A75Pro`) these keys are at
// KeyIndex 64/66/65/44 — a different numbering. Beta.27..beta.34 wrote
// actuation data at our KeyIndex offsets, which means tester B's gen-2
// OEM keyboard received per-key changes at the *wrong physical keys*
// (slot 64 of his firmware was whatever key happens to be there, not A).
// This class is the fix.
public sealed class KeyMatrixSlotMap
{
    // slot[N] = (type, code1, code2) at firmware slot N. Length = 128.
    private readonly (byte type, byte code1, byte code2)[] _bySlot;

    // hid (USB usage ID) → firmware slot. Only standard-key slots
    // (type 0x10). If two slots claim the same HID, the lower slot wins
    // (matches the JS bundle's `findIndex` semantics).
    private readonly Dictionary<byte, int> _byHid;

    private KeyMatrixSlotMap((byte, byte, byte)[] bySlot, Dictionary<byte, int> byHid)
    {
        _bySlot = bySlot;
        _byHid = byHid;
    }

    public int SlotCount => _bySlot.Length;

    // Number of slots whose type marks them as standard HID keys.
    public int StandardKeyCount => _byHid.Count;

    // Look up the firmware slot for a HID usage code. Returns -1 if
    // unmapped (e.g., HID code doesn't appear in the firmware's keymap).
    public int TryGetSlotForHid(byte hid) => _byHid.TryGetValue(hid, out var slot) ? slot : -1;

    // Look up the HID code at a firmware slot. Returns 0 if the slot
    // isn't a standard key.
    public byte HidAtSlot(int slot)
    {
        if (slot < 0 || slot >= _bySlot.Length) return 0;
        var (type, _, code2) = _bySlot[slot];
        return type == PacketsGen2.KEY_MATRIX_TYPE_STANDARD ? code2 : (byte)0;
    }

    public (byte type, byte code1, byte code2) RawAtSlot(int slot)
    {
        if (slot < 0 || slot >= _bySlot.Length) return (0, 0, 0);
        return _bySlot[slot];
    }

    // Parse a 512-byte default-keymap readback into a slot map.
    // `matrixBytes` must be at least KEY_MATRIX_REGION_SIZE bytes; only
    // the first KEY_MATRIX_SLOT_COUNT × KEY_MATRIX_ENTRY_SIZE bytes are
    // consumed (the rest of the region is padding the firmware leaves zero).
    public static KeyMatrixSlotMap BuildFromMatrix(byte[] matrixBytes)
    {
        if (matrixBytes is null) throw new System.ArgumentNullException(nameof(matrixBytes));
        int needed = PacketsGen2.KEY_MATRIX_SLOT_COUNT * PacketsGen2.KEY_MATRIX_ENTRY_SIZE;
        if (matrixBytes.Length < needed)
            throw new System.ArgumentException($"need at least {needed} bytes, got {matrixBytes.Length}", nameof(matrixBytes));

        var bySlot = new (byte, byte, byte)[PacketsGen2.KEY_MATRIX_SLOT_COUNT];
        var byHid = new Dictionary<byte, int>();
        for (int slot = 0; slot < PacketsGen2.KEY_MATRIX_SLOT_COUNT; slot++)
        {
            int o = slot * PacketsGen2.KEY_MATRIX_ENTRY_SIZE;
            byte type = matrixBytes[o + 0];
            byte code1 = matrixBytes[o + 1];
            byte code2 = matrixBytes[o + 2];
            bySlot[slot] = (type, code1, code2);
            if (type == PacketsGen2.KEY_MATRIX_TYPE_STANDARD && code2 != 0 && !byHid.ContainsKey(code2))
                byHid[code2] = slot;
        }
        return new KeyMatrixSlotMap(bySlot, byHid);
    }
}
