using HidSharp;

namespace Driver;

public sealed record KeyboardFilter
{
    required public int VendorId, ProductId, Usage, UsagePage;
}

public sealed class KeyboardManager : IDisposable
{
    // Reference list of every PID the official DrunkDeer web driver's
    // `navigator.hid.requestDevice` filters on, per docs/keyboard-protocol.md §2.1.
    // This is NO LONGER an allowlist for selection — `IsDrunkDeerKeyboard`
    // probes any device with VID 0x352D and 64-byte HID reports, regardless of
    // PID, because newer firmware revisions (gen-2 A75 Pro, observed 2026-05-23)
    // sometimes introduce PIDs that aren't in the official driver's list and
    // would otherwise be silently dropped. The array is kept for two reasons:
    //   1. Diagnostics: `FindKeyboard` flags "[UNKNOWN PID]" in the log when
    //      probing a device whose PID isn't in this list, making it easy to
    //      spot new firmware revisions in user-submitted logs.
    //   2. The 0x05AC (Apple) branch of `IsDrunkDeerKeyboard` still uses
    //      exact PID matching, since Apple's VID covers thousands of unrelated
    //      devices and probing all of them with IDENTITY_PACKET would be rude.
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
        new KeyboardFilter { VendorId = 0x352d, ProductId = 0x1a81, Usage = 0, UsagePage = 0xff00 }, // (from gen-2 web driver filter list)
        new KeyboardFilter { VendorId = 0x352d, ProductId = 0x2094, Usage = 0, UsagePage = 0xff00 }, // (from gen-2 web driver filter list)
        new KeyboardFilter { VendorId = 0x19f5, ProductId = 0xfb5c, Usage = 0, UsagePage = 0xff00 }, // A75 Pro on gen-2 firmware (drunkdeer.keybord.net.cn lineage)
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

    // Hash of the last "no-match" enumeration we dumped. Used to throttle the
    // full HID inventory dump: HidSharp fires OnDeviceListChanged in bursts
    // (10+ events per single plug/unplug action), and without throttling we'd
    // dump 20-30 device records per event and burn through the 2 MB log cap.
    // Reset to 0 whenever a match is found, so the next no-match dumps fresh.
    private static int _lastDumpEnumHash;

    private static KeyboardWithSpecs? FindKeyboard()
    {
        DebugLogger.Log("FindKeyboard() called");

        var allDevices = DeviceList.Local.GetHidDevices().ToList();
        var matched = allDevices.Where(IsDrunkDeerKeyboard).ToList();
        DebugLogger.Log($"  Enumerated {allDevices.Count} HID devices total, {matched.Count} pass DrunkDeer filter");

        foreach (var d in matched)
        {
            int inLen = -1, outLen = -1;
            string mfg = "?", prod = "?";
            try { inLen = d.GetMaxInputReportLength(); } catch { }
            try { outLen = d.GetMaxOutputReportLength(); } catch { }
            try { mfg = d.GetManufacturer() ?? ""; } catch { }
            try { prod = d.GetProductName() ?? ""; } catch { }
            DebugLogger.Log($"  - VID=0x{d.VendorID:x4} PID=0x{d.ProductID:x4} InLen={inLen} OutLen={outLen} Mfg='{mfg}' Product='{prod}'");
        }

        foreach (var device in matched)
        {
            var isKnownPid = DrunkDeerKeyboards.Any(k => k.VendorId == device.VendorID && k.ProductId == device.ProductID);
            var pidTag = isKnownPid ? "" : " [UNKNOWN PID — new firmware revision?]";
            try
            {
                using var stream = device.Open();
                var raw = stream.WritePacket(Packets.IDENTITY_PACKET);
                DebugLogger.Log($"  spec packet from PID=0x{device.ProductID:x4}{pidTag}: {raw.PacketToString()}");
                var specs = new KeyboardSpecs(raw);
                DebugLogger.Log($"    -> KeyboardType={specs.KeyboardType?.ToString() ?? "null"} Firmware={specs.FirmwareVersion} Compatible={specs.IsCompatible()} RTMatch={specs.RTMatch?.ToString() ?? "null"} AutoMatchMode={specs.AutoMatchMode?.ToString() ?? "null"} LWReplace={specs.LastWinReplace?.ToString() ?? "null"}");
                if (specs.IsCompatible())
                {
                    var caps = specs.GetCapabilities();
                    DebugLogger.Log($"    -> Capabilities: Tier={caps.Tier} Precision={caps.Precision} IsTooOld={caps.IsTooOld} Label=\"{caps.Label}\"");
                    DebugLogger.Log($"  Selected PID=0x{device.ProductID:x4}");
                    _lastDumpEnumHash = 0;
                    return (device, specs);
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"  PID=0x{device.ProductID:x4}{pidTag} ERROR: {ex.Message}");
            }
        }

        // Nothing matched (or matches failed compat). Dump the full HID
        // inventory so the user/maintainer can identify the keyboard manually.
        // Most useful when gen-2 firmware shifts the device to a VID we don't
        // recognize and the manufacturer/product strings don't contain
        // "drunkdeer" either.
        if (matched.Count == 0)
        {
            int enumHash = 17;
            foreach (var d in allDevices)
                enumHash = HashCode.Combine(enumHash, d.VendorID, d.ProductID);

            if (enumHash != _lastDumpEnumHash)
            {
                _lastDumpEnumHash = enumHash;
                DebugLogger.Log($"  No DrunkDeer-like devices matched. Full HID inventory ({allDevices.Count} devices):");
                foreach (var d in allDevices)
                {
                    int inLen = -1, outLen = -1;
                    string mfg = "?", prod = "?";
                    try { inLen = d.GetMaxInputReportLength(); } catch { }
                    try { outLen = d.GetMaxOutputReportLength(); } catch { }
                    try { mfg = d.GetManufacturer() ?? ""; } catch { }
                    try { prod = d.GetProductName() ?? ""; } catch { }
                    DebugLogger.Log($"    VID=0x{d.VendorID:x4} PID=0x{d.ProductID:x4} InLen={inLen} OutLen={outLen} Mfg='{mfg}' Product='{prod}'");
                }
            }
            else
            {
                DebugLogger.Log("  No DrunkDeer-like devices matched (same enumeration as previous scan, dump suppressed).");
            }
        }

        DebugLogger.Log("  No compatible keyboard found");
        return null;
    }

    public static bool IsDrunkDeerKeyboard(HidDevice device)
    {
        // HidSharp doesn't expose Usage/UsagePage easily, so we rely on the
        // 64-byte input + output report check to identify the vendor-defined
        // HID interface (UsagePage 0xFF00) on multi-interface keyboards.
        //
        // Three acceptance paths, in order of confidence:
        //   1. VID 0x352D (DrunkDeer's own vendor ID) — any PID accepted.
        //      TypeCode validation in KeyboardSpecs is the real compat gate.
        //   2. USB manufacturer/product string contains "drunkdeer" — catches
        //      gen-2 firmware that's been rebranded to a non-0x352D VID
        //      (observed 2026-05-24 in field: A75 Pro running gen-2 firmware
        //      enumerates with VID outside our reference list). String
        //      descriptors are typically preserved across firmware revisions
        //      even when VID/PID change, making them a more durable identity.
        //   3. Apple's 0x05AC PIDs in DrunkDeerKeyboards — only 0x024F is a
        //      known DrunkDeer relay quirk, kept as a strict PID match since
        //      Apple's VID covers thousands of unrelated devices.
        try
        {
            if (device.GetMaxOutputReportLength() < 64 || device.GetMaxInputReportLength() < 64)
                return false;
        }
        catch { return false; }

        if (device.VendorID == 0x352d) return true;

        try
        {
            var mfg = device.GetManufacturer() ?? "";
            var product = device.GetProductName() ?? "";
            if (mfg.Contains("drunkdeer", StringComparison.OrdinalIgnoreCase) ||
                product.Contains("drunkdeer", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        catch { /* descriptor strings unavailable — fall through to PID-list check */ }

        return DrunkDeerKeyboards.Any(k => k.VendorId == device.VendorID && k.ProductId == device.ProductID);
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
