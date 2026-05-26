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
//   0x09  WriteLwPairRtp        (per-pair LW threshold; verified usb3.pcapng)
//   0x0C  ReadMacroChunk
//   0x0D  WriteMacroChunk
//   0x0E  WriteActiveProfile
//   0xA0  ReadKeyTrigger
//   0xA1  WriteKeyTriggerChunk (per-key actuation/RT, 56 bytes/chunk, 1024 bytes/profile)
//   0xA5  WriteLwPairs          (LW pair table; 5 chunks at base 0x0100, verified usb3.pcapng)
public static class PacketsGen2
{
    public const byte REQUEST_MAGIC  = 0x55;
    public const byte RESPONSE_MAGIC = 0xAA;

    // Sub-commands.
    public const byte SUB_READ_BASE_BLOCK      = 0x04;
    public const byte SUB_READ_FUNC_BLOCK      = 0x05;
    public const byte SUB_WRITE_FUNC_BLOCK     = 0x06;
    public const byte SUB_WRITE_LW_PAIR_RTP    = 0x09;
    public const byte SUB_WRITE_ACTIVE_PROFILE = 0x0E;
    public const byte SUB_READ_KEY_TRIGGER     = 0xA0;
    public const byte SUB_WRITE_KEY_TRIGGER    = 0xA1;
    public const byte SUB_WRITE_LW_PAIRS       = 0xA5;

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

    // Offset of the first data byte in a 0x55-family response.
    //
    // Layout: [magic, sub_cmd, 0x00, cs, length, addr_lo, addr_hi, is_last, data...]
    //   bytes 0-2: header  (3 bytes)
    //   bytes 3-7: envelope echo (5 bytes — checksum, length, address u16le, is_last)
    //   byte 8+:   data
    //
    // The earlier 2026-05-25 wire-format doc said offset 10. That was wrong:
    // verified 2026-05-26 against usb2.pcapng frame 12879 (ReadFuncBlock,
    // length=0x38=56 requested) — 56 bytes of data run from offset 8 to 63,
    // which only works if data starts at 8. Corrected here as the source of
    // truth; the doc has been updated to match.
    public const int RESPONSE_DATA_OFFSET = 8;

    // Pulls the active profile slot index from a ReadBaseBlock (0x55 0x04)
    // response. The first byte of the returned data carries the index.
    //
    // Returns 0 if the response is malformed or too short — that matches the
    // pre-2026-05-26 hardcoded behavior, so callers degrade gracefully on
    // unfamiliar response shapes.
    public static byte ParseActiveProfileIndex(byte[] response)
    {
        if (!IsExtendedGatewayResponse(response, SUB_READ_BASE_BLOCK)) return 0;
        if (response.Length <= RESPONSE_DATA_OFFSET) return 0;
        return response[RESPONSE_DATA_OFFSET];
    }

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

    // ── Last Win pair table (0x55 0xA5) ─────────────────────────────────────
    //
    // Verified 2026-05-26 via tester B's usb3.pcapng (the LW-activation
    // capture). The official OEM driver writes the LW pair table as 5
    // chunked 0xA5 packets totalling 256 bytes, addresses 0x0100..0x01E0
    // stepping by the chunk length (56 / 56 / 56 / 56 / 32). Final chunk
    // carries is_last=0x01.
    //
    // Per-pair encoding within the region: each user pair (e.g. A↔D) is
    // bidirectional and expands into 2 firmware-level pairs (A→D and D→A).
    // Each firmware pair is 6 bytes:
    //
    //   [0x10, 0x00, mainKey_HID, 0x10, 0x00, triggerKey_HID]
    //
    // The 0x10/0x00 bytes are constant in the captured A↔D pair — likely a
    // type/group tag the firmware ignores on this opcode (the pair index is
    // positional, not encoded in the bytes). Mirror them verbatim.
    public const int LW_PAIRS_REGION_SIZE = 256;
    public const ushort LW_PAIRS_BASE_ADDR = 0x0100;
    public const int LW_PAIR_RECORD_SIZE = 6;

    // Encodes one directional firmware pair into `dst` at `offset`.
    private static void EncodeLwPairRecord(byte[] dst, int offset, byte mainKey, byte triggerKey)
    {
        dst[offset + 0] = 0x10;
        dst[offset + 1] = 0x00;
        dst[offset + 2] = mainKey;
        dst[offset + 3] = 0x10;
        dst[offset + 4] = 0x00;
        dst[offset + 5] = triggerKey;
    }

    // Builds the 5-chunk 0x55 0xA5 sequence that pushes the LW pair table.
    // `pairs` is the user-facing list of (HID code A, HID code B) pairs;
    // each one fans out to two firmware-level records (A→B and B→A) so
    // either side can act as the held key.
    //
    // Returns the chunks in order. Caller sends them via the gen-2 channel
    // exactly like the 0xA1 KeyTrigger chunks. Zero `pairs` produces an
    // all-zero region (firmware clears the table).
    public static System.Collections.Generic.IEnumerable<byte[]> BuildWriteLwPairsSequence(
        System.Collections.Generic.IReadOnlyList<(byte HidA, byte HidB)> pairs)
    {
        var region = new byte[LW_PAIRS_REGION_SIZE];
        int offset = 0;
        for (int i = 0; i < pairs.Count; i++)
        {
            var (a, b) = pairs[i];
            if (a == 0 || b == 0 || a == b) continue;
            if (offset + LW_PAIR_RECORD_SIZE * 2 > LW_PAIRS_REGION_SIZE) break;
            EncodeLwPairRecord(region, offset, a, b);
            offset += LW_PAIR_RECORD_SIZE;
            EncodeLwPairRecord(region, offset, b, a);
            offset += LW_PAIR_RECORD_SIZE;
        }

        var chunks = new System.Collections.Generic.List<byte[]>(5);
        int dataOffset = 0;
        while (dataOffset < LW_PAIRS_REGION_SIZE)
        {
            int remaining = LW_PAIRS_REGION_SIZE - dataOffset;
            int length = remaining >= WRITE_CHUNK_DATA_MAX ? WRITE_CHUNK_DATA_MAX : remaining;
            bool isLast = (dataOffset + length) == LW_PAIRS_REGION_SIZE;
            ushort address = (ushort)(LW_PAIRS_BASE_ADDR + dataOffset);
            byte addrLo = (byte)(address & 0xFF);
            byte addrHi = (byte)((address >> 8) & 0xFF);
            byte isLastByte = isLast ? (byte)0x01 : (byte)0x00;

            var chunk = new byte[64];
            chunk[0] = REQUEST_MAGIC;
            chunk[1] = SUB_WRITE_LW_PAIRS;
            chunk[2] = 0x00;
            chunk[3] = ComputeWriteChecksum((byte)length, addrLo, addrHi, isLastByte, region.AsSpan(dataOffset, length));
            chunk[4] = (byte)length;
            chunk[5] = addrLo;
            chunk[6] = addrHi;
            chunk[7] = isLastByte;
            System.Array.Copy(region, dataOffset, chunk, 8, length);

            chunks.Add(chunk);
            dataOffset += length;
        }

        return chunks;
    }

    // ── FuncBlock read / write (0x55 0x05 / 0x55 0x06) ──────────────────────
    //
    // FuncBlock is a 64-byte global region at address 0x0040 carrying every
    // firmware-wide toggle: RGB mode, brightness, debounce, polling rate,
    // and — relevant here — the Last Win master enable bit. usb3.pcapng
    // captures the official driver flipping it (frame 7499 baseline vs
    // frame 21301 with LW on): the only byte that changes between the two
    // is the 8th byte of the first read/write chunk's data — going
    // 0x06 → 0x0E (bit 3 set).
    //
    // Read/write are chunked: 56 bytes at 0x0040 + 8 bytes at 0x0078,
    // 64 bytes total. The second chunk carries is_last=1 to commit.
    public const ushort FUNC_BLOCK_PRIMARY_ADDR    = 0x0040;
    public const byte   FUNC_BLOCK_PRIMARY_LENGTH  = 56;
    public const ushort FUNC_BLOCK_CONTINUATION_ADDR   = 0x0078;
    public const byte   FUNC_BLOCK_CONTINUATION_LENGTH = 8;

    // Offset within the FuncBlock primary chunk's data (0..55) of the byte
    // carrying the LW master flag. Bit 3 (0x08) toggles LW; the surrounding
    // bits are other Func flags we preserve via read-modify-write.
    public const int  FUNC_BLOCK_LW_MASTER_BYTE_OFFSET = 8;
    public const byte FUNC_BLOCK_LW_MASTER_BIT         = 0x08;

    // Read request for the FuncBlock. Same envelope as BuildReadBaseBlock,
    // just different sub-cmd. Length is bytes to fetch (max 56 per request),
    // address is the firmware-side byte offset within the FuncBlock region.
    public static byte[] BuildReadFuncBlock(byte length, ushort address)
        => BuildReadRequest(SUB_READ_FUNC_BLOCK, length, address);

    // Write request for one FuncBlock chunk. `data` carries the bytes
    // to commit (up to 56 long); `address` is the firmware-side byte
    // offset within the FuncBlock region; `isLast` marks the final chunk
    // of the read-modify-write sequence so the firmware applies the
    // changes.
    public static byte[] BuildWriteFuncBlockChunk(System.ReadOnlySpan<byte> data, ushort address, bool isLast)
    {
        if (data.Length < 1 || data.Length > WRITE_CHUNK_DATA_MAX)
            throw new System.ArgumentException($"data length must be 1..{WRITE_CHUNK_DATA_MAX}, got {data.Length}", nameof(data));

        byte addrLo = (byte)(address & 0xFF);
        byte addrHi = (byte)((address >> 8) & 0xFF);
        byte isLastByte = isLast ? (byte)0x01 : (byte)0x00;

        var packet = new byte[64];
        packet[0] = REQUEST_MAGIC;
        packet[1] = SUB_WRITE_FUNC_BLOCK;
        packet[2] = 0x00;
        packet[3] = ComputeWriteChecksum((byte)data.Length, addrLo, addrHi, isLastByte, data);
        packet[4] = (byte)data.Length;
        packet[5] = addrLo;
        packet[6] = addrHi;
        packet[7] = isLastByte;
        data.CopyTo(packet.AsSpan(8, data.Length));
        return packet;
    }

    // ── Per-pair LW threshold (0x55 0x09) ───────────────────────────────────
    //
    // Captured in usb3.pcapng frames 18407, 18455 — two packets sent right
    // after the 0xA5 pair table for an A↔D config:
    //
    //   addr=0x086F  data=[0x94, 0x00, 0x27]   (pair direction 0, threshold 0x27)
    //   addr=0x0875  data=[0x94, 0x01, 0x25]   (pair direction 1, threshold 0x25)
    //
    // 6 bytes of address stride per firmware pair. Both packets carry
    // is_last=0x01 (single-shot writes, not chunked). The threshold bytes
    // (0x27 = 39, 0x25 = 37) are the user's configured per-pair sensitivity
    // in 0.01 mm units. For auto-seeded pairs we default to 0x14 (= 0.20 mm,
    // the firmware's actuation floor) which keeps the default LW transition
    // responsive without false-firing.
    public const ushort LW_PAIR_RTP_BASE_ADDR = 0x086F;
    public const int LW_PAIR_RTP_STRIDE = 6;
    public const byte LW_PAIR_RTP_DEFAULT_THRESHOLD = 0x14; // 0.20 mm

    public static byte[] BuildWriteLwPairRtp(byte pairDirectionIndex, byte threshold)
    {
        ushort address = (ushort)(LW_PAIR_RTP_BASE_ADDR + pairDirectionIndex * LW_PAIR_RTP_STRIDE);
        byte addrLo = (byte)(address & 0xFF);
        byte addrHi = (byte)((address >> 8) & 0xFF);
        byte[] data = [0x94, pairDirectionIndex, threshold];
        byte isLast = 0x01;

        var packet = new byte[64];
        packet[0] = REQUEST_MAGIC;
        packet[1] = SUB_WRITE_LW_PAIR_RTP;
        packet[2] = 0x00;
        packet[3] = ComputeWriteChecksum((byte)data.Length, addrLo, addrHi, isLast, data);
        packet[4] = (byte)data.Length;
        packet[5] = addrLo;
        packet[6] = addrHi;
        packet[7] = isLast;
        System.Array.Copy(data, 0, packet, 8, data.Length);
        return packet;
    }
}
