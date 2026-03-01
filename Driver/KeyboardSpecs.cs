namespace Driver;

// received_report_handle with a0
// on_event_keyboard_open
public sealed record KeyboardSpecs
{
    public string FirmwareVersion { get; set; } = string.Empty;
    // Connection in use? Not sure if necessary to track.. for now we ignore
    public bool KbdLinkUsed { get; set; } = false;
    public int? TurboValue { get; set; }
    public int? RapidTrigger { get; set; }
    public int? RapidTriggerPlus { get; set; }
    public int? LastWinValue { get; set; }
    public int? KeyboardType { get; set; }

    public KeyboardSpecs(byte[] packet)
    {
        if (packet.Length < 1) return;
        if (packet[0] != 0xa0) return;
        if (packet[1] == 0x02 && packet[2] == 0x00)
        {
            KbdLinkUsed = false;
            FirmwareVersion = string.Format("0.{0}{1}", packet[8], packet[7]);
            TurboValue = packet[15];
            RapidTrigger = packet[16];
            RapidTriggerPlus = packet[18];
            LastWinValue = packet[19];
            //this.kbd_link_used = false;
            //this.firmware_version_string =
            //  "0." + byte9.toString() + byte8.toString();
            //this.turbovalue = data.getUint8(15);
            //this.rtvalue = data.getUint8(16);
            //this.#on_event_keyboard_open(byte5, byte6, byte7, byte8, byte9);
            KeyboardType = GetKeyboardType(packet);
        }
        else if (packet[1] == 0x02 && packet[2] == 0x04)
        {
            KbdLinkUsed = false;
        }
    }

    private static int? GetKeyboardType(byte[] packet)
    {
        return (packet[4], packet[5], packet[6]) switch
        {
            (11, 1, 1) => 75,
            (11, 4, 1) => 75, // or k82? k82 case is unreachable if though
            (11, 4, 3) => 750,
            (11, 4, 2) => 751, // 751 (UK) or 752 (FR) or 753 (DE)
            (11, 2, 1) => 65,
            (15, 1, 1) => 65,
            (11, 3, 1) => 60,
            (11, 4, 5) => 754,
            (11, 4, 7) => 755,
            _ => null,
        };
    }
}
