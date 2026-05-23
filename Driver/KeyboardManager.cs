using HidSharp;

namespace Driver;

public sealed record KeyboardFilter
{
    required public int VendorId, ProductId, Usage, UsagePage;
}

public sealed class KeyboardManager : IDisposable
{
    // Every PID the official DrunkDeer web driver's `navigator.hid.requestDevice`
    // filters on, per docs/keyboard-protocol.md §2.1. Listing them all here means
    // even keyboards we haven't tested on hardware (G75, G60 variants, etc.)
    // enumerate and reach the resolver, where TypeCode identification takes over.
    // Unknown models will surface the "unrecognized model" banner in the editor
    // rather than silently disappearing from the device list.
    public static readonly KeyboardFilter[] DrunkDeerKeyboards = [
        new KeyboardFilter { VendorId = 0x352d, ProductId = 0x2382, Usage = 0, UsagePage = 0xff00 },
        new KeyboardFilter { VendorId = 0x352d, ProductId = 0x2383, Usage = 0, UsagePage = 0xff00 }, // G65
        new KeyboardFilter { VendorId = 0x352d, ProductId = 0x2384, Usage = 0, UsagePage = 0xff00 },
        new KeyboardFilter { VendorId = 0x352d, ProductId = 0x2386, Usage = 0, UsagePage = 0xff00 },
        new KeyboardFilter { VendorId = 0x352d, ProductId = 0x2387, Usage = 0, UsagePage = 0xff00 }, // A75 Ultra
        new KeyboardFilter { VendorId = 0x352d, ProductId = 0x238f, Usage = 0, UsagePage = 0xff00 },
        new KeyboardFilter { VendorId = 0x352d, ProductId = 0x2390, Usage = 0, UsagePage = 0xff00 },
        new KeyboardFilter { VendorId = 0x352d, ProductId = 0x2391, Usage = 0, UsagePage = 0xff00 }, // A75 Pro
        new KeyboardFilter { VendorId = 0x352d, ProductId = 0x2394, Usage = 0, UsagePage = 0xff00 },
        new KeyboardFilter { VendorId = 0x352d, ProductId = 0x23b3, Usage = 0, UsagePage = 0xff00 },
        new KeyboardFilter { VendorId = 0x352d, ProductId = 0x23b4, Usage = 0, UsagePage = 0xff00 },
        new KeyboardFilter { VendorId = 0x352d, ProductId = 0x23b5, Usage = 0, UsagePage = 0xff00 },
        new KeyboardFilter { VendorId = 0x352d, ProductId = 0x23b6, Usage = 0, UsagePage = 0xff00 },
        new KeyboardFilter { VendorId = 0x352d, ProductId = 0x2a08, Usage = 0, UsagePage = 0xff00 }, // A75 Pro second interface
        new KeyboardFilter { VendorId = 0x05ac, ProductId = 0x024f, Usage = 0, UsagePage = 0xff00 }  // Apple-relay quirk
    ];

    public KeyboardWithSpecs? _keyboardWithSpecs;
    public KeyboardWithSpecs? KeyboardWithSpecs
    {
        get { return _keyboardWithSpecs; }
        set
        {
            if (!EqualityComparer<string?>.Default.Equals(_keyboardWithSpecs?.Keyboard.ToString(), value?.Keyboard.ToString()))
            {
                _keyboardWithSpecs = value;
                ConnectedKeyboardChanged?.Invoke(_keyboardWithSpecs);
            }
        }
    }
    public event Action<KeyboardWithSpecs?>? ConnectedKeyboardChanged;

    public KeyboardManager() { KeyboardWithSpecs = FindKeyboard(); Register(); }

    private void OnDeviceListChanged(object? sender, DeviceListChangedEventArgs e)
    {
        // Always re-scan on device-list changes. The previous CanOpen
        // early-return was unreliable on Windows: HidSharp's cached
        // HidDevice still reports CanOpen=true for several seconds after
        // physical USB unplug, which made the app miss the disconnect and
        // leave the connection pill stuck on the previous keyboard. The
        // setter compares device IDs so re-scanning when nothing relevant
        // changed is a no-op (no event fired).
        DebugLogger.Log($"OnDeviceListChanged: re-scanning (current={_keyboardWithSpecs?.Keyboard.ToString() ?? "null"})");
        KeyboardWithSpecs = FindKeyboard();
    }

    private static KeyboardWithSpecs? FindKeyboard()
    {
        DebugLogger.Log("FindKeyboard() called");

        var allDevices = DeviceList.Local.GetHidDevices().ToList();
        var knownVids = new HashSet<int> { 0x352d, 0x05ac };
        var candidates = allDevices.Where(d => knownVids.Contains(d.VendorID)).ToList();
        DebugLogger.Log($"  Enumerated {allDevices.Count} HID devices total, {candidates.Count} match known VIDs (0x352D, 0x05AC)");

        foreach (var d in candidates)
        {
            int inLen = -1, outLen = -1;
            try { inLen = d.GetMaxInputReportLength(); } catch { }
            try { outLen = d.GetMaxOutputReportLength(); } catch { }
            var passes = IsDrunkDeerKeyboard(d);
            DebugLogger.Log($"  - VID=0x{d.VendorID:x4} PID=0x{d.ProductID:x4} InLen={inLen} OutLen={outLen} PassesFilter={passes}");
        }

        foreach (var device in allDevices.Where(IsDrunkDeerKeyboard))
        {
            try
            {
                using var stream = device.Open();
                var raw = stream.WritePacket(Packets.IDENTITY_PACKET);
                DebugLogger.Log($"  spec packet from PID=0x{device.ProductID:x4}: {raw.PacketToString()}");
                var specs = new KeyboardSpecs(raw);
                DebugLogger.Log($"    -> KeyboardType={specs.KeyboardType?.ToString() ?? "null"} Firmware={specs.FirmwareVersion} Compatible={specs.IsCompatible()} RTMatch={specs.RTMatch?.ToString() ?? "null"} AutoMatchMode={specs.AutoMatchMode?.ToString() ?? "null"} LWReplace={specs.LastWinReplace?.ToString() ?? "null"}");
                if (specs.IsCompatible())
                {
                    var caps = specs.GetCapabilities();
                    DebugLogger.Log($"    -> Capabilities: Tier={caps.Tier} Precision={caps.Precision} IsTooOld={caps.IsTooOld} Label=\"{caps.Label}\"");
                    DebugLogger.Log($"  Selected PID=0x{device.ProductID:x4}");
                    return (device, specs);
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"  PID=0x{device.ProductID:x4} ERROR: {ex.Message}");
            }
        }

        DebugLogger.Log("  No compatible keyboard found");
        return null;
    }

    public static bool IsDrunkDeerKeyboard(HidDevice device)
    {
        // The HidSharp lib doesn't allow for easy access to the usage and usage page attributes.
        // Instead we check if the input and output report length are both over 64 bytes.
        // This indicates we probably have a device with read and write stream capability.
        return DrunkDeerKeyboards.Any(ddkbs => ddkbs.ProductId == device.ProductID && ddkbs.VendorId == device.VendorID && device.GetMaxOutputReportLength() >= 64 && device.GetMaxInputReportLength() >= 64);
    }

    public void Register() { DeviceList.Local.Changed += OnDeviceListChanged; }

    public void Unregister() { DeviceList.Local.Changed -= OnDeviceListChanged; }

    public void Dispose()
    {
        Unregister();
        KeyboardWithSpecs = null;
    }

    public bool IsConnected()
    {
        if (KeyboardWithSpecs is not { } keyboard) return false;
        using var stream = keyboard.Keyboard.Open();
        return stream.Ping();
    }
}
