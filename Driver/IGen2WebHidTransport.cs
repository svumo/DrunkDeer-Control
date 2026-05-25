namespace Driver;

// Abstraction over a WebHID-backed transport for OEM keyboards that the
// Windows HID class driver silently filters (gen-2 A75 Pro VID 0x19F5 and
// likely future variants — see docs/gen2-oem-investigation.md). The impl
// lives in WpfApp (it needs WebView2 + a WPF window) but the abstraction
// stays in Driver so KeyboardManager can take an optional dependency
// without pulling WebView2 into the driver layer.
//
// All methods are async and may be called from any thread; the impl
// marshals to its UI thread internally.
public interface IGen2WebHidTransport
{
    // Whether the underlying WebView2 has finished initializing. Detection
    // code should not call any other method until this is true (or call
    // EnsureReadyAsync first).
    bool IsReady { get; }

    // Whether navigator.hid is actually exposed in the embedded WebView2
    // context. Will be true on machines where WebHID-via-WebView2 works,
    // false where it doesn't (WebView2 Runtime too old, enterprise policy
    // disabling WebHID, etc). Only meaningful once IsReady is true — read
    // it after EnsureReadyAsync to decide whether to attempt WebHID
    // detection or skip straight to other strategies.
    bool IsWebHidApiAvailable { get; }

    // Whether a device is currently open and ready for SendAsync calls.
    bool IsConnected { get; }

    // VID/PID of the currently-open device, or (0, 0) if none.
    (int vid, int pid) ConnectedDevice { get; }

    // Fires when a HID input report arrives from the connected device.
    // payload is the raw report bytes (no Report ID prefix — the impl
    // exposes reportId separately for symmetry with HidStream semantics).
    event Action<byte, byte[]>? InputReportReceived;

    // Fires when the connected device gets unplugged or otherwise closes.
    event Action? Disconnected;

    // Waits for WebView2 initialization to finish. Safe to call multiple
    // times. Throws if WebView2 runtime is missing or init fails — caller
    // should treat as "WebHID transport unavailable, fall through to other
    // detection paths".
    Task EnsureReadyAsync(CancellationToken ct = default);

    // Attempts to re-open a device whose permission was granted in a
    // previous session (Chromium persists WebHID grants in the user-data
    // folder). Does NOT show the device picker — returns false if no
    // matching previously-permitted device is found.
    //
    // pid=0 means "any product ID with the given VID" (useful when an OEM
    // batch uses multiple PIDs).
    //
    // usagePage/usage (optional, both must be ≥0 to apply): require the
    // matching device to expose a collection with this usagePage and usage.
    // Set this when the physical device exposes multiple HID interfaces
    // and you only want one (e.g. OEM A75 Pro at VID 0x19F5 exposes a boot
    // keyboard collection at usage=6 AND the vendor data collection at
    // usage=0 — only the latter accepts our writes). Without this filter,
    // a leftover bad permission from a previous mis-pick will silently
    // re-bond on every launch.
    Task<bool> TryReconnectAsync(int vid, int pid, int usagePage = -1, int usage = -1, CancellationToken ct = default);

    // Shows the Chromium device picker so the user can grant permission to
    // a device matching the given VID. MUST be called from a user-gesture
    // context (a button click in the WPF UI) — Chromium rejects programmatic
    // requestDevice calls. The impl makes the WebView2 window briefly
    // visible during the picker so the user can interact with it.
    //
    // usagePage/usage (optional, both must be ≥0 to apply): narrow the
    // picker's filter so only HID interfaces with this usagePage/usage
    // pairing appear. Use the same filter as TryReconnectAsync to keep
    // the user from picking a sibling interface that we can't write to.
    //
    // Returns true if the user picked a device and we successfully opened
    // it. Returns false if the user cancelled or no device was picked.
    Task<bool> RequestPermissionAsync(int vid, int usagePage = -1, int usage = -1, CancellationToken ct = default);

    // Revokes WebHID permission for any previously-permitted device matching
    // the given VID (and PID if non-zero). Returns the number of devices
    // forgotten. Use this when reconnect succeeded against the wrong device
    // (e.g. a leftover bad pick that doesn't accept writes) so the next
    // picker session starts with a clean slate. Safe no-op if the transport
    // isn't ready or no matching device is persisted.
    Task<int> ForgetDeviceAsync(int vid, int pid = 0, CancellationToken ct = default);

    // Sends a single HID output report to the connected device. Returns
    // true if the JS bridge accepted the send; throws on transport-level
    // errors (no device, bridge crashed). Does not wait for any response;
    // callers that need a response listen on InputReportReceived.
    Task<bool> SendReportAsync(byte reportId, byte[] payload, CancellationToken ct = default);

    // Closes the open device (if any). Safe to call when nothing's open.
    Task CloseDeviceAsync(CancellationToken ct = default);
}
