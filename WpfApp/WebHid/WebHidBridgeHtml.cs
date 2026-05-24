namespace WpfApp.WebHid;

// HTML/JS bridge loaded into the embedded WebView2 control. Kept as a C#
// string constant rather than an embedded resource so the file is
// self-contained and easy to iterate on (no .csproj changes when editing).
//
// Protocol (JSON, both directions over WebView2 postMessage):
//
//   host -> page  {"id": int, "cmd": "reconnect"|"requestDevice"|"send"|"close", ...}
//     reconnect:     {vid, pid}          — silently re-open a previously-permitted device
//     requestDevice: {vid}               — show Chromium picker (must be user-gesture)
//     send:          {reportId, hex}     — sendReport with given Report ID + hex payload
//     close:         {}                  — close the open device
//
//   page -> host  {"type": "ready"|"response"|"input"|"disconnect"|"error", ...}
//     ready:      {}                                            — fired once after script loads
//     response:   {id, ok, deviceInfo?, error?}                 — answer to a host command
//     input:      {reportId, hex}                               — fired on every input report
//     disconnect: {}                                            — fired when device unplugs
//     error:      {error}                                       — out-of-band JS errors
internal static class WebHidBridgeHtml
{
    public const string Html = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<title>DrunkDeer Control — keyboard setup</title>
<style>
  :root {
    color-scheme: dark;
    --bg: #0f1115;
    --surface: #1a1d24;
    --border: #2a2e38;
    --fg: #e8eaee;
    --fg-muted: #9aa1ad;
    --accent: #5fd07a;
    --accent-strong: #79e795;
  }
  html, body { margin: 0; padding: 0; background: var(--bg); color: var(--fg);
    font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", system-ui, sans-serif;
    font-size: 14px; line-height: 1.45; }
  body { display: flex; align-items: center; justify-content: center; min-height: 100vh;
    -webkit-user-select: none; user-select: none; }
  .card { width: 360px; padding: 28px; background: var(--surface);
    border: 1px solid var(--border); border-radius: 12px; text-align: center; }
  h1 { font-size: 18px; font-weight: 600; margin: 0 0 8px; letter-spacing: -0.01em; }
  p { margin: 0 0 20px; color: var(--fg-muted); font-size: 13px; }
  button { appearance: none; border: 0; background: var(--accent); color: #0a0c10;
    font-weight: 600; font-size: 14px; padding: 10px 20px; border-radius: 8px;
    cursor: pointer; font-family: inherit; }
  button:hover { background: var(--accent-strong); }
  button:active { transform: translateY(1px); }
  button:disabled { opacity: 0.5; cursor: not-allowed; }
  .status { margin-top: 16px; font-size: 12px; color: var(--fg-muted); min-height: 16px; }
  .status.ok { color: var(--accent); }
  .status.err { color: #ff6b6b; }
</style>
</head>
<body>
  <div class="card">
    <h1>Connect your keyboard</h1>
    <p>A device picker will appear. Pick your DrunkDeer keyboard and click "Connect".</p>
    <button id="connectBtn" type="button">Show device picker</button>
    <div id="status" class="status"></div>
  </div>
<script>
(function() {
  'use strict';

  let device = null;
  let pendingPickerVid = 0;
  const statusEl = document.getElementById('status');
  const connectBtn = document.getElementById('connectBtn');

  function setStatus(text, kind) {
    statusEl.textContent = text || '';
    statusEl.className = 'status' + (kind ? ' ' + kind : '');
  }

  function bytesToHex(bytes) {
    let s = '';
    for (let i = 0; i < bytes.length; i++) {
      const b = bytes[i];
      s += (b < 16 ? '0' : '') + b.toString(16);
    }
    return s;
  }

  function hexToBytes(hex) {
    const out = new Uint8Array(hex.length / 2);
    for (let i = 0; i < out.length; i++) {
      out[i] = parseInt(hex.substr(i * 2, 2), 16);
    }
    return out;
  }

  function post(obj) {
    try { window.chrome.webview.postMessage(JSON.stringify(obj)); }
    catch (e) { /* webview gone, swallow */ }
  }

  function attachInputListener(d) {
    d.addEventListener('inputreport', function(ev) {
      const bytes = new Uint8Array(ev.data.buffer);
      post({ type: 'input', reportId: ev.reportId, hex: bytesToHex(bytes) });
    });
  }

  function describeDevice(d) {
    return {
      name: d.productName || '',
      vid: d.vendorId,
      pid: d.productId
    };
  }

  async function reconnect(vid, pid) {
    const devices = await navigator.hid.getDevices();
    let d = devices.find(function(x) {
      return x.vendorId === vid && (pid === 0 || x.productId === pid);
    });
    if (!d) return null;
    if (!d.opened) await d.open();
    attachInputListener(d);
    device = d;
    return d;
  }

  async function requestDevice(vid) {
    const picked = await navigator.hid.requestDevice({ filters: [{ vendorId: vid }] });
    if (!picked || picked.length === 0) return null;
    let d = picked[0];
    if (!d.opened) await d.open();
    attachInputListener(d);
    device = d;
    return d;
  }

  async function sendReport(reportId, hex) {
    if (!device) throw new Error('no device open');
    if (!device.opened) throw new Error('device closed');
    await device.sendReport(reportId, hexToBytes(hex));
  }

  async function closeDevice() {
    if (device) {
      try { await device.close(); } catch (e) { /* ignore */ }
      device = null;
    }
  }

  async function handle(msg) {
    const id = msg.id;
    try {
      if (msg.cmd === 'reconnect') {
        const d = await reconnect(msg.vid, msg.pid || 0);
        post({ type: 'response', id: id, ok: !!d, deviceInfo: d ? describeDevice(d) : null });
      } else if (msg.cmd === 'send') {
        await sendReport(msg.reportId, msg.hex);
        post({ type: 'response', id: id, ok: true });
      } else if (msg.cmd === 'close') {
        await closeDevice();
        post({ type: 'response', id: id, ok: true });
      } else if (msg.cmd === 'armPicker') {
        // Host tells us the next click on the Connect button should call
        // requestDevice for this VID. We can't call requestDevice from a
        // host-initiated postMessage because Chromium requires a user
        // gesture — the user must click the button.
        pendingPickerVid = msg.vid;
        setStatus('Click the button to choose your keyboard.');
        connectBtn.disabled = false;
        post({ type: 'response', id: id, ok: true });
      } else {
        post({ type: 'response', id: id, ok: false, error: 'unknown command: ' + msg.cmd });
      }
    } catch (e) {
      post({ type: 'response', id: id, ok: false, error: String(e && e.message ? e.message : e) });
    }
  }

  connectBtn.addEventListener('click', async function() {
    if (!pendingPickerVid) {
      setStatus('Picker not armed by host yet.', 'err');
      return;
    }
    connectBtn.disabled = true;
    setStatus('Waiting for you to pick a device…');
    try {
      const d = await requestDevice(pendingPickerVid);
      if (d) {
        setStatus('Connected to ' + (d.productName || 'keyboard') + '.', 'ok');
        post({ type: 'pickerResult', ok: true, deviceInfo: describeDevice(d) });
      } else {
        setStatus('No device chosen.', 'err');
        connectBtn.disabled = false;
        post({ type: 'pickerResult', ok: false });
      }
    } catch (e) {
      setStatus(String(e && e.message ? e.message : e), 'err');
      connectBtn.disabled = false;
      post({ type: 'pickerResult', ok: false, error: String(e && e.message ? e.message : e) });
    } finally {
      pendingPickerVid = 0;
    }
  });

  window.chrome.webview.addEventListener('message', function(ev) {
    try {
      const msg = JSON.parse(ev.data);
      handle(msg);
    } catch (e) {
      post({ type: 'error', error: 'parse error: ' + e.message });
    }
  });

  navigator.hid.addEventListener('disconnect', function(ev) {
    if (device && ev.device === device) {
      device = null;
      post({ type: 'disconnect' });
    }
  });

  // Disable button until host arms it.
  connectBtn.disabled = true;
  setStatus('');
  post({ type: 'ready' });
})();
</script>
</body>
</html>
""";
}
