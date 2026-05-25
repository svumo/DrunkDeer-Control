namespace Driver;

// Gen-2 "extended gateway" wire format (0x55 / 0xAA opcode family).
// Used by OEM firmware variants whose firmware ignores the gen-1 0xA0/0xB6
// alphabet — confirmed 2026-05-25 via USBPcap capture from the affected
// user (VID 0x19F5 / PID 0xFB5C, A75 Pro OEM, firmware 1.7).
//
// Full wire-format derivation, checksum proof, and per-key record layout
// live in docs/gen2-wire-format-confirmed.md. This file is just the
// builders + envelope helpers.
//
// All 0x55 commands share an envelope:
//
//   Read   (no payload):  [0x55, sub_cmd, 0x00, cs, length, addr_lo, addr_hi]
//   Write  (+ data[N]):   [0x55, sub_cmd, 0x00, cs, length, addr_lo, addr_hi, is_last, data[N]]
//
// Response (shared for all 0x55 ops):
//   [0xAA, sub_cmd, 0x00, _reserved[7], data[..]]    — read data starts at byte 10
//
// Checksum is over the variable inner envelope, computed by the transport
// layer before sending:
//
//   Read  cs = (length + addr_lo + addr_hi) & 0xFF
//   Write cs = (length + addr_lo + addr_hi + is_last + sum(data)) & 0xFF
//
// Sub-commands we care about (see deerios protocol/base_messages.yaml):
//   0x04  ReadBaseBlock        (read 32 bytes at addr 0 — for identity probe)
//   0x05  ReadFuncBlock
//   0x06  WriteFuncBlockChunk
//   0x0C  ReadMacroChunk
//   0x0D  WriteMacroChunk
//   0x0E  WriteActiveProfile
//   0xA0  ReadKeyTrigger
//   0xA1  WriteKeyTriggerChunk (per-key actuation/RT, 56 bytes/chunk, 1024 bytes/profile)
public static class PacketsGen2
{
    public const byte REQUEST_MAGIC  = 0x55;
    public const byte RESPONSE_MAGIC = 0xAA;

    // Sub-commands.
    public const byte SUB_READ_BASE_BLOCK    = 0x04;
    public const byte SUB_READ_KEY_TRIGGER   = 0xA0;
    public const byte SUB_WRITE_KEY_TRIGGER  = 0xA1;
    public const byte SUB_WRITE_ACTIVE_PROFILE = 0x0E;

    // The "read N bytes from address" request. 8 useful bytes; rest is
    // zero padding up to the channel's wire size.
    //
    // For the identity probe send (length=32, address=0): the response
    // payload[0] is the currently active profile index. Anything non-empty
    // confirms the device speaks the 0x55 family — that's enough to identify
    // it as a gen-2 OEM and proceed.
    public static byte[] BuildReadBaseBlock(byte length = 32, ushort address = 0)
        => BuildReadRequest(SUB_READ_BASE_BLOCK, length, address);

    // Generic read request builder. length is bytes to read (1..56);
    // address is the firmware-side byte offset.
    public static byte[] BuildReadRequest(byte subCmd, byte length, ushort address)
    {
        var packet = new byte[Packets.PACKET_SIZE];
        byte addrLo = (byte)(address & 0xFF);
        byte addrHi = (byte)((address >> 8) & 0xFF);
        packet[0] = REQUEST_MAGIC;
        packet[1] = subCmd;
        packet[2] = 0x00;
        packet[3] = ComputeReadChecksum(length, addrLo, addrHi);
        packet[4] = length;
        packet[5] = addrLo;
        packet[6] = addrHi;
        return packet;
    }

    public static byte ComputeReadChecksum(byte length, byte addrLo, byte addrHi)
        => (byte)((length + addrLo + addrHi) & 0xFF);

    public static byte ComputeWriteChecksum(byte length, byte addrLo, byte addrHi, byte isLast, System.ReadOnlySpan<byte> data)
    {
        int sum = length + addrLo + addrHi + isLast;
        for (int i = 0; i < data.Length; i++) sum += data[i];
        return (byte)(sum & 0xFF);
    }

    // True if the response packet matches the gen-2 extended-gateway format
    // for the given sub-command (e.g. 0xAA 0x04 0x00 ... for ReadBaseBlock).
    public static bool IsExtendedGatewayResponse(byte[] response, byte expectedSubCmd)
        => response.Length >= 3
        && response[0] == RESPONSE_MAGIC
        && response[1] == expectedSubCmd
        && response[2] == 0x00;

    // Maximum data bytes per write chunk (8-byte envelope + 56 data = 64 wire bytes).
    public const int WRITE_CHUNK_DATA_MAX = 56;

    // Per-key record stride within the KeyTrigger region (see KeyTriggerEntry).
    public const int KEY_TRIGGER_RECORD_SIZE = 8;

    // Firmware-side slot count for the KeyTrigger region on A75 Pro OEM
    // (128 slots × 8 bytes = 1024 bytes total). 126 physical keys plus
    // 2 padding slots — matches the capture's 18×56 + 1×16 = 1024-byte
    // chunked write footprint.
    public const int KEY_TRIGGER_SLOT_COUNT = 128;
    public const int KEY_TRIGGER_REGION_SIZE = KEY_TRIGGER_SLOT_COUNT * KEY_TRIGGER_RECORD_SIZE;

    // Builds the 19-packet chunked WriteKeyTriggerChunk (0x55 0xA1) sequence
    // that pushes a full profile's key-trigger region to the firmware.
    // `data` must be exactly KEY_TRIGGER_REGION_SIZE (1024) bytes.
    // `profileIndex` selects which profile slot; A75 Pro OEM observed
    // writing to profile 0 in the user capture. Address stride per profile
    // is the full region size.
    //
    // Sequence shape (matches the user's capture frames 7..79 exactly):
    //   - 18 full chunks: length=0x38 (56), addresses 0x000..0x3B8 stepping by 0x38, is_last=0
    //   - 1 trailer chunk: length=0x10 (16), address=0x3F0, is_last=0x01
    public static System.Collections.Generic.IEnumerable<byte[]> BuildWriteKeyTriggerChunkSequence(byte[] data, int profileIndex = 0)
    {
        if (data is null || data.Length != KEY_TRIGGER_REGION_SIZE)
            throw new System.ArgumentException($"data must be exactly {KEY_TRIGGER_REGION_SIZE} bytes, got {data?.Length ?? 0}", nameof(data));

        int baseAddress = profileIndex * KEY_TRIGGER_REGION_SIZE;
        var chunks = new System.Collections.Generic.List<byte[]>(19);

        int dataOffset = 0;
        while (dataOffset < KEY_TRIGGER_REGION_SIZE)
        {
            int remaining = KEY_TRIGGER_REGION_SIZE - dataOffset;
            int length = remaining >= WRITE_CHUNK_DATA_MAX ? WRITE_CHUNK_DATA_MAX : remaining;
            bool isLast = (dataOffset + length) == KEY_TRIGGER_REGION_SIZE;
            ushort address = (ushort)(baseAddress + dataOffset);
            byte addrLo = (byte)(address & 0xFF);
            byte addrHi = (byte)((address >> 8) & 0xFF);
            byte isLastByte = isLast ? (byte)0x01 : (byte)0x00;

            // 64-byte chunk: 8-byte envelope + up to 56 bytes of data, zero-padded.
            var chunk = new byte[64];
            chunk[0] = REQUEST_MAGIC;
            chunk[1] = SUB_WRITE_KEY_TRIGGER;
            chunk[2] = 0x00;
            chunk[3] = ComputeWriteChecksum((byte)length, addrLo, addrHi, isLastByte, data.AsSpan(dataOffset, length));
            chunk[4] = (byte)length;
            chunk[5] = addrLo;
            chunk[6] = addrHi;
            chunk[7] = isLastByte;
            System.Array.Copy(data, dataOffset, chunk, 8, length);
            // bytes 8+length .. 63 stay 0.

            chunks.Add(chunk);
            dataOffset += length;
        }

        return chunks;
    }
}
