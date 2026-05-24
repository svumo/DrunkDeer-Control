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
                // Clean up any gen-2 channel associated with the OUTGOING
                // keyboard. The next FindKeyboard pass will re-register a
                // fresh channel if the new device needs one. Without this
                // we'd leak file handles every time a gen-2 keyboard is
                // unplugged and replugged.
                var leavingPath = _keyboardWithSpecs?.Keyboard.DevicePath;
                if (!string.IsNullOrEmpty(leavingPath))
                {
                    HidDeviceExtensions.UnregisterGen2Channel(leavingPath);
                }
                _keyboardWithSpecs = value;
                ConnectedKeyboardChanged?.Invoke(_keyboardWithSpecs);
            }
        }
    }
    public event Action<KeyboardWithSpecs?>? ConnectedKeyboardChanged;

    // Fires when initial detection runs and finds a candidate gen-2 OEM
    // keyboard (VID 0x19F5 etc) that needs WebHID consent — i.e. standard
    // detection failed AND the WebHID transport has no previously-saved
    // permission for this device. MainWindow listens and shows the consent
    // dialog.
    public event Action<int /*vid*/, int /*pid*/>? Gen2WebHidConsentNeeded;

    private readonly IGen2WebHidTransport? _webHidTransport;

    // Singleton raw-input receiver — bypasses HidClass.sys's filtering on
    // OEM keyboards where ReadFile / HidD_GetInputReport return nothing.
    // Lazily-initialized in FindKeyboard so we don't pay the message-pump
    // thread cost on processes that never need it (e.g. profile-tool runs).
    private static RawInputReceiver? _sharedRawInputReceiver;
    private static readonly object _rawInputReceiverLock = new();

    private static RawInputReceiver? GetOrCreateRawInputReceiver()
    {
        lock (_rawInputReceiverLock)
        {
            if (_sharedRawInputReceiver is null)
            {
                try
                {
                    _sharedRawInputReceiver = new RawInputReceiver();
                }
                catch (Exception ex)
                {
                    DebugLogger.Log($"KeyboardManager: RawInputReceiver construction failed — {ex.GetType().Name}: {ex.Message}");
                    return null;
                }
            }
            return _sharedRawInputReceiver;
        }
    }

    public KeyboardManager() : this(null) { }

    public KeyboardManager(IGen2WebHidTransport? webHidTransport)
    {
        _webHidTransport = webHidTransport;
        // Initial scan runs synchronously. WebHID detection inside FindKeyboard
        // only kicks in if the transport happens to already be ready — which
        // it usually isn't this early (WebView2 init is queued on the UI
        // dispatcher and won't run until we yield). RefreshAsync, called from
        // MainWindow.Loaded, retries after init completes.
        KeyboardWithSpecs = FindKeyboardCore(_webHidTransport, this);
        Register();
    }

    // Public re-scan entry-point. Call this after WebView2 finishes
    // initializing (the initial scan from the constructor may have run
    // before transport.IsReady went true). MainWindow.Loaded triggers
    // this once on startup.
    public async Task RefreshAsync()
    {
        if (_webHidTransport is not null)
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _webHidTransport.EnsureReadyAsync(cts.Token);
            }
            catch
            {
                // Transport unavailable — fall through to standard probe.
            }
        }
        KeyboardWithSpecs = await System.Threading.Tasks.Task.Run(() => FindKeyboardCore(_webHidTransport, this));
    }

    // Called by MainWindow after the user grants WebHID permission in the
    // consent dialog. Runs a re-scan that includes WebHID reconnect.
    public Task OnWebHidConsentGrantedAsync() => RefreshAsync();

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
        KeyboardWithSpecs = FindKeyboardCore(_webHidTransport, this);
    }

    // beta.4 diagnostic build — throttling intentionally removed. Every scan
    // dumps everything so we maximize evidence for the gen-2 firmware
    // identity-timeout investigation. Will reinstate when we ship a
    // non-diagnostic build.

    private static KeyboardWithSpecs? FindKeyboardCore(IGen2WebHidTransport? webHidTransport, KeyboardManager owner)
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

                // Standard probe via HidSharp failed. Before giving up on
                // detection, try the gen-2 production path: HidD_SetOutputReport
                // for writes + HidD_GetInputReport polling across zero-access
                // sibling handles for reads. This is the same path Chrome's
                // WebHID uses to talk to gen-2 firmware whose response
                // interface is a Keyboard/Mouse collection blocked from
                // normal user-mode read access. If detection succeeds, the
                // Gen2KeyboardChannel stays registered so every subsequent
                // WritePacket / WriteFullProfile call on a stream of this
                // device is automatically routed through the same path.
                var gen2Specs = TryGen2Detection(device, allDevices);
                if (gen2Specs is not null)
                {
                    return (device, gen2Specs);
                }

                // Gen-2 HidD_SetOutputReport path also failed. Try the
                // WebHID transport — only succeeds if Chrome/WebView2 has
                // a previously-granted permission for this device. Silent
                // first time; the consent dialog (driven by MainWindow on
                // the Gen2WebHidConsentNeeded event below) gets the user
                // through the one-time picker.
                //
                // beta.13: also require IsWebHidApiAvailable. The bridge now
                // posts 'ready' even when navigator.hid is undefined (so we
                // get a diagnostic signal), but actually calling
                // requestDevice on such a transport would just time out.
                if (webHidTransport is not null && webHidTransport.IsReady && webHidTransport.IsWebHidApiAvailable)
                {
                    var webHidSpecs = TryGen2WebHidDetection(device, webHidTransport);
                    if (webHidSpecs is not null)
                    {
                        return (device, webHidSpecs);
                    }
                    // No saved permission. Signal the UI to prompt the user.
                    // We only emit this for the OEM-variant VIDs known to
                    // need WebHID (0x19F5 today; extend the list if more
                    // turn up). Don't spam the event for every gen-1 device
                    // that happens to fail the standard probe transiently.
                    if (device.VendorID == 0x19F5)
                    {
                        DebugLogger.Log($"  WebHID consent needed for VID=0x{device.VendorID:x4} PID=0x{device.ProductID:x4} — signalling UI");
                        try { owner.Gen2WebHidConsentNeeded?.Invoke(device.VendorID, device.ProductID); } catch { }
                    }
                }
                else if (device.VendorID == 0x19F5)
                {
                    var why = webHidTransport is null ? "no transport"
                            : !webHidTransport.IsReady ? "transport not ready"
                            : "navigator.hid unavailable in WebView2";
                    DebugLogger.Log($"  Skipping WebHID detection for VID=0x{device.VendorID:x4} PID=0x{device.ProductID:x4} — {why}");
                }

                // Both standard and gen-2 detection failed. Outer using-scope
                // has closed the HidSharp stream, so the device is free for
                // the diagnostic alt-probes to reopen it fresh for each
                // strategy. Wrapped in its own try-catch so any probe-side
                // bug doesn't abort the rest of FindKeyboard (beta.4 shipped
                // a crash here that prevented the app from even reaching its
                // main window).
                if (needsAltProbes)
                {
                    try { TryAlternativeIdentityProbes(device); }
                    catch (Exception probeEx)
                    {
                        DebugLogger.Log($"  PID=0x{device.ProductID:x4} alt-probes crashed — {probeEx.GetType().Name}: {probeEx.Message}");
                    }
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
        //
        // Critical ordering: log ALL sibling metadata + descriptors FIRST,
        // then probe in a second pass. If a probe panics on a malformed
        // interface (e.g. beta.4's IndexOutOfRangeException from zero-len
        // output), we still get the topology snapshot in the log.
        var matchedKeys = matched.Select(m => (m.VendorID, m.ProductID)).ToHashSet();
        var siblings = allDevices
            .Where(d => matchedKeys.Contains((d.VendorID, d.ProductID)) && !matched.Contains(d))
            .ToList();
        if (siblings.Count > 0)
        {
            DebugLogger.Log($"  {siblings.Count} sibling interface(s) of matched VID/PIDs — logging all metadata + descriptors first, then probing:");
            foreach (var sib in siblings)
            {
                LogDeviceMetadata(sib, prefix: "  sibling ");
                LogReportDescriptor(sib);
            }
            foreach (var sib in siblings)
            {
                int sibOut = -1;
                try { sibOut = sib.GetMaxOutputReportLength(); } catch { }
                if (sibOut < 64)
                {
                    DebugLogger.Log($"  sibling PID=0x{sib.ProductID:x4} path-tail={DevicePathTail(sib)} OutLen={sibOut} — skipping write probes (cannot send 64-byte commands)");
                    continue;
                }
                DebugLogger.Log($"  sibling PID=0x{sib.ProductID:x4} path-tail={DevicePathTail(sib)} OutLen={sibOut} — running alt-probes");
                try { TryAlternativeIdentityProbes(sib); }
                catch (Exception ex)
                {
                    DebugLogger.Log($"  sibling probe crashed — {ex.GetType().Name}: {ex.Message}");
                }
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
            DebugLogger.Log($"  PID=0x{device.ProductID:x4} path-tail={DevicePathTail(device)} report descriptor ({descriptor.Length} bytes): {descriptor.PacketToString()}");
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"  PID=0x{device.ProductID:x4} path-tail={DevicePathTail(device)} report descriptor unavailable: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Returns the trailing slice of the Windows HID device path — the part
    // that encodes interface index (`mi_NN`) and collection (`col_NN`). The
    // full path is verbose and noisy; the tail is enough to disambiguate
    // sibling interfaces in the log without 100+ chars per line.
    private static string DevicePathTail(HidDevice device)
    {
        try
        {
            var p = device.DevicePath ?? "";
            var first = p.IndexOf('#');
            if (first < 0) return p;
            var second = p.IndexOf('#', first + 1);
            if (second < 0) return p[(first + 1)..];
            return p[(first + 1)..second];
        }
        catch { return "?"; }
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
        int featSize = 0;
        try { featSize = device.GetMaxFeatureReportLength(); } catch { }

        // Skip output-bearing probes entirely if the interface has no
        // output capability (would crash on zero-length buffer arithmetic
        // or silently no-op at the HID layer).
        if (outSize <= 0)
        {
            DebugLogger.Log($"  [diagnostic] PID=0x{device.ProductID:x4} OutLen=0 — skipping write/read probes (input-only interface)");
        }
        else
        {
            var attempts = new (string label, byte[] buffer)[]
            {
                ("A: no-prefix padded", BuildNoPrefixBuffer(Packets.IDENTITY_PACKET, outSize)),
                ("B: report-id 4 unpadded (64 bytes)", BuildPrefixedBuffer(0x04, Packets.IDENTITY_PACKET, 64)),
                ("C: report-id 0 padded", BuildPrefixedBuffer(0x00, Packets.IDENTITY_PACKET, outSize)),
                ("D: report-id 1 padded", BuildPrefixedBuffer(0x01, Packets.IDENTITY_PACKET, outSize)),
            };

            foreach (var (label, buffer) in attempts)
            {
                if (buffer.Length == 0)
                {
                    DebugLogger.Log($"  [diagnostic] alt-probe {label}: skipped (zero-length buffer)");
                    continue;
                }
                RunWriteReadProbe(device, label, buffer);
            }

            // Strategy G: HidD_SetOutputReport via P/Invoke. The output endpoint
            // route (stream.Write → WriteFile) silently drops writes on HID
            // interfaces that declare 0-length output endpoints in their USB
            // descriptors. SetOutputReport routes the write through a control
            // transfer instead, which works regardless of endpoint topology.
            // WebHID's sendReport falls back to this internally; HidSharpCore's
            // stream.Write does not.
            RunHidDSetOutputReportProbe(device, outSize);

            // Strategy F: async listener. Catches responses that arrive after
            // the synchronous read's timeout window — useful if the firmware
            // takes >5 sec to respond, or if responses come asynchronously
            // over multiple reads.
            RunAsyncListenerProbe(device);

            // Strategy H: hybrid SetOutputReport + async read on HidStream.
            // Replicates WebHID's sendReport + inputreport-event pattern.
            // Beta.5 log proved SetOutputReport reaches the firmware (Strategy G
            // returned OK); GetInputReport failed only because it doesn't wait
            // for new data. HidStream.Read IS interrupt-driven (kernel buffers
            // input reports until we read them). If gen-2 firmware responds
            // via the input pipe at all, this is the combination that catches
            // it.
            RunHybridSetOutputAsyncReadProbe(device, outSize);

            // Strategy J: multi-collection listener. Beta.6 confirmed the
            // gen-2 web driver (Chrome's WebHID) successfully talks to this
            // user's keyboard — meaning the protocol IS reachable, we're just
            // listening on the wrong pipe. WebHID merges all HID collections
            // of a VID/PID into one logical device and listens on ALL of
            // their input pipes simultaneously. HidSharpCore exposes each
            // collection as a separate device, so we must open them all
            // ourselves. The gen-2 protocol's `$B` handler in index.js
            // explicitly checks `reportId === 4` on incoming reports — so
            // the spec response is tagged with report ID 4 and may arrive
            // on any sibling collection that declares it, not necessarily
            // the one we sent the command on. This strategy opens every
            // sibling collection, listens in parallel, sends via
            // SetOutputReport on mi_01, and logs which collection (if any)
            // produces input data.
            RunMultiCollectionListenerProbe(device, outSize);

            // (Strategy K is now wired in as production detection in
            // TryGen2Detection — runs BEFORE the diagnostic alt-probes block
            // in FindKeyboard. If gen-2 detection succeeds, FindKeyboard
            // returns early with the channel registered and we never reach
            // here at all.)
        }

        // Strategy E: feature report. Some HID devices accept control
        // commands only via SetFeature / GetFeature rather than Write / Read.
        // Skip if no feature reports are supported.
        if (featSize <= 0)
        {
            DebugLogger.Log($"  [diagnostic] PID=0x{device.ProductID:x4} FeatLen=0 — skipping feature-report probe (not supported by this interface)");
        }
        else
        {
            RunFeatureReportProbe(device, featSize);
        }
    }

    // P/Invoke into hid.dll for the control-transfer write path. WriteFile
    // on a HID device requires the interface to expose an output endpoint;
    // HidD_SetOutputReport routes the report over a USB control transfer
    // instead, which works even for interfaces that only have input endpoints.
    [System.Runtime.InteropServices.DllImport("hid.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern bool HidD_SetOutputReport(IntPtr hidDeviceObject, byte[] reportBuffer, int reportBufferLength);

    [System.Runtime.InteropServices.DllImport("hid.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern bool HidD_GetInputReport(IntPtr hidDeviceObject, byte[] reportBuffer, int reportBufferLength);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint GENERIC_READ = 0x80000000u;
    private const uint GENERIC_WRITE = 0x40000000u;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    // Gen-2 WebHID detection. Asks the WebHID transport to silently
    // reconnect to a previously-permitted device matching this VID/PID. If
    // successful, builds a Gen2WebHidChannel, sends the identity packet,
    // parses the response, and returns the specs (channel registered for
    // routing).
    //
    // Returns null if no saved permission exists OR if the reconnected
    // device doesn't respond to the identity packet within timeout. Caller
    // is expected to emit Gen2WebHidConsentNeeded so the UI can prompt
    // the user through the picker.
    private static KeyboardSpecs? TryGen2WebHidDetection(HidDevice vendorDevice, IGen2WebHidTransport transport)
    {
        DebugLogger.Log($"  Attempting gen-2 WebHID detection on PID=0x{vendorDevice.ProductID:x4}");
        bool reconnected;
        try
        {
            reconnected = transport.TryReconnectAsync(vendorDevice.VendorID, vendorDevice.ProductID).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"  gen-2 WebHID: TryReconnectAsync threw — {ex.GetType().Name}: {ex.Message}");
            return null;
        }
        if (!reconnected)
        {
            DebugLogger.Log("  gen-2 WebHID: no previously-permitted device found (needs user consent via picker)");
            return null;
        }

        var channel = new Gen2WebHidChannel(transport, vendorDevice);
        var raw = channel.WriteAndPoll(Packets.IDENTITY_PACKET, pollMs: 2000, expectFirstByte: 0xA0);
        DebugLogger.Log($"  gen-2 WebHID spec packet: {raw.PacketToString()}");
        if (raw.Length < 3)
        {
            DebugLogger.Log("  gen-2 WebHID: no spec response, abandoning");
            channel.Dispose();
            return null;
        }

        var specs = new KeyboardSpecs(raw);
        DebugLogger.Log($"    -> KeyboardType={specs.KeyboardType?.ToString() ?? "null"} Firmware={specs.FirmwareVersion} Compatible={specs.IsCompatible()}");
        if (!specs.IsCompatible())
        {
            DebugLogger.Log("  gen-2 WebHID: response parsed but KeyboardType unknown, abandoning");
            channel.Dispose();
            return null;
        }

        var path = vendorDevice.DevicePath ?? "";
        HidDeviceExtensions.RegisterGen2Channel(path, channel);
        var caps = specs.GetCapabilities();
        DebugLogger.Log($"    -> Capabilities: Tier={caps.Tier} Precision={caps.Precision} IsTooOld={caps.IsTooOld} Label=\"{caps.Label}\"");
        DebugLogger.Log($"  Selected (gen-2 WebHID) PID=0x{vendorDevice.ProductID:x4} — Gen2WebHidChannel registered for {path}");
        return specs;
    }

    // Production gen-2 detection. Opens a Gen2KeyboardChannel rooted at the
    // matched vendor device (mi_01), with zero-access read handles on every
    // sibling collection. Sends the gen-1-style identity packet via
    // HidD_SetOutputReport and polls HidD_GetInputReport across all read
    // handles for a [0xA0, 0x02, 0x00, ...] spec response. If we get one,
    // registers the channel in HidDeviceExtensions so all subsequent
    // WritePacket / WritePacketNoAck calls on streams of this device get
    // auto-routed through the channel — meaning ProfileManager sync,
    // tracking, etc. all just work without any additional changes elsewhere.
    //
    // Returns the parsed KeyboardSpecs on success, or null if no usable
    // response came back within the timeout. Caller checks for null and
    // falls through to the diagnostic alt-probes.
    private static KeyboardSpecs? TryGen2Detection(HidDevice vendorDevice, IEnumerable<HidDevice> allDevices)
    {
        DebugLogger.Log($"  Attempting gen-2 detection via SetOutputReport + GetInputReport polling on PID=0x{vendorDevice.ProductID:x4}");
        var siblings = allDevices
            .Where(d => d.VendorID == vendorDevice.VendorID && d.ProductID == vendorDevice.ProductID)
            .ToList();

        var rawInput = GetOrCreateRawInputReceiver();
        if (rawInput is not null && !rawInput.IsReady)
        {
            DebugLogger.Log("  gen-2 detection: RawInputReceiver not ready yet — proceeding without raw-input read path");
        }
        var channel = Gen2KeyboardChannel.TryOpen(vendorDevice, siblings, rawInput);
        if (channel is null)
        {
            DebugLogger.Log("  gen-2 detection: channel open failed, abandoning");
            return null;
        }

        if (channel.ReadHandleCount == 0)
        {
            DebugLogger.Log("  gen-2 detection: zero read handles opened (no usable response channel), abandoning");
            channel.Dispose();
            return null;
        }

        // Send identity, expect the gen-1-format spec response [0xA0, 0x02, 0x00, ...].
        var raw = channel.WriteAndPoll(Packets.IDENTITY_PACKET, pollMs: 2000, expectFirstByte: 0xA0);
        DebugLogger.Log($"  gen-2 detection spec packet: {raw.PacketToString()}");
        if (raw.Length < 3)
        {
            DebugLogger.Log("  gen-2 detection: no spec response captured, abandoning");
            channel.Dispose();
            return null;
        }

        var specs = new KeyboardSpecs(raw);
        DebugLogger.Log($"    -> KeyboardType={specs.KeyboardType?.ToString() ?? "null"} Firmware={specs.FirmwareVersion} Compatible={specs.IsCompatible()}");
        if (!specs.IsCompatible())
        {
            DebugLogger.Log("  gen-2 detection: response parsed but KeyboardType unknown, abandoning");
            channel.Dispose();
            return null;
        }

        var caps = specs.GetCapabilities();
        DebugLogger.Log($"    -> Capabilities: Tier={caps.Tier} Precision={caps.Precision} IsTooOld={caps.IsTooOld} Label=\"{caps.Label}\"");
        var path = vendorDevice.DevicePath ?? "";
        HidDeviceExtensions.RegisterGen2Channel(path, channel);
        DebugLogger.Log($"  Selected (gen-2) PID=0x{vendorDevice.ProductID:x4} — Gen2KeyboardChannel registered for {path}");
        return specs;
    }

    private static void RunMultiCollectionListenerProbe(HidDevice mi01Device, int outSize)
    {
        var buffer = BuildPrefixedBuffer(0x04, Packets.IDENTITY_PACKET, outSize);
        if (buffer.Length == 0)
        {
            DebugLogger.Log("  [diagnostic] alt-probe J: skipped (zero-length buffer)");
            return;
        }

        DebugLogger.Log("  [diagnostic] alt-probe J: multi-collection listener — open every VID/PID sibling, listen in parallel, write on mi_01 via SetOutputReport");

        var siblings = DeviceList.Local.GetHidDevices()
            .Where(d => d.VendorID == mi01Device.VendorID && d.ProductID == mi01Device.ProductID)
            .ToList();
        DebugLogger.Log($"  [diagnostic] alt-probe J: attempting to open {siblings.Count} collection(s) of VID=0x{mi01Device.VendorID:x4} PID=0x{mi01Device.ProductID:x4}");

        var streams = new List<(HidDevice device, HidStream stream)>();
        var captured = new List<(HidDevice device, byte[] data, DateTime ts)>();
        var capturedLock = new object();
        var cts = new System.Threading.CancellationTokenSource();
        var readers = new List<System.Threading.Tasks.Task>();
        IntPtr writeHandle = IntPtr.Zero;

        try
        {
            foreach (var sib in siblings)
            {
                HidStream? s = null;
                int inLen = -1;
                try { inLen = sib.GetMaxInputReportLength(); } catch { }
                try
                {
                    s = sib.Open();
                    try { s.ReadTimeout = 200; } catch { }
                    streams.Add((sib, s));
                    DebugLogger.Log($"  [diagnostic] alt-probe J: opened reader on {DevicePathTail(sib)} (InLen={inLen})");
                }
                catch (Exception ex)
                {
                    DebugLogger.Log($"  [diagnostic] alt-probe J: open FAILED on {DevicePathTail(sib)} — {ex.GetType().Name}: {ex.Message}");
                }
            }

            foreach (var (sibDev, sibStream) in streams)
            {
                var capDev = sibDev;
                var capStream = sibStream;
                readers.Add(System.Threading.Tasks.Task.Run(() =>
                {
                    while (!cts.IsCancellationRequested)
                    {
                        try
                        {
                            var data = capStream.Read();
                            if (data.Length > 0)
                            {
                                lock (capturedLock) captured.Add((capDev, data, DateTime.Now));
                            }
                        }
                        catch (TimeoutException) { /* expected, keep polling */ }
                        catch (Exception ex)
                        {
                            DebugLogger.Log($"  [diagnostic] alt-probe J: reader on {DevicePathTail(capDev)} stopped — {ex.GetType().Name}: {ex.Message}");
                            break;
                        }
                    }
                }));
            }

            var path = mi01Device.DevicePath;
            if (string.IsNullOrEmpty(path))
            {
                DebugLogger.Log("  [diagnostic] alt-probe J: mi_01 device path unavailable, skipping write");
            }
            else
            {
                writeHandle = CreateFile(
                    path,
                    GENERIC_READ | GENERIC_WRITE,
                    FILE_SHARE_READ | FILE_SHARE_WRITE,
                    IntPtr.Zero,
                    OPEN_EXISTING,
                    0,
                    IntPtr.Zero);
                if (writeHandle == INVALID_HANDLE_VALUE)
                {
                    var err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                    DebugLogger.Log($"  [diagnostic] alt-probe J: CreateFile for write FAILED (Win32 err {err})");
                    writeHandle = IntPtr.Zero;
                }
                else
                {
                    DebugLogger.Log($"  [diagnostic] alt-probe J: sending identity via SetOutputReport (size={buffer.Length}): {buffer.PacketToString()}");
                    var setOk = HidD_SetOutputReport(writeHandle, buffer, buffer.Length);
                    if (!setOk)
                    {
                        var err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                        DebugLogger.Log($"  [diagnostic] alt-probe J: SetOutputReport FAILED (Win32 err {err}) — readers will still run for 3s in case of async response");
                    }
                    else
                    {
                        DebugLogger.Log("  [diagnostic] alt-probe J: SetOutputReport OK — listening on all collections for 3s");
                    }
                }
            }

            System.Threading.Thread.Sleep(3000);
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"  [diagnostic] alt-probe J: outer exception — {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            cts.Cancel();
            try { System.Threading.Tasks.Task.WaitAll(readers.ToArray(), 1500); } catch { }
            if (writeHandle != IntPtr.Zero && writeHandle != INVALID_HANDLE_VALUE) CloseHandle(writeHandle);
            foreach (var (_, s) in streams)
            {
                try { s.Dispose(); } catch { }
            }
        }

        lock (capturedLock)
        {
            DebugLogger.Log($"  [diagnostic] alt-probe J: TOTAL captured = {captured.Count} input report(s) across {streams.Count} collection(s):");
            foreach (var (dev, data, ts) in captured)
            {
                DebugLogger.Log($"    <- [{ts:HH:mm:ss.fff}] from {DevicePathTail(dev)} ({data.Length} bytes): {data.PacketToString()}");
            }
            if (captured.Count == 0)
            {
                DebugLogger.Log("  [diagnostic] alt-probe J: zero captures — gen-2 firmware not responding to identity bytes on any collection. Different protocol likely required.");
            }
        }
    }

    private static void RunHybridSetOutputAsyncReadProbe(HidDevice device, int outSize)
    {
        var buffer = BuildPrefixedBuffer(0x04, Packets.IDENTITY_PACKET, outSize);
        if (buffer.Length == 0)
        {
            DebugLogger.Log("  [diagnostic] alt-probe H: skipped (zero-length buffer)");
            return;
        }
        DebugLogger.Log($"  [diagnostic] alt-probe H: SetOutputReport + async-read hybrid (size={buffer.Length}): {buffer.PacketToString()}");

        IntPtr writeHandle = IntPtr.Zero;
        HidStream? readStream = null;
        try
        {
            readStream = device.Open();
            try { readStream.ReadTimeout = 250; } catch { }

            var path = device.DevicePath;
            if (string.IsNullOrEmpty(path))
            {
                DebugLogger.Log("  [diagnostic] alt-probe H: device path unavailable, skipping");
                return;
            }
            writeHandle = CreateFile(
                path,
                GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                0,
                IntPtr.Zero);
            if (writeHandle == INVALID_HANDLE_VALUE)
            {
                var err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                DebugLogger.Log($"  [diagnostic] alt-probe H: CreateFile failed (Win32 err {err})");
                return;
            }

            var captured = new List<byte[]>();
            var cts = new System.Threading.CancellationTokenSource();
            var localStream = readStream;
            var reader = System.Threading.Tasks.Task.Run(() =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        var data = localStream.Read();
                        if (data.Length > 0)
                        {
                            lock (captured) captured.Add(data);
                        }
                    }
                    catch (TimeoutException) { /* expected, keep polling */ }
                    catch (Exception ex)
                    {
                        DebugLogger.Log($"  [diagnostic] alt-probe H: reader thread error — {ex.GetType().Name}: {ex.Message}");
                        break;
                    }
                }
            });

            var setOk = HidD_SetOutputReport(writeHandle, buffer, buffer.Length);
            if (!setOk)
            {
                var err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                DebugLogger.Log($"  [diagnostic] alt-probe H: HidD_SetOutputReport FAILED (Win32 err {err}) — reader will still run for 3s in case of async response");
            }
            else
            {
                DebugLogger.Log("  [diagnostic] alt-probe H: HidD_SetOutputReport OK — waiting 3s for async input report(s)");
            }

            System.Threading.Thread.Sleep(3000);
            cts.Cancel();
            try { reader.Wait(1000); } catch { }

            lock (captured)
            {
                DebugLogger.Log($"  [diagnostic] alt-probe H: captured {captured.Count} input report(s) over 3s:");
                foreach (var r in captured)
                {
                    DebugLogger.Log($"    <- ({r.Length} bytes): {r.PacketToString()}");
                }
            }
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"  [diagnostic] alt-probe H: exception — {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (writeHandle != IntPtr.Zero && writeHandle != INVALID_HANDLE_VALUE) CloseHandle(writeHandle);
            readStream?.Dispose();
        }
    }

    private static void RunHidDSetOutputReportProbe(HidDevice device, int outSize)
    {
        var buffer = BuildPrefixedBuffer(0x04, Packets.IDENTITY_PACKET, outSize);
        if (buffer.Length == 0)
        {
            DebugLogger.Log("  [diagnostic] alt-probe G: HidD_SetOutputReport — skipped (zero-length buffer)");
            return;
        }
        DebugLogger.Log($"  [diagnostic] alt-probe G: HidD_SetOutputReport (size={buffer.Length}): {buffer.PacketToString()}");

        IntPtr handle = IntPtr.Zero;
        try
        {
            var path = device.DevicePath;
            if (string.IsNullOrEmpty(path))
            {
                DebugLogger.Log("  [diagnostic] alt-probe G: device path unavailable, skipping");
                return;
            }
            handle = CreateFile(
                path,
                GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                0,
                IntPtr.Zero);
            if (handle == INVALID_HANDLE_VALUE)
            {
                var err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                DebugLogger.Log($"  [diagnostic] alt-probe G: CreateFile failed (Win32 err {err})");
                return;
            }

            var setOk = HidD_SetOutputReport(handle, buffer, buffer.Length);
            if (!setOk)
            {
                var err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                DebugLogger.Log($"  [diagnostic] alt-probe G: HidD_SetOutputReport FAILED (Win32 err {err})");
                return;
            }
            DebugLogger.Log("  [diagnostic] alt-probe G: HidD_SetOutputReport OK — attempting HidD_GetInputReport");

            var readBuffer = new byte[outSize];
            readBuffer[0] = 0x04;
            var getOk = HidD_GetInputReport(handle, readBuffer, readBuffer.Length);
            if (!getOk)
            {
                var err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                DebugLogger.Log($"  [diagnostic] alt-probe G: HidD_GetInputReport FAILED (Win32 err {err})");
                return;
            }
            DebugLogger.Log($"  [diagnostic] alt-probe G: HidD_GetInputReport OK ({readBuffer.Length} bytes): {readBuffer.PacketToString()}");
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"  [diagnostic] alt-probe G: exception — {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (handle != IntPtr.Zero && handle != INVALID_HANDLE_VALUE) CloseHandle(handle);
        }
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
        if (totalLen <= 0) return [];
        var buffer = new byte[totalLen];
        Array.Copy(packet, 0, buffer, 0, Math.Min(packet.Length, totalLen));
        return buffer;
    }

    private static byte[] BuildPrefixedBuffer(byte reportId, byte[] packet, int totalLen)
    {
        if (totalLen <= 0) return [];
        var buffer = new byte[totalLen];
        buffer[0] = reportId;
        if (totalLen > 1)
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
        // Catch any stray gen-2 channels that didn't get cleaned up through
        // the normal disconnect path (paranoia — KeyboardWithSpecs=null
        // above should already have triggered cleanup for the current
        // device).
        HidDeviceExtensions.ClearAllGen2Channels();
        // Tear down the shared RawInputReceiver. Safe to dispose even if
        // never created — the lock-guarded null check is enough.
        lock (_rawInputReceiverLock)
        {
            try { _sharedRawInputReceiver?.Dispose(); } catch { }
            _sharedRawInputReceiver = null;
        }
    }

    public bool IsConnected()
    {
        if (KeyboardWithSpecs is not { } keyboard) return false;
        using var stream = keyboard.Keyboard.Open();
        return stream.Ping();
    }
}
