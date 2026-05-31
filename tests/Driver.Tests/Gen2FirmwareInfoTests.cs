using Driver;
using Xunit;

namespace DrunkDeer.Tests;

// PacketsGen2.ParseFirmwareInfo decodes the 0x55 0x03 firmware-info readback —
// the real gen-2 OEM firmware identifier (the board doesn't answer the gen-1
// identity the app fakes "OEM 1.7" from). Bytes are from tester-B's usb2/usb3
// captures (identical): 07 01 "Dec 25 2025,19:53:41".
public class Gen2FirmwareInfoTests
{
    [Fact]
    public void Parses_version_bytes_and_build_date_from_the_captured_bytes()
    {
        // 07 01 + ASCII "Dec 25 2025,19:53:41"
        byte[] data =
        [
            0x07, 0x01,
            0x44, 0x65, 0x63, 0x20, 0x32, 0x35, 0x20, 0x32, 0x30, 0x32, 0x35,
            0x2C, 0x31, 0x39, 0x3A, 0x35, 0x33, 0x3A, 0x34, 0x31,
        ];

        var (v0, v1, build) = PacketsGen2.ParseFirmwareInfo(data);

        Assert.Equal(0x07, v0);
        Assert.Equal(0x01, v1);
        Assert.Equal("Dec 25 2025,19:53:41", build);
    }

    [Fact]
    public void Handles_missing_build_string_and_short_input()
    {
        Assert.Equal((0, 0, ""), PacketsGen2.ParseFirmwareInfo([]));
        Assert.Equal(((byte)0x07, (byte)0x01, ""), PacketsGen2.ParseFirmwareInfo([0x07, 0x01]));
    }
}
