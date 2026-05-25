namespace WpfApp.WebHid;

// HTML/JS bridge loaded into the embedded WebView2 control. Kept as a C#
// string constant rather than an embedded resource so the file is
// self-contained and easy to iterate on (no .csproj changes when editing).
//
// Protocol (JSON, both directions over WebView2 postMessage):
//
//   host -> page  {"id": int, "cmd": "reconnect"|"requestDevice"|"send"|"close"|"forget", ...}
//     reconnect:     {vid, pid, usagePage?, usage?}
//                        Silently re-open a previously-permitted device.
//                        When usagePage/usage are supplied, only match a
//                        device whose collections include that pairing —
//                        prevents silently re-bonding to a sibling HID
//                        interface (e.g. the boot-keyboard collection)
//                        that the user picked by mistake previously.
//     armPicker:     {vid, usagePage?, usage?}
//                        Stash filter for the next user-gesture click on
//                        the Connect button. usagePage+usage tighten the
//                        Chromium picker so the wrong HID interface for
//                        the same physical device can't appear.
//     send:          {reportId, hex}     — sendReport with given Report ID + hex payload
//     close:         {}                  — close the open device
//     forget:        {vid, pid?}         — revoke WebHID permission for any
//                                          previously-permitted device matching
//                                          this VID (and PID if supplied). Used
//                                          to evict a bad pick so the next
//                                          picker session starts clean.
//
//   page -> host  {"type": "ready"|"response"|"input"|"disconnect"|"error"|"log", ...}
//     ready:      {webhid: bool, secure: bool}                  — fired once after script loads.
//                                                                  webhid: navigator.hid is exposed.
//                                                                  secure: window.isSecureContext.
//                                                                  beta.13: this is now the FIRST
//                                                                  thing the script does, so the
//                                                                  host always gets a ready signal
//                                                                  even when WebHID is unavailable.
//     response:   {id, ok, deviceInfo?, error?}                 — answer to a host command
//     input:      {reportId, hex}                               — fired on every input report
//     disconnect: {}                                            — fired when device unplugs
//     error:      {error}                                       — out-of-band JS errors
//     log:        {level, text}                                 — console.error / warn forwarded
//                                                                  so the host log captures JS
//                                                                  exceptions in WebView2.
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

  // ─── post() and ready signal must be the first things we do ────────────
  // beta.12 shipped with the ready post AFTER navigator.hid access at the
  // bottom of the IIFE. On user machines where the page loaded as a
  // non-secure context (NavigateToString → about:blank origin), navigator.hid
  // is undefined and accessing it threw TypeError, killing the IIFE before
  // ready was sent. The host then timed out waiting for ready and silently
  // skipped the entire WebHID detection path.
  //
  // beta.13: send ready first with capability flags, then forward all JS
  // errors via postMessage so the host log captures them. Guard every
  // navigator.hid touch so any single missing API doesn't kill the bridge.

  function post(obj) {
    try { window.chrome.webview.postMessage(JSON.stringify(obj)); }
    catch (e) { /* webview gone, swallow */ }
  }

  const webhidAvailable = typeof navigator !== 'undefined'
    && typeof navigator.hid !== 'undefined'
    && typeof navigator.hid.requestDevice === 'function';
  const isSecure = typeof window !== 'undefined' && window.isSecureContext === true;

  post({ type: 'ready', webhid: webhidAvailable, secure: isSecure });

  // Forward JS exceptions to host. The host log is otherwise a black hole
  // for anything that happens inside WebView2.
  window.addEventListener('error', function(ev) {
    post({ type: 'log', level: 'error', text: 'window.onerror: ' + (ev.message || '') +
      ' @ ' + (ev.filename || '') + ':' + (ev.lineno || 0) });
  });
  window.addEventListener('unhandledrejection', function(ev) {
    let txt = '';
    try { txt = String(ev.reason && ev.reason.message ? ev.reason.message : ev.reason); } catch (_) { txt = '<unprintable>'; }
    post({ type: 'log', level: 'error', text: 'unhandledrejection: ' + txt });
  });
  ['error', 'warn'].forEach(function(level) {
    const orig = console[level];
    console[level] = function() {
      try {
        const parts = [];
        for (let i = 0; i < arguments.length; i++) {
          const a = arguments[i];
          parts.push(a && a.message ? a.message : String(a));
        }
        post({ type: 'log', level: level, text: parts.join(' ') });
      } catch (_) { /* never throw from console hook */ }
      try { orig.apply(console, arguments); } catch (_) {}
    };
  });

  if (!webhidAvailable) {
    // Nothing else we can do — host knows from the ready flag. Leave the
    // page sitting on the UI so a human inspecting the WebView2 window
    // sees an explanation rather than a blank dialog.
    try {
      const banner = document.createElement('div');
      banner.className = 'status err';
      banner.textContent = 'WebHID API is not available in this WebView2 context.';
      const card = document.querySelector('.card');
      if (card) card.appendChild(banner);
      const btn = document.getElementById('connectBtn');
      if (btn) btn.disabled = true;
    } catch (_) {}
    return;
  }

  // ─── normal bridge from here on; navigator.hid is known-good ───────────

  let device = null;
  let pendingPickerVid = 0;
  let pendingPickerUsagePage = -1; // -1 = no constraint
  let pendingPickerUsage = -1;
  const statusEl = document.getElementById('status');
  const connectBtn = document.getElementById('connectBtn');

  // beta.23: collection match helper. The OEM A75 Pro (VID 0x19F5) exposes
  // two HID interfaces under one physical device — the boot keyboard (mi_00,
  // usagePage=1, usage=6) and the 64-byte vendor data interface (mi_01,
  // usagePage=1, usage=0). Chrome WebHID enumerates each as a separate
  // picker entry. Beta.22's user (Discord) consistently picked the boot
  // keyboard entry; sendReport then failed with "Failed to write the report"
  // because WebHID strips reports from protected (keyboard) collections.
  // The loop: pick → reconnect succeeds silently → send fails → consent
  // re-fires → user picks the same wrong entry. Forever.
  function deviceMatchesUsage(d, usagePage, usage) {
    if (usagePage < 0) return true; // no constraint
    try {
      for (const c of d.collections || []) {
        if (c.usagePage !== usagePage) continue;
        if (usage < 0 || c.usage === usage) return true;
      }
    } catch (e) { /* fall through to false */ }
    return false;
  }

  function setStatus(text, kind) {
    if (!statusEl) return;
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

  function attachInputListener(d) {
    d.addEventListener('inputreport', function(ev) {
      const bytes = new Uint8Array(ev.data.buffer);
      // beta.18: log every input report so we can see in the host log
      // whether the firmware is responding to our writes at all.
      post({ type: 'log', level: 'warn', text: 'inputreport reportId=' + ev.reportId + ' bytes=' + bytes.length });
      post({ type: 'input', reportId: ev.reportId, hex: bytesToHex(bytes) });
    });
  }

  // beta.19: enumerate the declared output report IDs for this device.
  // Beta.18 user log showed sendReport(0x04, ...) failing with "Failed to
  // write the report" — Chromium rejects sendReport for a reportId the
  // device's descriptor doesn't declare. The OEM 0x19F5/0xFB5C variant's
  // mi_01 descriptor declares ONE output report with NO Report ID. So
  // we must call sendReport(0, ...) with the protocol's reportId byte
  // packed as the first data byte instead.
  function getDeclaredOutputReportIds(d) {
    const ids = new Set();
    try {
      for (const c of d.collections || []) {
        for (const r of c.outputReports || []) {
          ids.add(r.reportId);
        }
      }
    } catch (e) {
      post({ type: 'log', level: 'warn', text: 'getDeclaredOutputReportIds error: ' + (e && e.message ? e.message : e) });
    }
    return Array.from(ids);
  }

  function logDeviceTopology(d) {
    try {
      const cols = (d.collections || []).map(function(c) {
        return {
          usagePage: c.usagePage,
          usage: c.usage,
          in: (c.inputReports || []).map(function(r) { return r.reportId; }),
          out: (c.outputReports || []).map(function(r) { return r.reportId; }),
          feat: (c.featureReports || []).map(function(r) { return r.reportId; }),
        };
      });
      post({ type: 'log', level: 'warn', text: 'device topology: vid=0x' + d.vendorId.toString(16) + ' pid=0x' + d.productId.toString(16) + ' opened=' + d.opened + ' collections=' + JSON.stringify(cols) });
    } catch (e) {
      post({ type: 'log', level: 'warn', text: 'logDeviceTopology error: ' + (e && e.message ? e.message : e) });
    }
  }

  function describeDevice(d) {
    return {
      name: d.productName || '',
      vid: d.vendorId,
      pid: d.productId
    };
  }

  async function reconnect(vid, pid, usagePage, usage) {
    const devices = await navigator.hid.getDevices();
    const matches = devices.filter(function(x) {
      if (x.vendorId !== vid) return false;
      if (pid !== 0 && x.productId !== pid) return false;
      return deviceMatchesUsage(x, usagePage, usage);
    });
    if (matches.length === 0) {
      // No collection-matched candidate. Log how many vid/pid-matching
      // devices were skipped so the host log explains why reconnect
      // returned null even though Chromium has a persisted permission.
      const vidPidOnly = devices.filter(function(x) {
        return x.vendorId === vid && (pid === 0 || x.productId === pid);
      });
      if (vidPidOnly.length > 0 && usagePage >= 0) {
        post({ type: 'log', level: 'warn', text: 'reconnect: ' + vidPidOnly.length + ' permitted device(s) match vid/pid but none has a collection with usagePage=' + usagePage + (usage >= 0 ? ' usage=' + usage : '') + ' — likely a leftover bad pick from a previous session' });
      }
      return null;
    }
    const d = matches[0];
    if (!d.opened) await d.open();
    attachInputListener(d);
    logDeviceTopology(d);
    device = d;
    return d;
  }

  async function requestDevice(vid, usagePage, usage) {
    const filter = { vendorId: vid };
    if (usagePage >= 0) filter.usagePage = usagePage;
    if (usage >= 0) filter.usage = usage;
    const picked = await navigator.hid.requestDevice({ filters: [filter] });
    if (!picked || picked.length === 0) return null;
    let d = picked[0];
    if (!d.opened) await d.open();
    attachInputListener(d);
    logDeviceTopology(d);
    device = d;
    return d;
  }

  // beta.23: revoke WebHID permission for previously-picked devices. Called
  // when reconnect succeeded against the wrong device (one whose declared
  // output reports are empty, meaning a protected/boot-keyboard collection).
  // Without this, Chromium would keep returning the bad device on every
  // future reconnect attempt and the user would have to manually clear the
  // WebView2 user-data folder to recover.
  async function forget(vid, pid) {
    if (typeof navigator.hid.getDevices !== 'function') return 0;
    const devices = await navigator.hid.getDevices();
    let count = 0;
    for (const d of devices) {
      if (d.vendorId !== vid) continue;
      if (pid !== 0 && d.productId !== pid) continue;
      try {
        if (typeof d.forget === 'function') {
          await d.forget();
          count++;
        }
      } catch (e) {
        post({ type: 'log', level: 'warn', text: 'forget: device.forget() threw for vid=0x' + vid.toString(16) + ' pid=0x' + d.productId.toString(16) + ': ' + (e && e.message ? e.message : e) });
      }
    }
    return count;
  }

  async function sendReport(reportId, hex) {
    if (!device) throw new Error('no device open');
    if (!device.opened) {
      post({ type: 'log', level: 'warn', text: 'sendReport: device.opened=false on entry, attempting re-open' });
      try {
        await device.open();
        post({ type: 'log', level: 'warn', text: 'sendReport: re-open succeeded, device.opened=' + device.opened });
      } catch (e) {
        throw new Error('device closed and re-open failed: ' + (e && e.message ? e.message : e));
      }
      if (!device.opened) throw new Error('device closed and re-open did not stick');
    }

    const dataBytes = hexToBytes(hex);
    const declaredIds = getDeclaredOutputReportIds(device);

    // beta.20: if the device's HID descriptor doesn't declare the requested
    // Report ID, drop the Report ID concept entirely and send the payload
    // bytes as-is via sendReport(0, dataBytes).
    //
    // Beta.19 tried prepending the requested Report ID as the first data
    // byte — that produced 65-byte payloads on a 64-byte descriptor and
    // Chromium rejected with the same "Failed to write the report" error.
    //
    // For a device that declares one output report with reportId=0 and
    // size N, the wire format is just N bytes of payload — no Report ID
    // prefix in the wire at all. The 0x04 "Report ID prefix" from the
    // gen-1 protocol docs is a HID-layer convention added by the OS for
    // devices that DO declare Report IDs; on a no-ID descriptor the
    // firmware reads the bytes raw and the protocol opcode (0xA0 for
    // identity) is the first byte.
    if (reportId !== 0 && declaredIds.length > 0 && declaredIds.indexOf(reportId) === -1) {
      post({ type: 'log', level: 'warn', text: 'sendReport: requested reportId=' + reportId + ' not in declared ' + JSON.stringify(declaredIds) + ', falling back to sendReport(0, data) (' + dataBytes.length + ' bytes, no prefix)' });
      await device.sendReport(0, dataBytes);
      return;
    }

    // Either the descriptor declares this Report ID, or the device has no
    // declared output reports (in which case Chromium accepts any sendReport
    // and routes by report-id-byte convention). Use the requested reportId
    // directly.
    await device.sendReport(reportId, dataBytes);
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
        const up = (typeof msg.usagePage === 'number') ? msg.usagePage : -1;
        const u  = (typeof msg.usage     === 'number') ? msg.usage     : -1;
        const d = await reconnect(msg.vid, msg.pid || 0, up, u);
        post({ type: 'response', id: id, ok: !!d, deviceInfo: d ? describeDevice(d) : null });
      } else if (msg.cmd === 'send') {
        await sendReport(msg.reportId, msg.hex);
        post({ type: 'response', id: id, ok: true });
      } else if (msg.cmd === 'close') {
        await closeDevice();
        post({ type: 'response', id: id, ok: true });
      } else if (msg.cmd === 'forget') {
        const count = await forget(msg.vid, msg.pid || 0);
        post({ type: 'response', id: id, ok: true, forgotten: count });
      } else if (msg.cmd === 'armPicker') {
        // Host tells us the next click on the Connect button should call
        // requestDevice for this VID. We can't call requestDevice from a
        // host-initiated postMessage because Chromium requires a user
        // gesture — the user must click the button.
        pendingPickerVid = msg.vid;
        pendingPickerUsagePage = (typeof msg.usagePage === 'number') ? msg.usagePage : -1;
        pendingPickerUsage     = (typeof msg.usage     === 'number') ? msg.usage     : -1;
        setStatus('Click the button to choose your keyboard.');
        if (connectBtn) connectBtn.disabled = false;
        post({ type: 'response', id: id, ok: true });
      } else {
        post({ type: 'response', id: id, ok: false, error: 'unknown command: ' + msg.cmd });
      }
    } catch (e) {
      post({ type: 'response', id: id, ok: false, error: String(e && e.message ? e.message : e) });
    }
  }

  if (connectBtn) {
    connectBtn.addEventListener('click', async function() {
      if (!pendingPickerVid) {
        setStatus('Picker not armed by host yet.', 'err');
        return;
      }
      connectBtn.disabled = true;
      setStatus('Waiting for you to pick a device…');
      try {
        const d = await requestDevice(pendingPickerVid, pendingPickerUsagePage, pendingPickerUsage);
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
        pendingPickerUsagePage = -1;
        pendingPickerUsage = -1;
      }
    });
  }

  window.chrome.webview.addEventListener('message', function(ev) {
    try {
      const msg = JSON.parse(ev.data);
      handle(msg);
    } catch (e) {
      post({ type: 'error', error: 'parse error: ' + e.message });
    }
  });

  try {
    navigator.hid.addEventListener('disconnect', function(ev) {
      if (device && ev.device === device) {
        device = null;
        post({ type: 'disconnect' });
      }
    });
  } catch (e) {
    post({ type: 'log', level: 'warn', text: 'navigator.hid disconnect listener failed: ' + String(e && e.message ? e.message : e) });
  }

  // Disable button until host arms it.
  if (connectBtn) connectBtn.disabled = true;
  setStatus('');
})();
</script>
</body>
</html>
""";
}
