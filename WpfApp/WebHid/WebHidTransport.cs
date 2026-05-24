using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Driver;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace WpfApp.WebHid;

// WebView2-backed implementation of IGen2WebHidTransport.
//
// Lifecycle:
//   - Construction kicks off WebView2 init on the UI thread (async)
//   - EnsureReadyAsync awaits init + first "ready" message from the JS bridge
//   - TryReconnectAsync calls navigator.hid.getDevices() and opens a matching
//     previously-permitted device — silent, no UI
//   - RequestPermissionAsync makes the hidden window visible, posts an
//     "armPicker" command to JS, and waits for the user to click the page's
//     Connect button (Chromium requires a user gesture for requestDevice).
//     Returns when the user picks a device or cancels.
//   - SendReportAsync forwards to the open device via JS device.sendReport.
//
// All public methods are thread-safe. Internal: every WebView2 access is
// marshalled to the UI thread via _window.Dispatcher.Invoke. Cross-thread
// callers await Tasks that complete on a pool thread (so they don't
// re-enter the UI thread).
public sealed class WebHidTransport : IGen2WebHidTransport, IDisposable
{
    private const int RequestTimeoutMs = 5000;

    private readonly object _lock = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly TaskCompletionSource<bool> _readyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<bool>? _activePickerTcs;
    private Window? _window;
    private WebView2? _webView;
    private int _nextRequestId;
    private (int vid, int pid) _connected;
    private bool _isReady;
    private bool _webHidApiAvailable;
    private bool _disposed;

    public bool IsReady => _isReady;
    public bool IsWebHidApiAvailable => _webHidApiAvailable;
    public bool IsConnected => _connected.vid != 0;
    public (int vid, int pid) ConnectedDevice => _connected;

    public event Action<byte, byte[]>? InputReportReceived;
    public event Action? Disconnected;

    public WebHidTransport()
    {
        // Defer all WPF work to the UI thread. Construction is sync from
        // the caller's perspective; the actual WebView2 init runs in
        // EnsureReadyAsync.
        var app = System.Windows.Application.Current;
        // Guarded — in tests/headless contexts there's no WPF app.
        if (app is null)
        {
            DebugLogger.Log("WebHidTransport: no System.Windows.Application.Current at construction — WebHID will not be available");
            _readyTcs.TrySetResult(false);
            return;
        }
        app.Dispatcher.BeginInvoke(new Action(StartInitialization));
    }

    private void StartInitialization()
    {
        try
        {
            // Hidden host window — positioned off-screen and Topmost=false.
            // We make it visible only during the device-picker flow.
            _window = new Window
            {
                Title = "DrunkDeer Control — keyboard setup",
                Width = 420,
                Height = 320,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ShowInTaskbar = false,
                ShowActivated = false,
                Visibility = Visibility.Hidden,
                Background = System.Windows.Media.Brushes.Black,
            };
            // Set the same icon as the main app so the taskbar (during the
            // brief picker visibility) doesn't look unbranded.
            try
            {
                var iconUri = new Uri("pack://application:,,,/Resources/drunkdeer-control-logo.ico");
                _window.Icon = new System.Windows.Media.Imaging.BitmapImage(iconUri);
            }
            catch { /* best-effort icon */ }

            _webView = new WebView2();
            _window.Content = _webView;

            // Show the window once (hidden) so the HWND is created and the
            // WebView2 control can attach. The window's Visibility=Hidden
            // keeps it off-screen visually.
            _window.Show();

            // Kick off WebView2 init. Don't await here — fire-and-forget
            // since this is on a Dispatcher.BeginInvoke continuation.
            _ = InitializeWebView2Async();
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"WebHidTransport.StartInitialization: failed — {ex.GetType().Name}: {ex.Message}");
            _readyTcs.TrySetResult(false);
        }
    }

    private async Task InitializeWebView2Async()
    {
        try
        {
            // UserDataFolder lives under our existing per-user data dir so
            // WebHID permission grants persist across app updates.
            var appRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DrunkDeer Control");
            var userDataRoot = Path.Combine(appRoot, "WebView2");
            Directory.CreateDirectory(userDataRoot);

            // beta.13: write the bridge HTML to a real folder and serve it
            // via SetVirtualHostNameToFolderMapping at https://drunkdeer.local/.
            //
            // Why this matters: WebHID (navigator.hid) is gated behind
            // window.isSecureContext === true. NavigateToString loads with
            // an opaque about:blank origin, which fails that check, and
            // navigator.hid is undefined. Beta.12's user log showed the
            // bridge IIFE dying on `navigator.hid.addEventListener(...)`
            // before it could even post the 'ready' message.
            //
            // Virtual-host mapping is the documented WebView2 pattern for
            // exposing secure-context APIs to packaged HTML. The .local TLD
            // is reserved (RFC 6762), so we won't collide with anything
            // routable. Resource access kind is DenyCors — Allow would let
            // the page fetch other things from disk under that origin.
            var bridgeFolder = Path.Combine(appRoot, "WebHidBridge");
            Directory.CreateDirectory(bridgeFolder);
            var bridgePath = Path.Combine(bridgeFolder, "index.html");
            File.WriteAllText(bridgePath, WebHidBridgeHtml.Html);

            var options = new CoreWebView2EnvironmentOptions();
            var env = await CoreWebView2Environment.CreateAsync(null, userDataRoot, options);
            await _webView!.EnsureCoreWebView2Async(env);

            _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            _webView.CoreWebView2.PermissionRequested += OnPermissionRequested;
            _webView.CoreWebView2.ProcessFailed += OnProcessFailed;

            // Disable right-click menu, devtools, etc — this is an internal
            // bridge, not a user-facing browser.
            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _webView.CoreWebView2.Settings.IsZoomControlEnabled = false;
            _webView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;

            _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "drunkdeer.local",
                bridgeFolder,
                CoreWebView2HostResourceAccessKind.DenyCors);

            _webView.CoreWebView2.Navigate("https://drunkdeer.local/index.html");
            DebugLogger.Log($"WebHidTransport: WebView2 initialized (userData={userDataRoot}, bridge={bridgePath}) — navigating to https://drunkdeer.local/index.html, waiting for bridge 'ready' message");
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"WebHidTransport.InitializeWebView2Async: failed — {ex.GetType().Name}: {ex.Message}");
            _readyTcs.TrySetResult(false);
        }
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        // WebView2 child process crashed/exited. Without this hook, a renderer
        // crash inside the bridge would look identical to "still loading" —
        // the host would hang in EnsureReadyAsync and time out silently.
        DebugLogger.Log($"WebHidTransport: WebView2 process failed — Kind={e.ProcessFailedKind} Reason={e.Reason} ExitCode={e.ExitCode} ProcessDescription='{e.ProcessDescription}'");
        if (!_isReady)
        {
            // Unblock anyone waiting on EnsureReadyAsync so they fail fast
            // instead of timing out at 5s.
            _readyTcs.TrySetResult(false);
        }
    }

    // Auto-grant HID permission for any device. WebHID's user gesture for
    // requestDevice is still enforced — this just skips an additional
    // "may this page access HID?" prompt that some WebView2 versions show.
    private void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        // Constant for the HID device permission kind. We compare by
        // numeric value to avoid taking a hard dependency on a specific
        // WebView2 enum version that may not expose CoreWebView2PermissionKind.Hid.
        if ((int)e.PermissionKind == 13 /* CoreWebView2PermissionKind.HidDevice */
            || e.PermissionKind.ToString().Contains("Hid", StringComparison.OrdinalIgnoreCase))
        {
            e.State = CoreWebView2PermissionState.Allow;
            e.Handled = true;
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string raw;
        try { raw = e.TryGetWebMessageAsString(); }
        catch { return; }
        if (string.IsNullOrEmpty(raw)) return;

        JsonElement msg;
        try { msg = JsonDocument.Parse(raw).RootElement; }
        catch (Exception ex)
        {
            DebugLogger.Log($"WebHidTransport.OnWebMessageReceived: JSON parse failed — {ex.Message}");
            return;
        }

        if (!msg.TryGetProperty("type", out var typeEl)) return;
        var type = typeEl.GetString();
        switch (type)
        {
            case "ready":
                {
                    bool webhid = msg.TryGetProperty("webhid", out var wh) && wh.ValueKind == JsonValueKind.True;
                    bool secure = msg.TryGetProperty("secure", out var sc) && sc.ValueKind == JsonValueKind.True;
                    _webHidApiAvailable = webhid;
                    // _isReady means "bridge loaded and reachable", not
                    // "WebHID will work" — that's the webhid flag. Code
                    // paths that depend on actual HID access (TryReconnect,
                    // RequestPermission, SendReport) re-check the flag.
                    _isReady = true;
                    _readyTcs.TrySetResult(true);
                    DebugLogger.Log($"WebHidTransport: bridge ready (webhid={webhid}, secureContext={secure})");
                    if (!webhid)
                    {
                        // Most likely cause: WebView2 Runtime missing the HID
                        // feature, or an enterprise policy disabling it.
                        // Either way, the gen-2 detection chain will skip
                        // WebHID instead of hanging on a request that can
                        // never succeed.
                        DebugLogger.Log("WebHidTransport: navigator.hid not exposed in this WebView2 context — gen-2 OEM keyboards cannot be detected via WebHID on this machine.");
                    }
                }
                break;
            case "log":
                {
                    var level = msg.TryGetProperty("level", out var lvlEl) ? lvlEl.GetString() : "log";
                    var text = msg.TryGetProperty("text", out var txtEl) ? txtEl.GetString() : "";
                    DebugLogger.Log($"WebHidTransport: JS {level}: {text}");
                }
                break;
            case "response":
                HandleResponse(msg);
                break;
            case "input":
                HandleInput(msg);
                break;
            case "disconnect":
                _connected = (0, 0);
                try { Disconnected?.Invoke(); } catch { }
                DebugLogger.Log("WebHidTransport: device disconnected");
                break;
            case "pickerResult":
                HandlePickerResult(msg);
                break;
            case "error":
                DebugLogger.Log($"WebHidTransport: JS error — {msg.GetProperty("error").GetString()}");
                break;
        }
    }

    private void HandleResponse(JsonElement msg)
    {
        if (!msg.TryGetProperty("id", out var idEl)) return;
        int id = idEl.GetInt32();
        if (!_pending.TryRemove(id, out var tcs)) return;
        tcs.TrySetResult(msg);
    }

    private void HandleInput(JsonElement msg)
    {
        if (!msg.TryGetProperty("reportId", out var ridEl)) return;
        if (!msg.TryGetProperty("hex", out var hexEl)) return;
        byte reportId = (byte)ridEl.GetInt32();
        var hex = hexEl.GetString() ?? "";
        var bytes = HexToBytes(hex);
        try { InputReportReceived?.Invoke(reportId, bytes); } catch { }
    }

    private void HandlePickerResult(JsonElement msg)
    {
        bool ok = msg.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
        var tcs = Interlocked.Exchange(ref _activePickerTcs, null);
        if (ok && msg.TryGetProperty("deviceInfo", out var infoEl) && infoEl.ValueKind == JsonValueKind.Object)
        {
            int vid = infoEl.GetProperty("vid").GetInt32();
            int pid = infoEl.GetProperty("pid").GetInt32();
            _connected = (vid, pid);
            DebugLogger.Log($"WebHidTransport: picker connected VID=0x{vid:x4} PID=0x{pid:x4}");
        }
        else
        {
            DebugLogger.Log("WebHidTransport: picker dismissed without selection");
        }
        tcs?.TrySetResult(ok);

        // After picker resolution, get the host window out of the user's
        // way WITHOUT setting Visibility=Hidden. Beta.17's user log showed
        // the picker loop: pick → success → identity probe → sendReport
        // returns ok=false → consent fires again. Most likely cause is
        // that hiding the WebView2 window backgrounds the embedded
        // Chromium page, which then releases (or pauses) the open HID
        // device handle. Subsequent sendReport calls find device.opened
        // === false and the bridge throws.
        //
        // Workaround: move the window off-screen instead of hiding it.
        // The WPF Window stays IsVisible=true, the WebView2 control
        // stays active, and Chromium keeps the HID device handle open.
        // Revert Topmost so it doesn't outrank everything; size stays
        // the same so the off-screen offset moves the whole rect out
        // of any monitor's bounds.
        _window?.Dispatcher.BeginInvoke(new Action(async () =>
        {
            await Task.Delay(1500);
            try
            {
                if (_window is not null)
                {
                    _window.Topmost = false;
                    // Park well off the left edge of the leftmost monitor.
                    // (-32000 is the Win32 "minimized window" sentinel; we
                    // use -20000 to avoid colliding with that.)
                    _window.Left = -20000;
                    _window.Top = -20000;
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"WebHidTransport.HandlePickerResult: post-pick offscreen-move failed — {ex.GetType().Name}: {ex.Message}");
            }
        }));
    }

    public async Task EnsureReadyAsync(CancellationToken ct = default)
    {
        using var reg = ct.Register(() => _readyTcs.TrySetCanceled());
        await _readyTcs.Task.ConfigureAwait(false);
        if (!_isReady) throw new InvalidOperationException("WebHidTransport failed to initialize");
    }

    public async Task<bool> TryReconnectAsync(int vid, int pid, CancellationToken ct = default)
    {
        await EnsureReadyAsync(ct).ConfigureAwait(false);
        var resp = await SendCommandAsync(new
        {
            cmd = "reconnect",
            vid,
            pid
        }, ct).ConfigureAwait(false);
        bool ok = resp.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
        if (ok && resp.TryGetProperty("deviceInfo", out var info) && info.ValueKind == JsonValueKind.Object)
        {
            int gotVid = info.GetProperty("vid").GetInt32();
            int gotPid = info.GetProperty("pid").GetInt32();
            _connected = (gotVid, gotPid);
            DebugLogger.Log($"WebHidTransport: reconnected to previously-permitted device VID=0x{gotVid:x4} PID=0x{gotPid:x4}");
        }
        else if (!ok)
        {
            var err = resp.TryGetProperty("error", out var errEl) ? errEl.GetString() : "(no error field)";
            DebugLogger.Log($"WebHidTransport: TryReconnect VID=0x{vid:x4} PID=0x{pid:x4} returned ok=false — JS error: {err}");
        }
        return ok;
    }

    public async Task<bool> RequestPermissionAsync(int vid, CancellationToken ct = default)
    {
        DebugLogger.Log($"WebHidTransport.RequestPermissionAsync: vid=0x{vid:x4} — awaiting bridge ready");
        await EnsureReadyAsync(ct).ConfigureAwait(false);

        // Arm the picker: tell JS that the next click on the "Connect"
        // button should call requestDevice for this VID.
        DebugLogger.Log("WebHidTransport.RequestPermissionAsync: sending armPicker to JS bridge");
        var armResp = await SendCommandAsync(new { cmd = "armPicker", vid }, ct).ConfigureAwait(false);
        if (!(armResp.TryGetProperty("ok", out var armOk) && armOk.GetBoolean()))
        {
            DebugLogger.Log("WebHidTransport.RequestPermissionAsync: armPicker rejected by bridge — aborting");
            return false;
        }

        // Set up a TCS that completes when the user picks (or cancels).
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _activePickerTcs, tcs);

        // Show the window. History (and why this code is paranoid):
        //   beta.12-13: window made visible but JS bridge died before ready
        //   beta.14: persistent Topmost — didn't beat modal consent dialog
        //   beta.15: dropped Topmost on consent dialog — modal still won
        //   beta.16: closed consent dialog on Continue — window STILL didn't
        //            appear visibly to user despite all four diagnostic
        //            lines firing cleanly
        //
        // beta.17: more aggressive show pattern. The previous code set
        // Visibility=Visible on a window that was constructed with
        // Visibility=Hidden and then Show()'d during init. That sequence
        // may leave the window in an inconsistent state on some Windows
        // configurations — visible per WPF's bookkeeping but not actually
        // rendered. Force-show via explicit Show() + Win32 ShowWindow as
        // fallback. Also anchor the position to whatever app window is
        // currently active so users with multi-monitor setups don't have
        // the picker appear on a monitor they're not looking at.
        DebugLogger.Log("WebHidTransport.RequestPermissionAsync: showing window (Topmost=true, persistent, anchored to active window)");
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                if (_window is null) return;

                // Position the window centered over the currently-active
                // app window. CenterScreen positions on the primary monitor,
                // which is the wrong monitor for multi-display users whose
                // app instance lives on a secondary screen. Anchoring to the
                // active window puts the picker host in the user's line of
                // sight.
                var anchor = System.Windows.Application.Current.Windows
                    .OfType<Window>()
                    .Where(w => w != _window && w.IsVisible && w.WindowState != WindowState.Minimized && w.ActualWidth > 0)
                    .OrderByDescending(w => w.IsActive)
                    .FirstOrDefault();
                if (anchor is not null)
                {
                    _window.Left = anchor.Left + (anchor.ActualWidth - _window.Width) / 2;
                    _window.Top = anchor.Top + (anchor.ActualHeight - _window.Height) / 2;
                    DebugLogger.Log($"WebHidTransport.RequestPermissionAsync: anchored to '{anchor.Title}' at ({anchor.Left:F0},{anchor.Top:F0}) size {anchor.ActualWidth:F0}x{anchor.ActualHeight:F0} → picker at ({_window.Left:F0},{_window.Top:F0})");
                }
                else
                {
                    DebugLogger.Log("WebHidTransport.RequestPermissionAsync: no anchor window found, falling back to CenterScreen positioning");
                }

                // Force visible via every available mechanism. WPF
                // Visibility=Visible alone has been observed to fail when
                // the window was constructed with Visibility=Hidden and
                // Show()'d-while-hidden during init.
                _window.Visibility = Visibility.Visible;
                _window.WindowState = WindowState.Normal;
                _window.Topmost = true;
                _window.Show();
                _window.Activate();

                // Win32 fallback: if WPF didn't actually surface the
                // window, push it to the top of the Z-order and steal
                // foreground focus. SetForegroundWindow has restrictions
                // (only works when the calling process owns the foreground
                // OR a user gesture preceded it — clicking Continue counts
                // as a user gesture, so this should succeed here).
                var hwnd = new WindowInteropHelper(_window).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    ShowWindow(hwnd, SW_SHOWNORMAL);
                    SetForegroundWindow(hwnd);
                    BringWindowToTop(hwnd);
                }
                else
                {
                    DebugLogger.Log("WebHidTransport.RequestPermissionAsync: WindowInteropHelper returned IntPtr.Zero — Win32 fallback skipped");
                }

                // Log final state so the next user log tells us whether the
                // window actually rendered and where. If IsVisible=False
                // after all the above, we have a deeper WPF problem; if
                // IsVisible=True but user reports nothing on screen, the
                // logged position will tell us whether it ended up
                // off-screen / on a different monitor.
                DebugLogger.Log($"WebHidTransport.RequestPermissionAsync: post-show state — IsVisible={_window.IsVisible} IsActive={_window.IsActive} Topmost={_window.Topmost} State={_window.WindowState} Pos=({_window.Left:F0},{_window.Top:F0}) Size={_window.ActualWidth:F0}x{_window.ActualHeight:F0} HasHwnd={hwnd != IntPtr.Zero}");
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"WebHidTransport.RequestPermissionAsync: window-show failed — {ex.GetType().Name}: {ex.Message}");
            }
        });

        DebugLogger.Log("WebHidTransport.RequestPermissionAsync: waiting for pickerResult (user must click Show device picker button)");
        using var reg = ct.Register(() => tcs.TrySetCanceled());
        try
        {
            var result = await tcs.Task.ConfigureAwait(false);
            DebugLogger.Log($"WebHidTransport.RequestPermissionAsync: picker resolved ok={result}");
            return result;
        }
        catch (OperationCanceledException)
        {
            DebugLogger.Log("WebHidTransport.RequestPermissionAsync: cancelled — hiding window");
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    if (_window is not null)
                    {
                        _window.Topmost = false;
                        _window.Visibility = Visibility.Hidden;
                    }
                }
                catch (Exception ex)
                {
                    DebugLogger.Log($"WebHidTransport.RequestPermissionAsync: cancel-hide failed — {ex.GetType().Name}: {ex.Message}");
                }
            });
            return false;
        }
    }

    public async Task<bool> SendReportAsync(byte reportId, byte[] payload, CancellationToken ct = default)
    {
        await EnsureReadyAsync(ct).ConfigureAwait(false);
        var resp = await SendCommandAsync(new
        {
            cmd = "send",
            reportId = (int)reportId,
            hex = BytesToHex(payload)
        }, ct).ConfigureAwait(false);
        bool ok = resp.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
        if (!ok)
        {
            var err = resp.TryGetProperty("error", out var errEl) ? errEl.GetString() : "(no error field)";
            DebugLogger.Log($"WebHidTransport: SendReport(reportId=0x{reportId:x2}, {payload.Length} bytes) returned ok=false — JS error: {err}");
        }
        return ok;
    }

    public async Task CloseDeviceAsync(CancellationToken ct = default)
    {
        if (!_isReady) return;
        try { await SendCommandAsync(new { cmd = "close" }, ct).ConfigureAwait(false); }
        catch { /* best-effort */ }
        _connected = (0, 0);
    }

    private async Task<JsonElement> SendCommandAsync(object payload, CancellationToken ct)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WebHidTransport));
        if (_webView is null) throw new InvalidOperationException("WebView2 not initialized");

        int id = Interlocked.Increment(ref _nextRequestId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var withId = MergeWithId(payload, id);
        var json = JsonSerializer.Serialize(withId);

        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            _webView!.CoreWebView2.PostWebMessageAsString(json);
        });

        using var timeoutCts = new CancellationTokenSource(RequestTimeoutMs);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        using var reg = linked.Token.Register(() =>
        {
            if (_pending.TryRemove(id, out var t)) t.TrySetCanceled();
        });

        return await tcs.Task.ConfigureAwait(false);
    }

    private static Dictionary<string, object?> MergeWithId(object payload, int id)
    {
        var dict = new Dictionary<string, object?> { ["id"] = id };
        foreach (var prop in payload.GetType().GetProperties())
        {
            dict[prop.Name] = prop.GetValue(payload);
        }
        return dict;
    }

    private static string BytesToHex(byte[] bytes)
    {
        var sb = new System.Text.StringBuilder(bytes.Length * 2);
        for (int i = 0; i < bytes.Length; i++) sb.Append(bytes[i].ToString("x2"));
        return sb.ToString();
    }

    private static byte[] HexToBytes(string hex)
    {
        if (string.IsNullOrEmpty(hex) || (hex.Length % 2) != 0) return Array.Empty<byte>();
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                try { _webView?.Dispose(); } catch { }
                try { _window?.Close(); } catch { }
            });
        }
        catch { }
    }

    // Win32 fallbacks for forcing the picker host window to the foreground
    // when WPF's Visibility + Topmost + Activate sequence isn't enough on
    // its own. See RequestPermissionAsync.
    private const int SW_SHOWNORMAL = 1;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hWnd);
}
