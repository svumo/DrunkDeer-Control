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

    // beta.4 diagnostic build — throttling intentionally removed. Every scan
    // dumps everything so we maximize evidence for the gen-2 firmware
    // identity-timeout investigation. Will reinstate when we ship a
    // non-diagnostic build.

    private static KeyboardWithSpecs? FindKeyboard()
    {
        DebugLogger.Log("FindKeyboard() called");

        var allDevices = DeviceList.Local.GetHidDevices().ToList();
        var matched = allDevices.Where(IsDrunkDeerKeyboard).ToList();
        DebugLogger.Log($"  Enumerated {allDevices.Count} HID devices total, {matched.Count} pass DrunkDeer filter");

        foreach (var d in matched)
        {
            LogDeviceMetadata(d, prefix: "  matched ");
        }

        foreach (var device in matched)
        {
            var isKnownPid = DrunkDeerKeyboards.Any(k => k.VendorId == device.VendorID && k.ProductId == device.ProductID);
            var pidTag = isKnownPid ? "" : " [UNKNOWN PID — new firmware revision?]";

            LogReportDescriptor(device);

            bool needsAltProbes = false;
            try
            {
                using (var stream = device.Open())
                {
                    // Longer read timeout for the identity probe. HidSharpCore's
                    // default behaves like ~3 sec in the field; gen-2 firmware may
                    // respond more slowly than gen-1 to the first packet after
                    // device open, so allow more headroom before declaring timeout.
                    try { stream.ReadTimeout = 5000; } catch { }
                    var raw = stream.WritePacket(Packets.IDENTITY_PACKET);
                    DebugLogger.Log($"  spec packet from PID=0x{device.ProductID:x4}{pidTag}: {raw.PacketToString()}");
                    var specs = new KeyboardSpecs(raw);
                    DebugLogger.Log($"    -> KeyboardType={specs.KeyboardType?.ToString() ?? "null"} Firmware={specs.FirmwareVersion} Compatible={specs.IsCompatible()} RTMatch={specs.RTMatch?.ToString() ?? "null"} AutoMatchMode={specs.AutoMatchMode?.ToString() ?? "null"} LWReplace={specs.LastWinReplace?.ToString() ?? "null"}");
                    if (specs.IsCompatible())
                    {
                        var caps = specs.GetCapabilities();
                        DebugLogger.Log($"    -> Capabilities: Tier={caps.Tier} Precision={caps.Precision} IsTooOld={caps.IsTooOld} Label=\"{caps.Label}\"");
                        DebugLogger.Log($"  Selected PID=0x{device.ProductID:x4}");
                        return (device, specs);
                    }
                    needsAltProbes = true;
                }

                // Standard probe failed. Outer using-scope has closed the stream,
                // so the device is free for the alt-probes to reopen it fresh
                // for each strategy.
                if (needsAltProbes)
                {
                    TryAlternativeIdentityProbes(device);
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"  PID=0x{device.ProductID:x4}{pidTag} ERROR: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Probe sibling interfaces — any HID device that shares VID/PID with
        // a matched candidate but didn't pass the filter (e.g. has 8-byte
        // reports rather than 64+). Gen-2 firmware may split control commands
        // and response notifications across different USB collections, where
        // we'd otherwise miss the response interface entirely.
        var matchedKeys = matched.Select(m => (m.VendorID, m.ProductID)).ToHashSet();
        var siblings = allDevices
            .Where(d => matchedKeys.Contains((d.VendorID, d.ProductID)) && !matched.Contains(d))
            .ToList();
        if (siblings.Count > 0)
        {
            DebugLogger.Log($"  Probing {siblings.Count} sibling interface(s) of matched VID/PIDs (didn't pass main filter):");
            foreach (var sib in siblings)
            {
                LogDeviceMetadata(sib, prefix: "  sibling ");
                LogReportDescriptor(sib);
                TryAlternativeIdentityProbes(sib);
            }
        }

        // Always dump the full HID inventory when probing failed (no
        // throttling on this diagnostic build). Helps spot devices the
        // filter might have wrongly excluded and gives a complete topology
        // snapshot for the maintainer reading the log.
        DebugLogger.Log($"  Full HID inventory ({allDevices.Count} devices):");
        foreach (var d in allDevices)
        {
            LogDeviceMetadata(d, prefix: "    ");
        }

        DebugLogger.Log("  No compatible keyboard found");
        return null;
    }

    // Logs everything HidSharp will tell us about a single HID device — VID/PID,
    // report sizes, manufacturer/product strings, serial, release number, and
    // the Windows device path (which encodes USB topology + collection index).
    // Each field is read independently so a single failed lookup doesn't
    // suppress the rest of the record.
    private static void LogDeviceMetadata(HidDevice d, string prefix)
    {
        int inLen = -1, outLen = -1, featLen = -1;
        string mfg = "?", prod = "?", serial = "?", path = "?", release = "?";
        try { inLen = d.GetMaxInputReportLength(); } catch { }
        try { outLen = d.GetMaxOutputReportLength(); } catch { }
        try { featLen = d.GetMaxFeatureReportLength(); } catch { }
        try { mfg = d.GetManufacturer() ?? ""; } catch { }
        try { prod = d.GetProductName() ?? ""; } catch { }
        try { serial = d.GetSerialNumber() ?? ""; } catch { }
        try { release = d.ReleaseNumber?.ToString() ?? "?"; } catch { }
        try { path = d.DevicePath ?? ""; } catch { }
        DebugLogger.Log($"{prefix}VID=0x{d.VendorID:x4} PID=0x{d.ProductID:x4} InLen={inLen} OutLen={outLen} FeatLen={featLen} Release={release} Mfg='{mfg}' Product='{prod}' Serial='{serial}'");
        DebugLogger.Log($"{prefix}  Path='{path}'");
    }

    // Dumps the raw HID report descriptor bytes for the device. The descriptor
    // is the ground truth about what report IDs, report sizes, and report
    // directions (input/output/feature) the firmware advertises — far more
    // authoritative than MaxOutputReportLength alone. Hex dump is enough for
    // manual decoding by a maintainer; if we ever need to parse it in-app,
    // HidSharpCore exposes a parsed view too.
    private static void LogReportDescriptor(HidDevice device)
    {
        try
        {
            var descriptor = device.GetRawReportDescriptor();
            DebugLogger.Log($"  PID=0x{device.ProductID:x4} report descriptor ({descriptor.Length} bytes): {descriptor.PacketToString()}");
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"  PID=0x{device.ProductID:x4} report descriptor unavailable: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Sends the gen-1 identity bytes via SIX alternative wire framings + a
    // feature-report attempt + an async-listener probe, logging each result.
    // Purely diagnostic — we don't switch to whichever strategy works, that
    // requires a follow-up code change once we know.
    //
    // Strategies (each runs on a fresh device.Open()):
    //   A: No-prefix padded. Buffer = [0xA0, 0x02, ...padded to MaxOutputReportLength].
    //      Tests whether the firmware doesn't want a report ID byte in the
    //      buffer (Windows HID stack would inject it from the descriptor).
    //   B: Report-ID 4 unpadded (64 bytes). Tests whether 65-byte writes
    //      themselves are the problem and the firmware expects exactly 64.
    //   C: Report-ID 0 padded. Tests whether the firmware uses report ID 0.
    //   D: Report-ID 1 padded. The standard Windows keyboard report ID.
    //   E: Report-ID 4 padded as feature report (HidStream.SetFeature).
    //      Tests whether the firmware accepts identity as a feature report
    //      rather than an output report.
    //   F: Async listener. Opens the device, kicks off a background reader
    //      thread that captures ANY input report for 3 seconds, sends the
    //      standard probe, then logs every report received. Catches
    //      responses that arrive after our usual synchronous read times out.
    private static void TryAlternativeIdentityProbes(HidDevice device)
    {
        int outSize = 65;
        try { outSize = device.GetMaxOutputReportLength(); } catch { }
        int featSize = outSize;
        try { featSize = device.GetMaxFeatureReportLength(); } catch { }
        if (featSize <= 0) featSize = outSize;

        var attempts = new (string label, byte[] buffer)[]
        {
            ("A: no-prefix padded", BuildNoPrefixBuffer(Packets.IDENTITY_PACKET, outSize)),
            ("B: report-id 4 unpadded (64 bytes)", BuildPrefixedBuffer(0x04, Packets.IDENTITY_PACKET, 64)),
            ("C: report-id 0 padded", BuildPrefixedBuffer(0x00, Packets.IDENTITY_PACKET, outSize)),
            ("D: report-id 1 padded", BuildPrefixedBuffer(0x01, Packets.IDENTITY_PACKET, outSize)),
        };

        foreach (var (label, buffer) in attempts)
        {
            RunWriteReadProbe(device, label, buffer);
        }

        // Strategy E: feature report. Some HID devices accept control
        // commands only via SetFeature / GetFeature rather than Write / Read.
        RunFeatureReportProbe(device, featSize);

        // Strategy F: async listener. Catches responses that arrive after
        // the synchronous read's timeout window — useful if the firmware
        // takes >5 sec to respond, or if responses come asynchronously
        // over multiple reads.
        RunAsyncListenerProbe(device);
    }

    private static void RunWriteReadProbe(HidDevice device, string label, byte[] buffer)
    {
        DebugLogger.Log($"  [diagnostic] alt-probe {label}: writing {buffer.Length} bytes: {buffer.PacketToString()}");
        try
        {
            using var stream = device.Open();
            try { stream.ReadTimeout = 3000; } catch { }
            try
            {
                stream.Write(buffer);
            }
            catch (Exception writeEx)
            {
                DebugLogger.Log($"  [diagnostic] alt-probe {label}: WRITE FAILED — {writeEx.GetType().Name}: {writeEx.Message}");
                return;
            }
            try
            {
                var response = stream.Read();
                DebugLogger.Log($"  [diagnostic] alt-probe {label}: READ OK ({response.Length} bytes): {response.PacketToString()}");
            }
            catch (Exception readEx)
            {
                DebugLogger.Log($"  [diagnostic] alt-probe {label}: READ FAILED — {readEx.GetType().Name}: {readEx.Message}");
            }
        }
        catch (Exception openEx)
        {
            DebugLogger.Log($"  [diagnostic] alt-probe {label}: device.Open() failed — {openEx.GetType().Name}: {openEx.Message}");
        }
    }

    private static void RunFeatureReportProbe(HidDevice device, int featSize)
    {
        var buffer = BuildPrefixedBuffer(0x04, Packets.IDENTITY_PACKET, featSize);
        DebugLogger.Log($"  [diagnostic] alt-probe E: feature-report (size={featSize}): {buffer.PacketToString()}");
        try
        {
            using var stream = device.Open();
            try
            {
                stream.SetFeature(buffer);
                DebugLogger.Log($"  [diagnostic] alt-probe E: SetFeature OK");
            }
            catch (Exception setEx)
            {
                DebugLogger.Log($"  [diagnostic] alt-probe E: SetFeature FAILED — {setEx.GetType().Name}: {setEx.Message}");
                return;
            }
            try
            {
                var responseBuffer = new byte[featSize];
                responseBuffer[0] = 0x04;
                stream.GetFeature(responseBuffer);
                DebugLogger.Log($"  [diagnostic] alt-probe E: GetFeature OK ({responseBuffer.Length} bytes): {responseBuffer.PacketToString()}");
            }
            catch (Exception getEx)
            {
                DebugLogger.Log($"  [diagnostic] alt-probe E: GetFeature FAILED — {getEx.GetType().Name}: {getEx.Message}");
            }
        }
        catch (Exception openEx)
        {
            DebugLogger.Log($"  [diagnostic] alt-probe E: device.Open() failed — {openEx.GetType().Name}: {openEx.Message}");
        }
    }

    private static void RunAsyncListenerProbe(HidDevice device)
    {
        DebugLogger.Log("  [diagnostic] alt-probe F: async-listener (open device, spawn reader, send standard probe, capture for 3s)");
        try
        {
            using var stream = device.Open();
            try { stream.ReadTimeout = 250; } catch { }

            var captured = new List<byte[]>();
            var cts = new System.Threading.CancellationTokenSource();
            var reader = System.Threading.Tasks.Task.Run(() =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        var data = stream.Read();
                        if (data.Length > 0)
                        {
                            lock (captured) captured.Add(data);
                        }
                    }
                    catch (TimeoutException) { /* expected — short timeout, keep polling */ }
                    catch (Exception ex)
                    {
                        DebugLogger.Log($"  [diagnostic] alt-probe F: reader thread error — {ex.GetType().Name}: {ex.Message}");
                        break;
                    }
                }
            });

            // Standard probe via the production write path so we replicate
            // what the user's normal session sends.
            try
            {
                stream.WritePacket(Packets.IDENTITY_PACKET);
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"  [diagnostic] alt-probe F: write failed — {ex.GetType().Name}: {ex.Message}");
            }

            System.Threading.Thread.Sleep(3000);
            cts.Cancel();
            try { reader.Wait(1000); } catch { }

            lock (captured)
            {
                DebugLogger.Log($"  [diagnostic] alt-probe F: captured {captured.Count} input report(s) over 3s:");
                foreach (var r in captured)
                {
                    DebugLogger.Log($"    <- ({r.Length} bytes): {r.PacketToString()}");
                }
            }
        }
        catch (Exception openEx)
        {
            DebugLogger.Log($"  [diagnostic] alt-probe F: device.Open() failed — {openEx.GetType().Name}: {openEx.Message}");
        }
    }

    private static byte[] BuildNoPrefixBuffer(byte[] packet, int totalLen)
    {
        var buffer = new byte[totalLen];
        Array.Copy(packet, 0, buffer, 0, Math.Min(packet.Length, totalLen));
        return buffer;
    }

    private static byte[] BuildPrefixedBuffer(byte reportId, byte[] packet, int totalLen)
    {
        var buffer = new byte[totalLen];
        buffer[0] = reportId;
        Array.Copy(packet, 0, buffer, 1, Math.Min(packet.Length, totalLen - 1));
        return buffer;
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
