using System.Collections.Generic;
using System.Linq;
using static Driver.Packets;

namespace Driver;

public static class Packets
{
    public enum KeyPointType
    {
        ActuationPoint = 0x01,
        Downstroke = 0x04,
        Upstroke = 0x05
    }

    public sealed record KeyRemapSettingExt
    {
        public required int KeyCmd { get; set; }
        public required int KeyType { get; set; }
        public required int KeyCode { get; set; }
        public required int RtpNumber { get; set; }
        public required int GroupNumber { get; set; }
        public required int PosInGroup { get; set; }
        public required bool RdtEnabled { get; set; }

        public static KeyRemapSettingExt From(KeyRemapSetting keyRemapSetting)
        {
            return new KeyRemapSettingExt
            {
                KeyCmd = keyRemapSetting.KeyCmd,
                KeyType = keyRemapSetting.KeyType,
                KeyCode = keyRemapSetting.KeyCode,
                RtpNumber = 0,
                GroupNumber = 0,
                PosInGroup = 0,
                RdtEnabled = false,
            };
        }
    }


    public readonly static byte REPORT_ID = 0x04;
    public readonly static byte PACKET_SIZE = 63;
    public readonly static byte[] IDENTITY_PACKET = [0xa0, 0x02, .. new byte[61]];
    public readonly static byte[] CLEAR_UP_RTP_PACKET = [0xaa, 0x00, 0x01, .. new byte[60]];
    public readonly static byte[] COMMON_SWITCH_PACKET_BASE = [0xb5, 0x00, 0x1e, 0x01, 0x00, 0x00, 0x01, .. new byte[56]];

    public static byte Clamp(this byte value, byte a, byte b)
        => Math.Max(a, Math.Min(value, b));

    // AP/DS/US wire scale = mm × 100 (verified via WebHID capture of
    // drunkdeer-antler.com on A75 Pro firmware 0x0017, 2026-05-20).
    //
    // Range update (2026-05-22): re-extracted from the newer
    // drunkdeer.keybord.net.cn bundle (see docs/protocol-findings-keybord-net-cn.md).
    // The bundle's clamp constants are:
    //   we = 0.2   → AP minimum
    //   bg = 3.3   → AP maximum  (NOT 2.0 — earlier antler.com slider cap
    //                              was UI-only; firmware accepts up to 3.3 mm)
    //   Xg = 3.1   → DS / US maximum
    // Used as `Math.max(we, Math.min(bg, I)) * 100` and the equivalent for
    // DS/US. We were artificially clamping the entire sensitivity range to
    // 2.0 mm; raising to the firmware-accepted max restores the missing
    // 1.0-3.3 mm AP zone (users on stiffer switches reported the upper
    // range felt missing). Range is byte 0..255 so 3.3 mm at × 100 fits
    // with headroom.
    // Rounding note: at the mm × 100 wire scale a 1-byte value caps out
    // at 255 (= 2.55 mm). The JS clamps to `bg = 3.3 mm` in UI but the
    // byte assignment after the multiplication is a JS implicit truncation
    // that would silently corrupt values > 2.55 mm. Capping at 255 here
    // is the highest value that survives the wire format intact. To get
    // the full 3.30 mm AP range we'd need the NewHighPrec dialect
    // (0xFD packets, 2 bytes/key, mm × 200) — that's A75 Ultra / Master
    // only on current firmware.
    public const byte AP_BYTE_MIN = 20;   // 0x14 = 0.20 mm
    public const byte AP_BYTE_MAX = 255;  // 0xFF = 2.55 mm
    public const byte DS_BYTE_MIN = 0;
    public const byte DS_BYTE_MAX = 255;  // 0xFF = 2.55 mm
    public const byte US_BYTE_MIN = 0;
    public const byte US_BYTE_MAX = 255;  // 0xFF = 2.55 mm

    public static byte GetActuationPoint(this Profile profile, int index)
        => ((byte)Math.Clamp((int)(profile.Keys_Array[index].Action_Point * 100), 0, 255)).Clamp(AP_BYTE_MIN, AP_BYTE_MAX);

    public static byte GetDownstrokePoint(this Profile profile, int index)
        => ((byte)Math.Clamp((int)(profile.Keys_Array[index].Downstroke * 100), 0, 255)).Clamp(DS_BYTE_MIN, DS_BYTE_MAX);

    public static byte GetUpstrokePoint(this Profile profile, int index)
        => ((byte)Math.Clamp((int)(profile.Keys_Array[index].Upstroke * 100), 0, 255)).Clamp(US_BYTE_MIN, US_BYTE_MAX);

    // correct order -->
    // remap packets
    // clear packet
    // rtp packets (authorityPacket -> downLoadPacket)
    // common switch packet

    // VERIFIED common switch packet byte map (extracted from the official
    // DrunkDeer driver at drunkdeer-antler.com — `sendCommonData` function in
    // index.js, decompiled 2026-05-09). Replaces an earlier comment that
    // speculated about an "if valueT == 1" conditional which does not exist
    // in the production code.
    //
    //   B[0..6] = 0xb5 0x00 0x1e 0x01 0x00 0x00 0x01      // fixed header
    //   B[7]    = turboMode          (0 = off, 1 = on)
    //   B[8]    = rapidTriggerMode   (0 = off, 1 = on)
    //   B[9]    = 0x00               // reserved
    //   B[10]   = LW + RDT combined enum:
    //                0 = neither
    //                1 = Last Win only
    //                2 = Release Dual-Trigger only
    //                3 = both
    //   B[11]   = RTMatch            (0 = off, 1 = on)
    //   B[12..62] = 0x00
    //
    // UI-level conflicts (enforced in the official driver, not the firmware):
    //   - Turbo and Keystroke Tracking are mutually exclusive
    //   - The other six flags can co-exist freely in any combination
    //
    // Three more global toggles live outside this packet — see ProfileSettings
    // doc in Driver/Profile.cs for the headers (0xFD 0x03 ..., 0xFC 0x0B ...,
    // 0xFD 0x0C ...). Those builders land in Phase C+.

    // clear up rtp packet = 0xAA, 0x00, 0x01, 0x00...x60

    // Part of remap commands - buildPkt_remap_key_array

    private static void AdjustPacketRemapping(this KeyRemapSettingExt setting, byte[] packet, int charNumber, int zeroPos)
    {
        packet[zeroPos] = (byte)charNumber;
        switch (setting.KeyType)
        {
            case 0:
                {
                    packet[zeroPos + 1] = (byte)setting.KeyCmd;
                    packet[zeroPos + 3] = (byte)setting.KeyCode;
                }
                break;
            case 1:
            case 2:
                {
                    packet[zeroPos + 1] = (byte)setting.KeyCmd;
                    packet[zeroPos + 2] = (byte)setting.KeyCode;
                }
                break;
            case 3:
                {
                    packet[zeroPos + 1] = (byte)setting.KeyCmd;
                }
                break;
            case 4:
                {
                    packet[zeroPos + 1] = 0xf8;
                    packet[zeroPos + 2] = (byte)setting.RtpNumber;
                    packet[zeroPos + 3] = (byte)setting.GroupNumber;
                    packet[zeroPos + 4] = (byte)setting.PosInGroup;
                    packet[zeroPos + 5] = (byte)(setting.RdtEnabled ? 0x01 : 0x00);
                }
                break;
        }
    }

    private static KeyRemapSettingExt[] MergeRemaps(this RemapProfile remapProfile)
    {
        return remapProfile.KeyCodeDefault.Select(KeyRemapSettingExt.From).ToArray();
    }

    // Public remap-packet builder used by the new keyboard view's Remap tab.
    // Takes a fixed-length 126-entry array of HID usage codes — element i is
    // the new keycode for firmware slot i, or 0 if that slot should keep its
    // factory default.
    //
    // Wire format mirrors `sendRemapKeyData` in the official JS bundle: the
    // 9 entries per packet are PACKED (`r` only advances for non-default
    // slots), NOT laid out at fixed 6-byte offsets. KeyType=1 ("regular
    // keyboard usage") gives a 3-byte entry: [slot, cmd, code], padded to
    // 6 bytes total stride. KeyCmd is always 1 for plain key remaps.
    //
    // The legacy `BuildPacketsRemapping` uses fixed offsets, which works for
    // profile saves where every slot has a (default-or-not) entry written —
    // but the firmware reads sequentially, so partial remaps with a single
    // entry need to be packed at the head of the entry region. Trying to
    // share that path resulted in the firmware reading the entry as the
    // wrong slot.
    public static byte[][] BuildRemapPackets(byte[] hidUsageBySlot, byte group = 1)
    {
        if (hidUsageBySlot.Length != 126)
            throw new ArgumentException("expected 126 slots", nameof(hidUsageBySlot));

        // `group` (packet[5] in the JS) selects the layer-group: 1 = default
        // mapping, 2 = Fn1, 3 = Fn2.
        byte[] _packet = [0xa0, 0x02, 0x04, 0x00, 0x0e, group, .. new byte[54], 0xa5, .. new byte[2]];
        var packets = new List<byte[]>(14);
        for (int pktNumber = 1; pktNumber <= 14; pktNumber++)
        {
            byte[] packet = (byte[])_packet.Clone();
            packet[3] = (byte)pktNumber;
            // Emit ALL 9 entries at fixed 6-byte offsets. Empirically the
            // firmware indexes entries by POSITION within the packet's entry
            // region — not by the slot byte each entry carries. Skipping
            // empty/gap slots (as the JS appears to with `if(k.keyCmd)`)
            // shifts every subsequent entry left and corrupts the keymap
            // (e.g. physical "1" typing "4", Enter typing PgDn). The JS
            // bundle never actually skips because every slot in its in-memory
            // keymap carries a default keyCmd=0xFC — for our partial layout,
            // gap slots simply get code=0 which the firmware writes through
            // without effect (they have no physical key on this model).
            //
            // KeyType=0 ("plain HID keyboard key") byte layout per JS
            // sendRemapKeyData:  [slot, cmd, 0, code, 0, 0]
            for (int w = 0; w < 9; w++)
            {
                int s = (pktNumber - 1) * 9 + w;
                if (s >= 126) break;
                int r = 6 + w * 6;
                packet[r + 0] = (byte)s;           // slot index
                packet[r + 1] = 0xFC;              // keyCmd for plain HID
                packet[r + 2] = 0x00;              // filler
                packet[r + 3] = hidUsageBySlot[s]; // HID usage code (0 = no key)
                packet[r + 4] = 0x00;              // filler
                packet[r + 5] = 0x00;              // filler
            }
            packets.Add(packet);
        }
        return [.. packets];
    }

    private static byte[][] BuildPacketsRemapping(this ProfileItem profileItem, byte layer = 1)
    {
        List<byte[]> packets = [];
        if (profileItem.RemapProfile is null)
            return [.. packets];
        var keyRemappings = profileItem.RemapProfile.MergeRemaps();
        if (keyRemappings.Length == 0 || keyRemappings.Any(k => k is null))
            return [.. packets];
        if (keyRemappings.Length != 126 || keyRemappings.Any(i => i is null)) throw new Exception(string.Format("Malformed keyremappings, {0}", string.Join<KeyRemapSettingExt>(',', keyRemappings)));
        for (int i = 0; i < (profileItem.Profile.RTP?.Lw_RtpSettings.Length ?? 0); i++)
        {
            var lwSetting = profileItem.Profile.RTP!.Lw_RtpSettings[i];
            if (lwSetting.MainKey?.Value is { } keyindex1 && lwSetting.TriggerKey?.Value is { } keyindex2)
            {
                var keyRemapping1 = keyRemappings[keyindex1];
                keyRemapping1.RdtEnabled = lwSetting.MainKey.Rdt;
                keyRemapping1.GroupNumber = i;
                keyRemapping1.RtpNumber = 0; // ?
                keyRemapping1.PosInGroup = 0;
                keyRemapping1.KeyType = 4;

                var keyRemapping2 = keyRemappings[keyindex2];
                keyRemapping2.RdtEnabled = lwSetting.TriggerKey.Rdt;
                keyRemapping2.GroupNumber = i;
                keyRemapping2.RtpNumber = 0; // ?
                keyRemapping2.PosInGroup = 1;
                keyRemapping2.KeyType = 4;
            }
        }
        byte[] _packet = [0xa0, 0x02, 0x04, 0x00, 0x0e, layer, .. new byte[54], 0xa5, ..new byte[2]];
        for (int pktNumber = 1; pktNumber <= 14; pktNumber++)
        {
            byte[] packet = (byte[])_packet.Clone();
            packet[3] = (byte)pktNumber;
            for (int charNumber = 0; charNumber < 9; charNumber++)
            {
                int i = (pktNumber - 1) * 9 + charNumber;
                if (i >= 126)
                {
                    break;
                }
                var keyDesc = keyRemappings[i];
                if (keyDesc.KeyCmd > 0)
                {
                    keyDesc.AdjustPacketRemapping(packet, i, 6 + charNumber * 6);
                }
            }
            packets.Add(packet);
        }
        return [.. packets];
    }

    // VERIFIED against `sendRTPAuthorityData` in the official JS bundle:
    //   C[0]=0xA7, C[1]=rtpNumber, C[2]=0x00, C[3]=0x2B, C[4]=0x01
    //
    // NOTE: an earlier private copy of this builder had byte[0] = 0x07 (a
    // typo dating back to the original RT+ implementation). Firmware did
    // not echo those packets back, which contributed to LW/remap commits
    // appearing accepted-but-ignored. The correct first byte is 0xA7.
    public static byte[] BuildPacketRTPAuthority(byte rtpNumberInGroup)
    {
        byte[] packet = [0xA7, rtpNumberInGroup, 0x00, 0x2b, 0x01, .. new byte[58]];
        return packet;
    }

    // VERIFIED against `sendRTPAuthorityDownloadData(rtpNumber, keyCode)` in
    // the official JS bundle:
    //   E[0]=0xA8, E[1]=rtpNumber, E[2..6]=01 01 01 0x26 01
    //   E[37]=0x02, E[38]=keycode, E[39]=0x01, E[40]=0x00
    //   E[41]=0x6D, E[42]=0x03, E[43]=keycode
    public static byte[] BuildPacketRTPAuthorityDownload(byte rtpNumberInGroup, byte keycode)
    {
        byte[] packet = [0xa8, rtpNumberInGroup, 0x01, 0x01, 0x01, 0x26, 0x01, .. new byte[30], 0x02, keycode, 0x01, 0x00, 0x6d, 0x03, keycode, .. new byte[19]];
        return packet;
    }

    // Pre-RTP-Authority clear packet — distinct from BuildClearRtpPacket
    // (the [0xFC, 0x0A, mode] LW-pair clear). This one is sent BETWEEN the
    // 42-packet full remap stream and the per-key RTPAuthority+Download
    // pairs in the official rtpSaveToKeyboard flow. Wire format:
    //   [0xAA, 0x00, 0x01, 0x00 × 60]
    // The firmware uses this as a "begin RTP authority batch" sentinel.
    public static byte[] BuildClearRtpUpperPacket() => [.. CLEAR_UP_RTP_PACKET];

    // Full remap stream — replicates `remapSaveToKeyboard` in the official
    // driver. Sends 3 layer groups × 14 packets each = 42 packets covering
    // all 126 slots for the default, Fn1 and Fn2 layers.
    //
    // The current keyboard view only edits group=1 (default layer), so for
    // groups 2 and 3 we pass an all-zero usage map, which produces packets
    // with the standard header + tail marker but no entries in the entry
    // region. The firmware needs to see these empty Fn-layer packets to
    // commit the default-layer changes — without them, our single-group
    // remap sat in firmware RAM but was never committed to its remap
    // table. (Confirmed against `remapSaveToKeyboard` in the JS bundle.)
    public static byte[][] BuildFullRemapSequence(byte[] hidUsageBySlot)
    {
        if (hidUsageBySlot.Length != 126)
            throw new ArgumentException("expected 126 slots", nameof(hidUsageBySlot));

        var empty = new byte[126];
        var all = new List<byte[]>(42);
        all.AddRange(BuildRemapPackets(hidUsageBySlot, group: 1));
        all.AddRange(BuildRemapPackets(empty,          group: 2));
        all.AddRange(BuildRemapPackets(empty,          group: 3));
        return [.. all];
    }

    // Typed remap entry — the 5 bytes following the slot byte in a remap
    // packet. Two shapes coexist in the firmware's wire format, gated by
    // the KeyCmd byte:
    //   KeyType=0 (HID key)  : [slot, 0xFC, 0,         HID code,  0,            0]
    //   KeyType=4 (LW/RDT)   : [slot, 0xF8, rtpNumber, group#,    posInGrp,     rtModeFlag]
    // KeyCmd=0 means "no entry" — firmware writes nothing to this slot.
    // Verified against `sendRemapKeyData` case 0 / case 4 in dd-index.js.
    //
    // B5 (the "rtModeFlag" param of Pair below) is the global Rapid Trigger
    // mode flag — `Number(I.keyboardObj.rapidTriggerMode)` in the JS. So
    // ALL Type-4 entries on a single sync share the same B5 value: 1 when
    // RT is enabled globally (which both LW and RDT require), 0 otherwise.
    // It is NOT a per-pair LW-vs-RDT discriminator — that lives on Common
    // Switch byte 10. A previous version of this comment claimed B5 was
    // "rdtEnabled" because the JS variable name suggests it, but the source
    // it's assigned from is rapidTriggerMode.
    public readonly record struct RemapEntry(byte KeyCmd, byte B2, byte B3, byte B4, byte B5)
    {
        public static readonly RemapEntry Empty = new(0, 0, 0, 0, 0);
        public static RemapEntry Hid(byte hidCode) => new(0xFC, 0, hidCode, 0, 0);
        public static RemapEntry Pair(byte rtpNumber, byte groupNumber, byte posInGroup, byte rtModeFlag)
            => new(0xF8, rtpNumber, groupNumber, posInGroup, rtModeFlag);
    }

    // Typed counterpart of BuildRemapPackets. Mixes HID-key (KeyType=0)
    // entries and LW/RDT pair (KeyType=4) entries per slot — required for
    // LW pairs to actually activate on hardware. See the JS bundle's
    // `O(X.value)` / `rtpSaveToKeyboard` flow.
    public static byte[][] BuildRemapPacketsTyped(RemapEntry[] entries, byte group = 1)
    {
        if (entries.Length != 126)
            throw new ArgumentException("expected 126 slots", nameof(entries));

        byte[] _packet = [0xa0, 0x02, 0x04, 0x00, 0x0e, group, .. new byte[54], 0xa5, .. new byte[2]];
        var packets = new List<byte[]>(14);
        for (int pktNumber = 1; pktNumber <= 14; pktNumber++)
        {
            byte[] packet = (byte[])_packet.Clone();
            packet[3] = (byte)pktNumber;
            for (int w = 0; w < 9; w++)
            {
                int s = (pktNumber - 1) * 9 + w;
                if (s >= 126) break;
                int r = 6 + w * 6;
                var entry = entries[s];
                packet[r + 0] = (byte)s;
                packet[r + 1] = entry.KeyCmd;
                packet[r + 2] = entry.B2;
                packet[r + 3] = entry.B3;
                packet[r + 4] = entry.B4;
                packet[r + 5] = entry.B5;
            }
            packets.Add(packet);
        }
        return [.. packets];
    }

    public static byte[][] BuildFullRemapSequenceTyped(RemapEntry[] entries)
    {
        if (entries.Length != 126)
            throw new ArgumentException("expected 126 slots", nameof(entries));

        var empty = new RemapEntry[126];
        var all = new List<byte[]>(42);
        all.AddRange(BuildRemapPacketsTyped(entries, group: 1));
        all.AddRange(BuildRemapPacketsTyped(empty,   group: 2));
        all.AddRange(BuildRemapPacketsTyped(empty,   group: 3));
        return [.. all];
    }

    private static byte[][] BuildPacketsRapidTriggerPlusSettings(this ProfileItem profileItem)
    {
        List<byte[]> packets = [];
        if (profileItem.RemapProfile is null || profileItem.Profile.RTP is null)
        {
            return [.. packets];
        }

        var settings = profileItem.Profile.RTP.Rdt_RtpSettings ?? [];
        var keycodes = profileItem.RemapProfile.KeyCodeDefault.ToDictionary(kc => kc.KeyIndex, kc => (byte)kc.KeyCode);
        for (int i = 0; i < settings.Length; i++)
        {
            if (settings[i] is not { } rtpSetting || !keycodes.TryGetValue(rtpSetting.MainKey?.Value ?? -1, out var keycode)) continue;
            packets.Add(BuildPacketRTPAuthority((byte)(i + 1)));
            packets.Add(BuildPacketRTPAuthorityDownload((byte)(i + 1), keycode));
        }
        return [.. packets];
    }

    public static byte[] BuildCommonSwitchPacket(ProfileSettings settings)
    {
        byte[] packet = [.. COMMON_SWITCH_PACKET_BASE];
        packet[7]  = settings.TurboEnabled        ? (byte)0x01 : (byte)0x00;
        packet[8]  = settings.RapidTriggerEnabled ? (byte)0x01 : (byte)0x00;
        // packet[9] stays 0
        packet[10] = (settings.LastWinEnabled, settings.ReleaseDualTriggerEnabled) switch
        {
            (true, true)   => (byte)0x03,
            (true, false)  => (byte)0x01,
            (false, true)  => (byte)0x02,
            (false, false) => (byte)0x00,
        };
        packet[11] = settings.RTMatchEnabled ? (byte)0x01 : (byte)0x00;
        return packet;
    }

    // VERIFIED byte maps for the three "outlier" global toggles — extracted
    // from the official DrunkDeer driver at drunkdeer-antler.com (functions
    // `sendTrackingStartData`, `sendTrackingStopData`, `sendLwReplaceData`,
    // `sendRtModeDate` in index.js, decompiled 2026-05-09).
    //
    // ⚠ Tracking start/stop has TWO firmware variants in the bundle, gated by
    // `precision === 2 && !oldOpenHighPrecision`:
    //   • New/high-precision firmware → [0xFD, 0x03, v]
    //   • Old/legacy firmware         → [0xB6, 0x03, v]
    // A75 Pro factory firmware 0x0009 is the OLD variant — sending 0xFD on
    // it produces no streaming packets (firmware silently ignores). The
    // streaming side (`HidStreamListener`) already parses both shapes, so
    // we just have to pick the right enable byte. Until we wire precision
    // detection from the spec response, default to 0xB6 (the variant that
    // works on the only firmware we've actually shipped against).
    //
    //   Last Win Replace       : [0xFC, 0x0B, v,    0x00 × 60]   v ∈ {0,1}
    //     ↳ consumes ProfileSettings.LastWinReplaceEnabled
    //
    //   Auto-Match (RT) Mode   : [0xFD, 0x0C, v,    0x00 × 60]   v ∈ 0..255
    //     ↳ consumes ProfileSettings.AutoMatchMode  (255 = off)
    //
    // These are global device settings, not per-profile RTP/remap data — they
    // sit outside BuildCommonSwitchPacket because their headers differ. Call
    // sites should send them every Sync alongside the common-switch packet.
    public static byte[] BuildKeystrokeTrackingPacket(bool enabled)
    {
        byte[] packet = new byte[PACKET_SIZE];
        packet[0] = 0xB6;
        packet[1] = 0x03;
        packet[2] = (byte)(enabled ? 0x01 : 0x00);
        return packet;
    }

    public static byte[] BuildLastWinReplacePacket(bool enabled)
    {
        byte[] packet = new byte[PACKET_SIZE];
        packet[0] = 0xFC;
        packet[1] = 0x0B;
        packet[2] = (byte)(enabled ? 0x01 : 0x00);
        return packet;
    }

    public static byte[] BuildAutoMatchModePacket(byte mode)
    {
        byte[] packet = new byte[PACKET_SIZE];
        packet[0] = 0xFD;
        packet[1] = 0x0C;
        packet[2] = mode;
        return packet;
    }

    // Clears any previously-registered LW/RDT pairs and primes the firmware
    // to accept the new pair table. From sendClearRtpData(B) in the JS bundle:
    //   [0xFC, 0x0A, mode, 0, 0, ...]
    // `mode` is the same LW+RDT combined enum used in Common Switch byte[10]:
    //   0 = none, 1 = LW only, 2 = RDT only, 3 = both.
    // Send this BEFORE BuildCreateLwPairsPacket — the official driver does
    // the same sequence (clear, then pairs, then common-switch).
    public static byte[] BuildClearRtpPacket(byte mode)
    {
        byte[] packet = new byte[PACKET_SIZE];
        packet[0] = 0xFC;
        packet[1] = 0x0A;
        packet[2] = mode;
        return packet;
    }

    // Registers Last-Win key pairs with the firmware. The Common Switch
    // packet's LW bit is just the master switch; without pairs registered
    // here, the firmware has nothing to deconflict.
    //
    // Wire format (from sendCreateLwData in the official JS bundle):
    //   [0xFC, 0x01, 0x00, pairCount,
    //    main0, trigger0, 0x00, 0x00,
    //    main1, trigger1, 0x00, 0x00, ...]
    // Each value is the keyboard slot index (0..125), matching what
    // BuildPacketKeyPoint uses.
    //
    // For bidirectional behavior (A→D switches to D AND D→A switches to A)
    // pass both pairings (A,D) and (D,A).
    public static byte[] BuildCreateLwPairsPacket(IReadOnlyList<(byte MainKey, byte TriggerKey)> pairs)
    {
        byte[] packet = new byte[PACKET_SIZE];
        packet[0] = 0xFC;
        packet[1] = 0x01;
        packet[2] = 0x00;
        packet[3] = (byte)pairs.Count;
        for (int i = 0; i < pairs.Count; i++)
        {
            int off = 4 + i * 4;
            if (off + 1 >= PACKET_SIZE) break;
            packet[off]     = pairs[i].MainKey;
            packet[off + 1] = pairs[i].TriggerKey;
            // packet[off + 2] and [off + 3] stay 0
        }
        return packet;
    }

    // Registers Release-Dual-Trigger key pairs with the firmware.
    //
    // History: the 2026-05-20 WebHID intercept of the OLDER drunkdeer-antler.com
    // bundle suggested this packet wasn't emitted during RDT save, and our
    // initial implementation routed RDT entirely through the remap stream
    // (Type-4 entries + RtpAuthority/Download + per-key US > 0 on the
    // release slot). That worked for single-pair RDT but produced the
    // phantom-fires-on-release symptom after profile-switching observed
    // 2026-05-22 — the firmware's pair table ended up in a half-configured
    // state that bled across profiles.
    //
    // Re-examination of the NEWER drunkdeer.keybord.net.cn bundle
    // (2026-05-22, see docs/protocol-findings-keybord-net-cn.md) shows
    // sendCreateRdtData IS called unconditionally for RDT-mode syncs,
    // with per-pair active/reset thresholds. We now emit it.
    //
    // Wire format (from sendCreateRdtData in the keybord.net.cn bundle):
    //   [0xFC, 0x03, pairCount,
    //    press0, release0, active0_lo, active0_hi, reset0_lo, reset0_hi, 0, 0,
    //    press1, release1, ...]
    //
    // Thresholds are 16-bit values at firmware scale (mm × 200, same as
    // the high-precision keystroke-tracking stream):
    //   activeRaw: clamped to 10..50  → physical 0.05..0.25 mm (press
    //              depth at which the press output fires)
    //   resetRaw:  clamped to 300..500 → physical 1.5..2.5 mm (release
    //              depth at which the release output fires)
    //
    // Defaults (activeRaw=20, resetRaw=400 → 0.1 mm / 2.0 mm) match the
    // values the official tool ships its initial pair config with. We
    // don't yet expose per-pair customization in our UI; defaults stay
    // until we add a threshold editor.
    public static byte[] BuildCreateRdtPairsPacket(
        IReadOnlyList<(byte PressSlot, byte ReleaseSlot)> pairs,
        ushort activeRaw = 20,
        ushort resetRaw = 400)
    {
        byte[] packet = new byte[PACKET_SIZE];
        packet[0] = 0xFC;
        packet[1] = 0x03;
        packet[2] = (byte)pairs.Count;

        activeRaw = (ushort)Math.Clamp((int)activeRaw, 10, 50);
        resetRaw  = (ushort)Math.Clamp((int)resetRaw, 300, 500);
        byte aLo = (byte)(activeRaw & 0xFF);
        byte aHi = (byte)((activeRaw >> 8) & 0xFF);
        byte rLo = (byte)(resetRaw & 0xFF);
        byte rHi = (byte)((resetRaw >> 8) & 0xFF);

        for (int i = 0; i < pairs.Count; i++)
        {
            int off = 3 + i * 8;
            if (off + 7 >= PACKET_SIZE) break;
            packet[off + 0] = pairs[i].PressSlot;
            packet[off + 1] = pairs[i].ReleaseSlot;
            packet[off + 2] = aLo;
            packet[off + 3] = aHi;
            packet[off + 4] = rLo;
            packet[off + 5] = rHi;
            // packet[off + 6] and [off + 7] stay 0
        }
        return packet;
    }

    public static byte[][] BuildPacketsRapidTriggerPlus(this ProfileItem profileItem)
    {
        List<byte[]> packets = [];
        if (profileItem.Profile.RTP is null || profileItem.RemapProfile is null) // For now needs a remapping to figure out keys on the keyboard
        {
            return [.. packets];
        }
        packets.AddRange(profileItem.BuildPacketsRemapping());
        packets.Add(CLEAR_UP_RTP_PACKET);
        packets.AddRange(profileItem.BuildPacketsRapidTriggerPlusSettings());
        packets.Add(BuildCommonSwitchPacket(profileItem.Profile.Settings));
        return [.. packets];
    }

    // OldHighPrec dialect — 0xB6 packets, 1 byte/key at mm × 100. 3 packets
    // (59 + 59 + 8 keys). Verified on A75 Pro firmware 0x0017. See
    // WirePrecision enum in FirmwareCapabilities.cs for the full dialect
    // matrix; this is the "old packet ID, new high-precision data scale"
    // case the firmware accepts after gaining its per-model precision
    // upgrade (A75 base ≥ 0x23, A75 Pro ≥ 0x11, A75 ISO ≥ 0x19).
    public static byte[] BuildPacketKeyPoint(this Profile profile, byte packetNumber, KeyPointType keyPointType)
    {
        var packet = new byte[PACKET_SIZE];
        packet[0] = 0xb6;
        packet[1] = ((byte)keyPointType);
        packet[2] = 0x00;
        packet[3] = packetNumber; // 0,1,2

        var offset = packetNumber switch
        {
            0 => 0,
            1 => 59,
            _ => 118,
        };

        var max_x = packetNumber switch
        {
            2 => 8,
            _ => 59
        };

        Func<int, byte> getValue = keyPointType switch
        {
            KeyPointType.ActuationPoint => i => profile.GetActuationPoint(i),
            KeyPointType.Downstroke => i => profile.GetDownstrokePoint(i),
            KeyPointType.Upstroke => i => profile.GetUpstrokePoint(i),
            _ => throw new NotImplementedException(),
        };

        for (int x = 0; x < max_x; x++)
        {
            var value = getValue(x + offset);
            packet[4 + x] = value;
        }

        return packet;
    }

    // Legacy dialect — 0xB6 packets, 1 byte/key at mm × 10. 3 packets
    // (59 + 59 + 8 keys). For older firmwares that don't support the
    // mm × 100 "high precision" interpretation: G65 (always per JS
    // bundle), plus A75 / A75 Pro / A75 ISO units below their respective
    // precision-upgrade firmware cutoffs. Each byte represents 0.1 mm,
    // so the effective range is 0..25.5 mm at 1/10 mm steps.
    public static byte[] BuildPacketKeyPointLegacy(this Profile profile, byte packetNumber, KeyPointType keyPointType)
    {
        var packet = new byte[PACKET_SIZE];
        packet[0] = 0xb6;
        packet[1] = (byte)keyPointType;
        packet[2] = 0x00;
        packet[3] = packetNumber;
        var offset = packetNumber switch { 0 => 0, 1 => 59, _ => 118 };
        var max_x = packetNumber switch { 2 => 8, _ => 59 };
        Func<int, byte> getValue = keyPointType switch
        {
            KeyPointType.ActuationPoint => i => (byte)Math.Clamp((int)(profile.Keys_Array[i].Action_Point * 10), 0, 255),
            KeyPointType.Downstroke    => i => (byte)Math.Clamp((int)(profile.Keys_Array[i].Downstroke    * 10), 0, 255),
            KeyPointType.Upstroke      => i => (byte)Math.Clamp((int)(profile.Keys_Array[i].Upstroke      * 10), 0, 255),
            _ => throw new NotImplementedException(),
        };
        for (int x = 0; x < max_x; x++) packet[4 + x] = getValue(x + offset);
        return packet;
    }

    // NewHighPrec dialect — 0xFD packets, 2 bytes/key (lo + hi) at mm × 200.
    // 5 packets (30 × 4 + 6 keys). True high-precision wire format —
    // same 1/200 mm resolution as the high-precision keystroke-tracking
    // stream. Used by A75 Ultra (TypeCode 756) and A75 Master (TypeCode
    // 757). The firmware silently rejects 0xB6 packets on these models.
    public static byte[] BuildPacketKeyPointHighPrec(this Profile profile, byte packetNumber, KeyPointType keyPointType)
    {
        var packet = new byte[PACKET_SIZE];
        packet[0] = 0xFD;
        packet[1] = (byte)keyPointType;  // 0x01 AP / 0x04 DS / 0x05 US
        packet[2] = packetNumber;         // 0..4
        int offset = packetNumber * 30;
        int max_x = packetNumber < 4 ? 30 : 6;
        Func<int, decimal> getMm = keyPointType switch
        {
            KeyPointType.ActuationPoint => i => profile.Keys_Array[i].Action_Point,
            KeyPointType.Downstroke    => i => profile.Keys_Array[i].Downstroke,
            KeyPointType.Upstroke      => i => profile.Keys_Array[i].Upstroke,
            _ => throw new NotImplementedException(),
        };
        int r = 0;
        for (int x = 0; x < max_x; x++)
        {
            int raw = Math.Clamp((int)(getMm(x + offset) * 200), 0, 65535);
            packet[3 + r] = (byte)(raw & 0xFF);
            packet[4 + r] = (byte)((raw >> 8) & 0xFF);
            r += 2;
        }
        return packet;
    }

    // Dispatch one keypoint-type's full per-key batch (AP, DS, or US)
    // according to the firmware's wire dialect. Returns 3 packets for
    // Legacy / OldHighPrec, 5 for NewHighPrec. The caller concatenates
    // the three batches (AP + DS + US) to build the full keypoint
    // section of a profile sync.
    public static byte[][] BuildKeyPointBatch(this Profile profile, WirePrecision precision, KeyPointType keyPointType)
    {
        return precision switch
        {
            WirePrecision.NewHighPrec =>
            [
                profile.BuildPacketKeyPointHighPrec(0, keyPointType),
                profile.BuildPacketKeyPointHighPrec(1, keyPointType),
                profile.BuildPacketKeyPointHighPrec(2, keyPointType),
                profile.BuildPacketKeyPointHighPrec(3, keyPointType),
                profile.BuildPacketKeyPointHighPrec(4, keyPointType),
            ],
            WirePrecision.Legacy =>
            [
                profile.BuildPacketKeyPointLegacy(0, keyPointType),
                profile.BuildPacketKeyPointLegacy(1, keyPointType),
                profile.BuildPacketKeyPointLegacy(2, keyPointType),
            ],
            _ =>  // OldHighPrec — existing/verified path
            [
                profile.BuildPacketKeyPoint(0, keyPointType),
                profile.BuildPacketKeyPoint(1, keyPointType),
                profile.BuildPacketKeyPoint(2, keyPointType),
            ],
        };
    }

    public static byte[][] BuildPackets(this ProfileItem profileItem)
    {
        List<byte[]> packets = [];
        var profile = profileItem.Profile;
        packets.Add(profile.BuildPacketKeyPoint(0, KeyPointType.ActuationPoint));
        packets.Add(profile.BuildPacketKeyPoint(1, KeyPointType.ActuationPoint));
        packets.Add(profile.BuildPacketKeyPoint(2, KeyPointType.ActuationPoint));
        packets.Add(profile.BuildPacketKeyPoint(0, KeyPointType.Downstroke));
        packets.Add(profile.BuildPacketKeyPoint(1, KeyPointType.Downstroke));
        packets.Add(profile.BuildPacketKeyPoint(2, KeyPointType.Downstroke));
        packets.Add(profile.BuildPacketKeyPoint(0, KeyPointType.Upstroke));
        packets.Add(profile.BuildPacketKeyPoint(1, KeyPointType.Upstroke));
        packets.Add(profile.BuildPacketKeyPoint(2, KeyPointType.Upstroke));

        packets.AddRange(profileItem.BuildPacketsRapidTriggerPlus());
        return [.. packets];
    }

    // The structured packet bundle for a full profile push. Lays out the
    // packets in the exact order the firmware expects them — see
    // BuildFullProfilePackets for the build, HidDeviceExtensions.WriteFullProfile
    // for the wire walk, and docs/keyboard-protocol.md §11 for the
    // rtpSaveToKeyboard sequence we're mirroring.
    public sealed record FullProfilePackets(
        byte[]? PreQuiesce,
        byte[][] Remap,
        byte[][]? RtpRemapReflush,
        byte[]? ClearRtpUpper,
        byte[][] RtpAuthority,
        // EarlyCommonSwitch is the same CommonSwitchPacket that's also at the
        // end of AckedBatch, sent BEFORE ClearRtp/LwPairs so the firmware
        // has the correct LW/RDT mode bits when it processes the pair table.
        // Without this, the firmware can be in PreQuiesce state (LW=off,
        // RDT=off) when the pair-table packet arrives and silently ignore
        // it. Only emitted when LW or RDT pairs are present; otherwise the
        // trailing CS in AckedBatch is sufficient.
        byte[]? EarlyCommonSwitch,
        // PrePassClearRtpUpper — an additional `aa 00 01` sent BEFORE the
        // remap stream when RDT or LW pairs are active. The default
        // ClearRtpUpper between remap and RtpAuthority isn't enough to flush
        // stale rtpNumber→HID-code mappings from a previous profile. Without
        // this pre-clear, switching from profile A (R→T) to profile B (Y→U)
        // can leave the firmware emitting T on Y release because the
        // rtpNumber=1 entry is still cached from profile A.
        byte[]? PrePassClearRtpUpper,
        // ClearRtp + LwPairs are nullable as of 2.2.0 — sent only when LW
        // is enabled with pairs, matching the web driver behaviour observed
        // in tools/captures/0x17/. RDT-only mode skips both (the official
        // driver does the same — see WebHID intercept 2026-05-20). Pre-2.2.0
        // always sent them with mode=0 / pairCount=0, which on some firmwares
        // can clobber per-key state.
        byte[]? ClearRtp,
        // ClearRtpAll — `fc 0a 00` (mode=none) sent IMMEDIATELY before
        // ClearRtp to fully wipe the firmware's pair table on every
        // pair-bearing sync. The mode-specific clear (`fc 0a 02` for RDT,
        // `fc 0a 01` for LW) sets the new mode but does NOT reliably
        // clear stale rtpNumber→HID-code mappings from the previous
        // profile. Verified 2026-05-21 on firmware 0x0017 — without this,
        // switching from profile A (with a pair) to profile B (with a
        // different pair) leaves profile B's press-key firing profile
        // A's release HID. Adding the leading `fc 0a 00` resets the
        // firmware to a clean state before the new mode + pair table
        // land.
        byte[]? ClearRtpAll,
        byte[]? LwPairs,
        // RdtPairs (`fc 03`) — sent when RDT is enabled with pairs, mirrors
        // the keybord.net.cn bundle behaviour discovered 2026-05-22 (see
        // docs/protocol-findings-keybord-net-cn.md). Without this, the
        // firmware ends up in a half-configured state and bleeds stale
        // release HID across profile switches (phantom-fires-on-release
        // symptom). Carries per-pair active/reset thresholds.
        byte[]? RdtPairs,
        byte[][] AckedBatch,
        byte[][] FireForget,
        // True when this bundle carries any LW or RDT pair entries
        // (Type-4 in the remap stream + matching RtpAuthority pairs).
        // Used by ProfileManager.PushCurrentProfileAsync to decide whether
        // the 600ms commit re-push should fire AND to detect pair-bearing
        // transitions for the _lastPushHadPairs tracker.
        bool HasAnyPairs = false)
    {
        public int Total =>
            (PreQuiesce is null ? 0 : 1)
            + (PrePassClearRtpUpper is null ? 0 : 1)
            + Remap.Length
            + (RtpRemapReflush is null ? 0 : RtpRemapReflush.Length)
            + (ClearRtpUpper is null ? 0 : 1)
            + RtpAuthority.Length
            + (EarlyCommonSwitch is null ? 0 : 1)
            + (ClearRtp is null ? 0 : 1)
            + (ClearRtpAll is null ? 0 : 1)
            + (LwPairs is null ? 0 : 1)
            + (RdtPairs is null ? 0 : 1)
            + AckedBatch.Length
            + FireForget.Length;
    }

    // Builds the full packet sequence required to push every byte of a profile
    // to the firmware. This is the single source of truth for "what does the
    // keyboard need to receive in order to fully reflect this profile?" —
    // ProfileManager.PushCurrentProfile and the keyboard view's Sync button
    // both call into this so a profile switch and a manual sync write the
    // EXACT same byte stream.
    //
    // Sequence (verified against the official driver's rtpSaveToKeyboard +
    // remapSaveToKeyboard flows; see DoSyncAsync's comment block for the
    // historical reverse-engineering notes):
    //   1. Full 42-packet remap stream (3 layer groups × 14 packets)
    //   2. clear-RTP-upper sentinel (only if remaps or LW pairs are present)
    //   3. RTPAuthority + Download pair per remapped key AND per LW pair member
    //   4. clear-RTP-pair (mode byte encodes LW+RDT)
    //   5. CreateLwPairs (bidirectional expansion of user pair list)
    //   6. ACK'd batch — per-key AP/DS/US (9 packets) + common-switch (1 packet)
    //   7. Fire-forget outliers — LastWinReplace + AutoMatchMode
    //
    // Keystroke Tracking is INTENTIONALLY excluded — its enable/disable
    // lifecycle is owned by the long-lived listener stream, not the
    // transient push stream. See KeyboardPerformanceView.StartKeystrokeTracking.
    public static FullProfilePackets BuildFullProfilePackets(
        this ProfileItem item,
        IReadOnlyList<LayoutKey> layoutFlat,
        FirmwareCapabilities? capabilities = null)
    {
        var profile = item.Profile;
        var settings = profile.Settings;

        // (1) Per-key AP/DS/US — 9 packets, ACK'd. Profiles imported from the
        // official web driver carry a full 126-entry Keys_Array; user-edited
        // profiles also normalize to 126 entries (see KeyboardPerformanceView.
        // WriteBackToActiveProfile). A short array would index out of bounds
        // in BuildPacketKeyPoint, so widen defensively here.
        // Always materialise a fresh 126-slot array AND clone each KeySetting
        // record. The wire-level US normalisation below mutates per-slot
        // Upstroke for RDT release slots; without this defensive copy those
        // mutations leak into the caller's item.Profile, corrupting the
        // saved profile JSON when the user toggles RDT off.
        {
            var fresh = new KeySetting[126];
            for (int i = 0; i < 126; i++)
                fresh[i] = i < profile.Keys_Array.Length
                    ? profile.Keys_Array[i] with { }   // shallow-clone the record
                    : new KeySetting { Action_Point = 2.0m };
            profile = profile with { Keys_Array = fresh };
        }

        // RDT release-slot US normalization (wire-level, defensive). The
        // UI-layer snapshot/revert in KeyboardPerformanceView is supposed
        // to keep the per-key US correct, but two failure modes were
        // observed 2026-05-22 on A75 Pro 0x0017 that the snapshot didn't
        // catch:
        //   • Pair created with RDT toggled OFF then later toggled ON →
        //     release slot's US never got bumped to 1.5 mm, firmware
        //     silently rejected the pair (no valid release slot) and
        //     fell back to a stale rtpNumber→HID mapping from a
        //     previous profile's sync (the "e fires v" cross-profile
        //     bleed).
        //   • RDT toggled OFF after a load-from-profile that had US=1.5
        //     baked in → snapshot captured 1.5 as "pre-pair", restore
        //     was a no-op, firmware kept treating the slot as a release
        //     slot (the "e still fires d even after RDT off" bug).
        // Wire-level fix: when hasRdtPairs, force release-slot US to
        // ≥ 1.5 mm. When RDT is disabled but pair definitions remain
        // in the profile (the UI preserves them for re-toggle), force
        // release-slot US to 0 so the firmware stops emitting release
        // HIDs from those slots.
        var earlyRdtPairsRaw = item.RemapProfile?.RdtPairs ?? [];
        if (settings.ReleaseDualTriggerEnabled && earlyRdtPairsRaw.Length > 0)
        {
            foreach (var pair in earlyRdtPairsRaw)
            {
                if (pair is not { Length: 2 }) continue;
                if (pair[0] == pair[1]) continue;
                byte releaseSlot = pair[1];
                if (releaseSlot >= 126) continue;
                if (profile.Keys_Array[releaseSlot].Upstroke < 1.5m)
                    profile.Keys_Array[releaseSlot].Upstroke = 1.5m;
            }
        }
        else if (!settings.ReleaseDualTriggerEnabled && earlyRdtPairsRaw.Length > 0)
        {
            foreach (var pair in earlyRdtPairsRaw)
            {
                if (pair is not { Length: 2 }) continue;
                byte releaseSlot = pair[1];
                if (releaseSlot >= 126) continue;
                profile.Keys_Array[releaseSlot].Upstroke = 0m;
            }
        }

        // Per-key AP/DS/US wire-format dispatch. Picks 0xB6 vs 0xFD packets,
        // 1-byte vs 2-byte values, mm × 10 vs × 100 vs × 200 based on the
        // firmware's reported wire dialect. Defaults to OldHighPrec when
        // capabilities is null (caller didn't pass any) — the same path
        // the codebase used pre-v2.4. See WirePrecision in
        // FirmwareCapabilities.cs and docs/protocol-findings-keybord-net-cn.md.
        var precision = capabilities?.Precision ?? WirePrecision.OldHighPrec;
        var keypointPackets =
            profile.BuildKeyPointBatch(precision, KeyPointType.ActuationPoint)
            .Concat(profile.BuildKeyPointBatch(precision, KeyPointType.Downstroke))
            .Concat(profile.BuildKeyPointBatch(precision, KeyPointType.Upstroke))
            .ToArray();
        var csPacket = BuildCommonSwitchPacket(settings);
        byte[][] ackedBatch = [..keypointPackets, csPacket];

        // (4) clear-RTP-pair — mode byte mirrors common-switch byte[10].
        byte rtpMode = (settings.LastWinEnabled, settings.ReleaseDualTriggerEnabled) switch
        {
            (true, true)   => (byte)3,
            (true, false)  => (byte)1,
            (false, true)  => (byte)2,
            (false, false) => (byte)0,
        };
        // Only emit the clear-RTP-pair packet when LW or RDT is actually
        // enabled — that's the web-driver behaviour observed in
        // tools/captures/0x17/initial-connect.log. Sending mode=0 every
        // sync (the pre-2.2.0 behaviour) is at minimum wasteful and on some
        // firmwares may clobber per-key state. The cached firmware pair
        // table is harmless when LW/RDT bits in CommonSwitch are off, so
        // omitting the clear has no observable side-effect.
        // fc 0a is the LW/RDT pair-table clear — emit it to handle
        // profile transitions (pair-bearing → no-pair leaves the firmware
        // still firing the old pair without it).
        byte[]? clearRtp = BuildClearRtpPacket(rtpMode);

        // ClearRtpAll — `fc 0a 00` sent immediately before the mode-set
        // ClearRtp. Defensive wipe of stale rtpNumber→HID-code mappings
        // from previous profile pushes. Mode-set ClearRtp alone doesn't
        // clear them; without this, switching profiles bleeds the previous
        // profile's release HID into the new profile's pair. Skip when no
        // pairs are involved (nothing to clear).
        byte[]? clearRtpAll = rtpMode != 0 ? BuildClearRtpPacket(0) : null;

        // Pull the typed remap entries + user LW/RDT pair lists off the profile.
        // All default to "no overrides" for profiles authored before these
        // fields existed (legacy web-driver exports).
        var perSlotRemap = item.RemapProfile?.PerSlotHidUsage ?? [];
        var lwPairsRaw = item.RemapProfile?.LwPairs ?? [];
        var rdtPairsRaw = item.RemapProfile?.RdtPairs ?? [];

        var defaultMap = KeyboardLayout.BuildDefaultHidUsageMap(layoutFlat);
        var entries = new RemapEntry[126];
        var hasRemaps = false;
        for (int i = 0; i < 126; i++)
        {
            byte user = i < perSlotRemap.Length ? perSlotRemap[i] : (byte)0;
            if (user != 0) hasRemaps = true;
            byte code = user != 0 ? user : defaultMap[i];
            entries[i] = code != 0 ? RemapEntry.Hid(code) : RemapEntry.Empty;
        }

        // (5) Build the LW pair list. The wire format wants both directions
        // for bidirectional LW (A→B and B→A both swap on demand), so each
        // user pair fans out to two firmware pairs. LW master toggle must
        // also be ON for the firmware to actually use them — but we still
        // ship the (empty) pair packet when LW is off so the firmware's
        // cached pair table gets cleared.
        var userPairs = new List<(byte Main, byte Trigger)>();
        if (settings.LastWinEnabled)
        {
            foreach (var pair in lwPairsRaw)
            {
                if (pair is not { Length: 2 }) continue;
                if (pair[0] == pair[1]) continue;
                userPairs.Add((pair[0], pair[1]));
                userPairs.Add((pair[1], pair[0]));
            }
        }
        var hasLwPairs = settings.LastWinEnabled && userPairs.Count > 0;
        // LW pair table only emitted when pairs exist — matches web driver
        // behaviour (no `fc 01` on initial connect when LW is off). The
        // firmware retains the previous pair table when we don't write it,
        // but the LW master flag in CommonSwitch keeps it dormant.
        byte[]? pairsPacket = hasLwPairs ? BuildCreateLwPairsPacket(userPairs) : null;

        // (5b) Collect RDT pair list. RDT pairs are ORDERED — first slot
        // emits its HID code on press, second slot emits on release. The
        // pair info reaches the firmware THREE ways simultaneously:
        //   1. fc 03 (BuildCreateRdtPairsPacket) — register press/release
        //      slots + per-pair active/reset thresholds. NEW as of v2.4
        //      after re-examining the keybord.net.cn bundle; see
        //      docs/protocol-findings-keybord-net-cn.md.
        //   2. Type-4 entry on press slot + RtpAuthority/Download for the
        //      release HID code (the remap-stream path that was the only
        //      mechanism pre-v2.4).
        //   3. Per-key US on the release slot (firmware uses this as the
        //      "release-detected" threshold reference).
        // The fc 03 packet is what was missing pre-v2.4 — its absence left
        // the firmware's pair table half-configured and caused phantom
        // release HIDs to fire on profile-switch.
        var userRdtPairs = new List<(byte Press, byte Release)>();
        if (settings.ReleaseDualTriggerEnabled)
        {
            foreach (var pair in rdtPairsRaw)
            {
                if (pair is not { Length: 2 }) continue;
                if (pair[0] == pair[1]) continue;
                userRdtPairs.Add((pair[0], pair[1]));
            }
        }
        var hasRdtPairs = settings.ReleaseDualTriggerEnabled && userRdtPairs.Count > 0;
        byte[]? rdtPairsPacket = hasRdtPairs ? BuildCreateRdtPairsPacket(userRdtPairs) : null;

        // (3) Overlay LW + RDT pair entries on the remap-entry array. Capture
        // the underlying HID code BEFORE overwriting so the RtpAuthorityDownload
        // payload carries the original keycode (the firmware needs to know
        // what each pair member outputs). Mirrors the JS bundle's `O(X.value)`
        // first-step-params construction.
        //
        // LW pairs: both slots become Type-4. Both posInGroup=0/1 entries
        //   share groupNumber, both reference rtpNumber=0 (the firmware looks
        //   up by group#/posInGroup, not rtpNumber, in the LW path).
        // RDT pairs: ONLY the press slot becomes Type-4, with rtpNumber that
        //   matches a paired RtpAuthority/Download entry carrying the
        //   release HID code. Release slot stays HID-key in the remap.
        //
        // groupNumber namespace is shared across LW + RDT in the overlay so
        // entries don't collide. UI mutual exclusion means in practice only
        // one of the two blocks runs per sync.
        // rtpNumber namespace is shared between the user-remap RtpAuthority
        // block (emitted first, counting up from 1) and the pair-member
        // RtpAuthority block (emitted second, after the user remaps). Bake
        // the offset into the Type-4 entry's rtpNumber field NOW so it
        // points at the correct RtpAuthority slot — previously the field
        // was written with the un-offset counter and the actual packets
        // were re-numbered later via a baseOffset shift, which left the
        // Type-4 entries pointing at the wrong RtpAuthority entry whenever
        // user remaps coexisted with LW/RDT pairs.
        int userRemapCount = 0;
        for (int i = 0; i < perSlotRemap.Length && i < 126; i++)
            if (perSlotRemap[i] != 0) userRemapCount++;
        var rtpAuthoritySlots = new List<(byte rtpNumber, byte keyCode)>();
        byte sharedRtpCounter = (byte)(userRemapCount + 1);
        // groupNumber starts at 0 — matches the official driver's wire
        // capture (sends [f8 01 00 00 01] for the first pair).
        byte sharedPairIndex = 0;
        // B5 = global RT mode flag (per the JS bundle's `Number(rapidTriggerMode)`).
        byte rtModeFlag = (byte)(settings.RapidTriggerEnabled ? 1 : 0);

        // WARM-UP SENTINEL. After `fc 0a 00` (clear-all-pairs), the
        // firmware's pair table is in "fresh init" state and silently
        // consumes the next Type-4 entry as warm-up. Without this,
        // the first pair of any multi-pair config doesn't fire; the
        // rest work. Cost is one Type-4 entry at an empty slot per
        // pair-bearing sync (no physical key, nothing fires).
        const byte SENTINEL_SLOT = 37;            // empty on A75 Pro / G65 / G75 layouts
        if (hasLwPairs || hasRdtPairs)
        {
            entries[SENTINEL_SLOT] = RemapEntry.Pair(
                rtpNumber: sharedRtpCounter,
                groupNumber: sharedPairIndex,
                posInGroup: 0,
                rtModeFlag: rtModeFlag);
            // Sentinel's release HID is 0x00 (no key). Even if the
            // firmware somehow fires the sentinel pair, nothing happens.
            rtpAuthoritySlots.Add((sharedRtpCounter++, 0x00));
            sharedPairIndex++;
        }
        if (hasLwPairs)
        {
            foreach (var pair in lwPairsRaw)
            {
                if (pair is not { Length: 2 }) continue;
                if (pair[0] == pair[1]) continue;
                byte mainSlot = pair[0];
                byte triggerSlot = pair[1];
                if (mainSlot >= 126 || triggerSlot >= 126) continue;
                byte mainCode = entries[mainSlot].KeyCmd == 0xFC ? entries[mainSlot].B3 : (byte)0;
                byte triggerCode = entries[triggerSlot].KeyCmd == 0xFC ? entries[triggerSlot].B3 : (byte)0;
                entries[mainSlot]    = RemapEntry.Pair(rtpNumber: 0, groupNumber: sharedPairIndex, posInGroup: 0, rtModeFlag: 0);
                entries[triggerSlot] = RemapEntry.Pair(rtpNumber: 0, groupNumber: sharedPairIndex, posInGroup: 1, rtModeFlag: 0);
                rtpAuthoritySlots.Add((sharedRtpCounter++, mainCode));
                rtpAuthoritySlots.Add((sharedRtpCounter++, triggerCode));
                sharedPairIndex++;
            }
        }
        // RDT activation — mirrors the JS bundle's O(X.value) RDT branch
        // (isRdtEnabled=true): Type-4 entry ONLY on the press slot. The
        // release slot stays HID-key so the firmware can read its normal
        // HID code for the release output.
        //
        // Putting Type-4 on BOTH slots was an experiment for non-adjacent
        // pairs (reverted 2026-05-21) — it made pressing the release key
        // directly also fire the pair, and pressing the press key emit the
        // RELEASE HID twice (e.g. R→T pair caused R press → emits "tt"
        // because the press slot was no longer HID-key).
        //
        // No fc 0a (clear-rtp) — actually we DO send it as of 2026-05-21
        // for profile-transition cleanup; see clearRtp computation above.
        // No fc 03 (create-rdt) — the official driver doesn't send it.
        if (hasRdtPairs)
        {
            foreach (var pair in rdtPairsRaw)
            {
                if (pair is not { Length: 2 }) continue;
                if (pair[0] == pair[1]) continue;
                byte pressSlot = pair[0];
                byte releaseSlot = pair[1];
                if (pressSlot >= 126 || releaseSlot >= 126) continue;
                // Capture release key's HID code BEFORE any modification.
                byte releaseCode = entries[releaseSlot].KeyCmd == 0xFC ? entries[releaseSlot].B3 : (byte)0;
                // rtpNumber on the Type-4 entry MUST match the RtpAuthority
                // counter; the rtpAuth list ordering preserves that.
                byte rtpNum = sharedRtpCounter;
                entries[pressSlot] = RemapEntry.Pair(rtpNumber: rtpNum, groupNumber: sharedPairIndex, posInGroup: 0, rtModeFlag: rtModeFlag);
                // entries[releaseSlot] stays HID-key — unmodified.
                rtpAuthoritySlots.Add((sharedRtpCounter++, releaseCode));
                sharedPairIndex++;
            }
        }
        var hasAnyPairs = hasLwPairs || hasRdtPairs;

        // (1) Final remap stream — emits 42 packets (default + Fn1 + Fn2 groups).
        var remapPackets = BuildFullRemapSequenceTyped(entries);

        // (1b) Redundant group=1 re-flush when LW/RDT pairs are active.
        byte[][]? rtpRemapReflush = hasAnyPairs
            ? BuildRemapPacketsTyped(entries, group: 1)
            : null;

        // (2)/(3) clearRtpUpper + per-key RTPAuthority pairs.
        byte[]? clearRtpUpper = (hasRemaps || hasAnyPairs)
            ? BuildClearRtpUpperPacket()
            : null;
        var rtpAuth = new List<byte[]>();
        if (hasRemaps)
        {
            byte counter = 1;
            for (int slot = 0; slot < 126; slot++)
            {
                byte user = slot < perSlotRemap.Length ? perSlotRemap[slot] : (byte)0;
                if (user == 0) continue;
                rtpAuth.Add(BuildPacketRTPAuthority(counter));
                rtpAuth.Add(BuildPacketRTPAuthorityDownload(counter, user));
                counter++;
            }
        }
        if (hasAnyPairs)
        {
            // rtpAuthoritySlots entries already carry the post-offset
            // rtpNumber (sharedRtpCounter was seeded to userRemapCount+1
            // above), so they slot in directly after the user-remap block
            // without further adjustment. The Type-4 entries in `entries`
            // reference these same rtpNumbers.
            foreach (var (rtpNumber, keyCode) in rtpAuthoritySlots)
            {
                rtpAuth.Add(BuildPacketRTPAuthority(rtpNumber));
                rtpAuth.Add(BuildPacketRTPAuthorityDownload(rtpNumber, keyCode));
            }

            // Defensive padding: write blank (0x00 HID) RtpAuthority entries
            // for higher rtpNumbers up to a fixed slot ceiling, even if this
            // sync's pair list only uses the first few. Without this, the
            // firmware's rtpNumber→releaseHID table retains stale mappings
            // from PREVIOUS profile syncs (observed 2026-05-22 on A75 Pro
            // 0x0017: Typing profile registers rtpNumber=2→v for c→v pair,
            // user switches to Valorant with pair e→d, our sync writes
            // rtpNumber=2→d but firmware keeps v active — pressing e fires
            // v even after profile-switch + RDT-off-then-on cycles).
            //
            // The pattern is "fc 0a 00 clears the mode but NOT the
            // rtpNumber→HID table." Padding explicitly overwrites every
            // rtpNumber up to RTP_SLOT_CEILING with 0x00 (the no-op HID
            // code, same as the sentinel uses). Firmware-acceptable
            // because we already write the sentinel with 0x00; this just
            // extends the same trick to wipe stale higher slots.
            //
            // Ceiling of 8 covers all realistic pair counts (the firmware's
            // own limit is likely lower — most keyboards have 4-6 pair
            // slots — but 8 is small enough that the extra ~16 packets
            // don't materially slow sync).
            const byte RTP_SLOT_CEILING = 8;
            while (sharedRtpCounter <= RTP_SLOT_CEILING)
            {
                rtpAuth.Add(BuildPacketRTPAuthority(sharedRtpCounter));
                rtpAuth.Add(BuildPacketRTPAuthorityDownload(sharedRtpCounter, 0x00));
                sharedRtpCounter++;
            }
        }

        // (7) Fire-forget outlier toggles. Keystroke Tracking is excluded.
        var fireForget = new byte[][]
        {
            BuildLastWinReplacePacket(settings.LastWinReplaceEnabled),
            BuildAutoMatchModePacket(settings.AutoMatchMode),
        };

        // (0) Pre-quiesce. The firmware's LW/RDT deconflict pipeline runs in
        // parallel with the remap-table write — if a key is pressed (or even
        // *interpreted* as pressed by the firmware's mid-write read of a
        // partial entry) while pair-member slots are being rewritten, the
        // firmware can emit ghost keystrokes (commonly seen: "spamming `a`
        // for a second"). Sending a Common-Switch packet with LW=off+RDT=off
        // FIRST puts the firmware in a quiet state for the duration of the
        // rewrite; the final Common-Switch in AckedBatch re-asserts the
        // user's intended state once the new entries are in place. Skipped
        // entirely when LW/RDT aren't enabled — no need to flicker the bits.
        byte[]? preQuiesce = null;
        if (settings.LastWinEnabled || settings.ReleaseDualTriggerEnabled)
        {
            var quiet = new ProfileSettings
            {
                RapidTriggerEnabled = settings.RapidTriggerEnabled,
                TurboEnabled = settings.TurboEnabled,
                LastWinEnabled = false,
                ReleaseDualTriggerEnabled = false,
                RTMatchEnabled = settings.RTMatchEnabled,
                KeystrokeTrackingEnabled = settings.KeystrokeTrackingEnabled,
                LastWinReplaceEnabled = settings.LastWinReplaceEnabled,
                AutoMatchMode = settings.AutoMatchMode,
            };
            preQuiesce = BuildCommonSwitchPacket(quiet);
        }

        // Pre-emit CommonSwitch (with the final RT/LW/RDT bits) BEFORE the
        // pair table so the firmware has the correct mode active when it
        // processes create-rdt/create-lw. Only emit when pairs are involved.
        byte[]? earlyCommonSwitch = hasAnyPairs ? BuildCommonSwitchPacket(settings) : null;

        // Extra `aa 00 01` BEFORE the remap stream flushes the firmware's
        // cached rtpNumber→HID mapping from a previous profile. Needed for
        // profile-switch correctness.
        byte[]? prePassClearRtpUpper = hasAnyPairs ? BuildClearRtpUpperPacket() : null;

        return new FullProfilePackets(
            PreQuiesce: preQuiesce,
            Remap: remapPackets,
            RtpRemapReflush: rtpRemapReflush,
            ClearRtpUpper: clearRtpUpper,
            RtpAuthority: [.. rtpAuth],
            EarlyCommonSwitch: earlyCommonSwitch,
            PrePassClearRtpUpper: prePassClearRtpUpper,
            ClearRtp: clearRtp,
            ClearRtpAll: clearRtpAll,
            LwPairs: pairsPacket,
            RdtPairs: rdtPairsPacket,
            AckedBatch: ackedBatch,
            FireForget: fireForget,
            HasAnyPairs: hasAnyPairs);
    }
}
