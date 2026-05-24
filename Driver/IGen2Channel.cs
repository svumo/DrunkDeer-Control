namespace Driver;

// Common interface for gen-2 keyboard channels so HidDeviceExtensions can
// route through any implementation. Two impls today:
//   - Gen2KeyboardChannel: uses HidD_SetOutputReport + multi-path reads
//     (async overlapped ReadFile + GetInputReport polling + Raw Input).
//     Works for some gen-2 OEM variants whose interrupt-IN reports the HID
//     class driver actually dispatches.
//   - Gen2WebHidChannel: routes through an embedded WebView2 + WebHID.
//     For OEM variants where HidClass.sys silently drops responses (VID
//     0x19F5 etc). See docs/gen2-oem-investigation.md.
//
// Channels register against a device path in HidDeviceExtensions; the
// WritePacket / WritePacketNoAck extensions check the registry and route
// through whichever channel is registered for the stream's underlying
// device, falling through to the plain HidStream path for gen-1 devices.
public interface IGen2Channel : IDisposable
{
    byte[] WriteAndPoll(byte[] packet, int pollMs = 1500, byte? expectFirstByte = null);
    bool WriteNoAck(byte[] packet);
    string WriteDevicePath { get; }
}
