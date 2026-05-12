using HidSharp;

namespace Driver;

public sealed record KeyboardFilter
{
    required public int VendorId, ProductId, Usage, UsagePage;
}

public sealed class KeyboardManager : IDisposable
{
    public static readonly KeyboardFilter[] DrunkDeerKeyboards = [
        new KeyboardFilter { VendorId = 0x352d, ProductId = 0x2383, Usage = 0, UsagePage = 0xff00 },
        new KeyboardFilter { VendorId = 0x352d, ProductId = 0x2386, Usage = 0, UsagePage = 0xff00 },
        new KeyboardFilter { VendorId = 0x352d, ProductId = 0x2382, Usage = 0, UsagePage = 0xff00 },
        new KeyboardFilter { VendorId = 0x352d, ProductId = 0x2384, Usage = 0, UsagePage = 0xff00 },
        new KeyboardFilter { VendorId = 0x05ac, ProductId = 0x024f, Usage = 0, UsagePage = 0xff00 },
        new KeyboardFilter { VendorId = 0x352d, ProductId = 0x2391, Usage = 0, UsagePage = 0xff00 },
        new KeyboardFilter { VendorId = 0x352d, ProductId = 0x2a08, Usage = 0, UsagePage = 0xff00 }, // A75 Pro second interface
        new KeyboardFilter { VendorId = 0x352d, ProductId = 0x2387, Usage = 0, UsagePage = 0xff00 } // A75 Ultra
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
        if (KeyboardWithSpecs is { } keyboard && keyboard.Keyboard.CanOpen) return;

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
