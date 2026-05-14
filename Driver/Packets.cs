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

    public static byte GetActuationPoint(this Profile profile, int index)
        => ((byte)(profile.Keys_Array[index].Action_Point * 10)).Clamp(2, 38);

    public static byte GetDownstrokePoint(this Profile profile, int index)
        => ((byte)(profile.Keys_Array[index].Downstroke * 10)).Clamp(0, 36);

    public static byte GetUpstrokePoint(this Profile profile, int index)
        => ((byte)(profile.Keys_Array[index].Upstroke * 10)).Clamp(0, 36);

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
    //   KeyType=0 (HID key)  : [slot, 0xFC, 0,         HID code,  0,          0]
    //   KeyType=4 (LW/RDT)   : [slot, 0xF8, rtpNumber, group#,    posInGrp,   rdtEnabled]
    // KeyCmd=0 means "no entry" — firmware writes nothing to this slot.
    // Verified against `sendRemapKeyData` case 0 / case 4 in dd-index.js.
    //
    // rdtEnabled (B5): 0 for Last Win pairs, 1 for Release Dual-Trigger pairs.
    // Verified from the JS bundle's O(X.value) firstStepParams construction:
    //   LW branch  → xA(entry, {rdtEnabled:!1}) → B5 = 0
    //   RDT branch → xA(entry, {rdtEnabled:!0}) → B5 = 1
    public readonly record struct RemapEntry(byte KeyCmd, byte B2, byte B3, byte B4, byte B5)
    {
        public static readonly RemapEntry Empty = new(0, 0, 0, 0, 0);
        public static RemapEntry Hid(byte hidCode) => new(0xFC, 0, hidCode, 0, 0);
        public static RemapEntry Pair(byte rtpNumber, byte groupNumber, byte posInGroup, byte rdtEnabled)
            => new(0xF8, rtpNumber, groupNumber, posInGroup, rdtEnabled);
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
            KeyPointType.ActuationPoint => profile.GetActuationPoint,
            KeyPointType.Downstroke => profile.GetDownstrokePoint,
            KeyPointType.Upstroke => profile.GetUpstrokePoint,
            _ => throw new NotImplementedException(),
        };

        for (int x = 0; x < max_x; x++)
        {
            var value = getValue(x + offset);
            packet[4 + x] = value;
        }

        return packet;
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
        byte[] ClearRtp,
        byte[] LwPairs,
        byte[][] AckedBatch,
        byte[][] FireForget)
    {
        public int Total =>
            (PreQuiesce is null ? 0 : 1)
            + Remap.Length
            + (RtpRemapReflush is null ? 0 : RtpRemapReflush.Length)
            + (ClearRtpUpper is null ? 0 : 1)
            + RtpAuthority.Length
            + 2 /* ClearRtp + LwPairs */
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
        IReadOnlyList<LayoutKey> layoutFlat)
    {
        var profile = item.Profile;
        var settings = profile.Settings;

        // (1) Per-key AP/DS/US — 9 packets, ACK'd. Profiles imported from the
        // official web driver carry a full 126-entry Keys_Array; user-edited
        // profiles also normalize to 126 entries (see KeyboardPerformanceView.
        // WriteBackToActiveProfile). A short array would index out of bounds
        // in BuildPacketKeyPoint, so widen defensively here.
        if (profile.Keys_Array.Length < 126)
        {
            var widened = new KeySetting[126];
            for (int i = 0; i < widened.Length; i++)
                widened[i] = i < profile.Keys_Array.Length
                    ? profile.Keys_Array[i]
                    : new KeySetting { Action_Point = 2.0m };
            profile = profile with { Keys_Array = widened };
        }

        var ackedBatch = new byte[][]
        {
            profile.BuildPacketKeyPoint(0, KeyPointType.ActuationPoint),
            profile.BuildPacketKeyPoint(1, KeyPointType.ActuationPoint),
            profile.BuildPacketKeyPoint(2, KeyPointType.ActuationPoint),
            profile.BuildPacketKeyPoint(0, KeyPointType.Downstroke),
            profile.BuildPacketKeyPoint(1, KeyPointType.Downstroke),
            profile.BuildPacketKeyPoint(2, KeyPointType.Downstroke),
            profile.BuildPacketKeyPoint(0, KeyPointType.Upstroke),
            profile.BuildPacketKeyPoint(1, KeyPointType.Upstroke),
            profile.BuildPacketKeyPoint(2, KeyPointType.Upstroke),
            BuildCommonSwitchPacket(settings),
        };

        // (4) clear-RTP-pair — mode byte mirrors common-switch byte[10].
        byte rtpMode = (settings.LastWinEnabled, settings.ReleaseDualTriggerEnabled) switch
        {
            (true, true)   => (byte)3,
            (true, false)  => (byte)1,
            (false, true)  => (byte)2,
            (false, false) => (byte)0,
        };
        var clearRtp = BuildClearRtpPacket(rtpMode);

        // Pull the typed remap entries + user LW pair list off the profile.
        // Both default to "no overrides" for profiles authored before these
        // fields existed (legacy web-driver exports).
        var perSlotRemap = item.RemapProfile?.PerSlotHidUsage ?? [];
        var lwPairsRaw = item.RemapProfile?.LwPairs ?? [];

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
        var pairsPacket = BuildCreateLwPairsPacket(userPairs);

        var hasLwPairs = settings.LastWinEnabled && userPairs.Count > 0;

        // (3) Overlay LW pair entries on the remap-entry array. Capture the
        // underlying HID code BEFORE overwriting so the RTPAuthorityDownload
        // payload carries the original keycode (firmware needs to know what
        // each pair member outputs). Mirrors the JS bundle's `O(X.value)`
        // first-step-params construction.
        //
        // rdtEnabled (B5) is 0 for LW pairs and 1 for RDT pairs — verified
        // from the JS O(X.value) else-branch: xA(entry, {rdtEnabled:!1}).
        // An earlier version passed rtModeByte (=1 when RT is on), which made
        // the firmware treat every LW pair entry as an RDT pair and silently
        // drop the deconflict behaviour.
        var rtpAuthoritySlots = new List<(byte rtpNumber, byte keyCode)>();
        if (hasLwPairs)
        {
            byte rtpCounter = 1;
            byte pairIndex = 0;
            foreach (var pair in lwPairsRaw)
            {
                if (pair is not { Length: 2 }) continue;
                if (pair[0] == pair[1]) continue;
                byte mainSlot = pair[0];
                byte triggerSlot = pair[1];
                if (mainSlot >= 126 || triggerSlot >= 126) continue;
                byte mainCode = entries[mainSlot].KeyCmd == 0xFC ? entries[mainSlot].B3 : (byte)0;
                byte triggerCode = entries[triggerSlot].KeyCmd == 0xFC ? entries[triggerSlot].B3 : (byte)0;
                entries[mainSlot]    = RemapEntry.Pair(rtpNumber: 0, groupNumber: pairIndex, posInGroup: 0, rdtEnabled: 0);
                entries[triggerSlot] = RemapEntry.Pair(rtpNumber: 0, groupNumber: pairIndex, posInGroup: 1, rdtEnabled: 0);
                rtpAuthoritySlots.Add((rtpCounter++, mainCode));
                rtpAuthoritySlots.Add((rtpCounter++, triggerCode));
                pairIndex++;
            }
        }

        // (1) Final remap stream — 42 packets covering default + Fn1 + Fn2.
        var remapPackets = BuildFullRemapSequenceTyped(entries);

        // (1b) Redundant group=1 re-flush for LW/RDT — mirrors the JS
        // rtpSaveToKeyboard which re-sends group=1 (with the modified
        // KeyType=4 entries) RIGHT BEFORE ClearRtpUpper. The initial 42-
        // packet stream already includes this group, but the official
        // driver always re-sends it as the immediate predecessor of
        // ClearRtpUpper; omitting it has been correlated with pair
        // activation failing on firmware 0.09 (see docs §11.5).
        byte[][]? rtpRemapReflush = hasLwPairs
            ? BuildRemapPacketsTyped(entries, group: 1)
            : null;

        // (2)/(3) clearRtpUpper + per-key RTPAuthority pairs.
        byte[]? clearRtpUpper = (hasRemaps || hasLwPairs)
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
        if (hasLwPairs)
        {
            // rtpNumber namespace must not collide with the user-remap range.
            // Re-base after the user-remap entries (2 packets per remap → /2).
            byte baseOffset = (byte)(rtpAuth.Count / 2);
            foreach (var (rtpNumber, keyCode) in rtpAuthoritySlots)
            {
                byte adjusted = (byte)(rtpNumber + baseOffset);
                rtpAuth.Add(BuildPacketRTPAuthority(adjusted));
                rtpAuth.Add(BuildPacketRTPAuthorityDownload(adjusted, keyCode));
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

        return new FullProfilePackets(
            PreQuiesce: preQuiesce,
            Remap: remapPackets,
            RtpRemapReflush: rtpRemapReflush,
            ClearRtpUpper: clearRtpUpper,
            RtpAuthority: [.. rtpAuth],
            ClearRtp: clearRtp,
            LwPairs: pairsPacket,
            AckedBatch: ackedBatch,
            FireForget: fireForget);
    }
}
