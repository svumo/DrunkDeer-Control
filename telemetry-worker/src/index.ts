// DrunkDeer Control firmware-version Worker.
//
// One-way server → client channel. The WPF app polls GET /firmware on
// launch to surface a banner when the connected keyboard's firmware
// version is older than what DrunkDeer publishes. No user data flows
// the other direction — see the README in this folder for the why.
//
// A daily cron scrapes drunkdeer.com/pages/downloads, fetches the latest
// DrunkdeerUpdaterV*.zip, parses config/config.ini in memory, and writes
// per-PID firmware versions to KV under `fw:0x????` keys.

import { unzipSync } from "fflate";

interface Env {
  KV: KVNamespace;
}

export default {
  async fetch(req: Request, env: Env): Promise<Response> {
    try {
      const url = new URL(req.url);
      if (req.method === "OPTIONS") return new Response(null, { status: 204, headers: corsHeaders() });
      if (req.method === "GET" && url.pathname === "/firmware") return await handleFirmware(env);
      if (req.method === "GET" && url.pathname === "/") return new Response("DrunkDeer Control firmware channel — see github.com/svumo/DrunkDeer-Control", { status: 200 });
      return new Response("not found", { status: 404 });
    } catch (e) {
      console.error("worker exception:", e);
      return new Response("internal error", { status: 500 });
    }
  },

  // Daily cron — see wrangler.toml [triggers]. Scrapes the DrunkDeer
  // downloads page, fetches the latest updater zip, parses config.ini,
  // and writes per-PID firmware versions to KV. Failure is swallowed so
  // the next day's run can retry; the existing data sticks around because
  // we don't TTL the fw:* keys.
  async scheduled(_event: ScheduledEvent, env: Env, _ctx: ExecutionContext): Promise<void> {
    try {
      const url = await findLatestBundleUrl();
      if (!url) {
        console.log("scheduled: no bundle URL found on downloads page");
        return;
      }
      const zipBuf = await fetch(url, {
        headers: { "User-Agent": "drunkdeer-firmware-worker/1.0" },
      }).then(r => r.arrayBuffer());
      const unzipped = unzipSync(new Uint8Array(zipBuf));
      // Tolerate Windows-style backslashes and either casing — config writers
      // are inconsistent. First match wins.
      const iniKey = Object.keys(unzipped).find(k =>
        k.toLowerCase().replace(/\\/g, "/").endsWith("config/config.ini"));
      if (!iniKey) {
        console.error("scheduled: config/config.ini not found inside zip; entries:", Object.keys(unzipped));
        return;
      }
      const ini = new TextDecoder().decode(unzipped[iniKey]);
      const versions = parseConfigIni(ini);
      const checkedAt = new Date().toISOString();
      await Promise.all([
        env.KV.put("fw:bundle_url", url),
        env.KV.put("fw:bundle_checked_at", checkedAt),
        ...Object.entries(versions).map(([pid, ver]) => env.KV.put(`fw:${pid}`, ver)),
      ]);
      console.log(`scheduled: wrote ${Object.keys(versions).length} fw entries from ${url}`);
    } catch (e) {
      console.error("scheduled cron failed:", e);
    }
  },
};

// Scrape drunkdeer.com/pages/downloads for the latest DrunkdeerUpdaterV*.zip
// URL. The Shopify CDN paths embed the version in the filename, and the page
// always links the newest one — but it ALSO links older versions, so we sort
// the matches and pick the highest version number.
async function findLatestBundleUrl(): Promise<string | null> {
  const res = await fetch("https://drunkdeer.com/pages/downloads", {
    headers: { "User-Agent": "drunkdeer-firmware-worker/1.0" },
  });
  if (!res.ok) {
    console.error(`findLatestBundleUrl: downloads page returned ${res.status}`);
    return null;
  }
  const html = await res.text();
  const re = /https?:\/\/cdn\.shopify\.com\/[^"'<>\s]*DrunkdeerUpdaterV[\d.]+\.zip[^"'<>\s]*/g;
  const matches = html.match(re);
  if (!matches?.length) return null;
  // De-dupe and pick highest version. Sort by extracted vX.Y.Z numeric tuple
  // — lexicographic on the raw URL is unreliable because the `?v=<epoch>`
  // query string varies independently from the version.
  const uniq = Array.from(new Set(matches));
  uniq.sort((a, b) => compareVersionsInUrl(a, b));
  return uniq[uniq.length - 1];
}

function compareVersionsInUrl(a: string, b: string): number {
  const va = extractVersionTuple(a);
  const vb = extractVersionTuple(b);
  for (let i = 0; i < Math.max(va.length, vb.length); i++) {
    const da = va[i] ?? 0;
    const db = vb[i] ?? 0;
    if (da !== db) return da - db;
  }
  return 0;
}

function extractVersionTuple(url: string): number[] {
  const m = url.match(/DrunkdeerUpdaterV(\d+(?:\.\d+)*)/);
  if (!m) return [0];
  return m[1].split(".").map(n => Number(n) || 0);
}

// INI parser tuned for DrunkDeer's config.ini shape: top-level [DEVICEn]
// sections containing `VidAndPid=0x352d,0x2383` and `Version=0x0008` lines.
// Tolerates `;` comments, BOM, CRLF, and whitespace around `=`. Returns
// pid (lowercased hex like "0x2383") → version (e.g. "0x0008").
export function parseConfigIni(text: string): Record<string, string> {
  const out: Record<string, string> = {};
  let curPid: string | null = null;
  let curVer: string | null = null;
  const flush = () => {
    if (curPid && curVer) out[curPid] = curVer;
    curPid = null;
    curVer = null;
  };
  for (const rawLine of text.split(/\r?\n/)) {
    // Strip BOM (only matters on the first line) and trim.
    const line = rawLine.replace(/^﻿/, "").trim();
    if (!line) continue;
    if (line.startsWith(";") || line.startsWith("#")) continue;
    if (line.startsWith("[")) {
      flush();
      continue;
    }
    const eq = line.indexOf("=");
    if (eq < 0) continue;
    const key = line.slice(0, eq).trim().toLowerCase();
    const value = line.slice(eq + 1).trim();
    if (key === "vidandpid") {
      // "0x352d,0x2383" — second half is the PID. Normalise to lowercase hex.
      const parts = value.split(",").map(p => p.trim());
      if (parts.length >= 2) {
        const pid = parts[1].toLowerCase();
        if (/^0x[0-9a-f]+$/.test(pid)) curPid = pid;
      }
    } else if (key === "version") {
      const v = value.toLowerCase();
      if (/^0x[0-9a-f]+$/.test(v)) curVer = v;
    }
  }
  flush();
  return out;
}

interface FirmwareResponse {
  versions: Record<string, string>;
  bundle: string | null;
  checked: string | null;
}

async function handleFirmware(env: Env): Promise<Response> {
  const fwKeys = await listKeys(env, "fw:");
  const versions: Record<string, string> = {};
  let bundle: string | null = null;
  let checked: string | null = null;
  // Pull all fw:* values in parallel. The metadata keys (bundle_url,
  // bundle_checked_at) live in the same prefix so we filter them out of
  // the per-PID map.
  const values = await Promise.all(fwKeys.map(k => env.KV.get(k)));
  for (let i = 0; i < fwKeys.length; i++) {
    const key = fwKeys[i];
    const val = values[i];
    if (val === null) continue;
    if (key === "fw:bundle_url") { bundle = val; continue; }
    if (key === "fw:bundle_checked_at") { checked = val; continue; }
    if (key.startsWith("fw:0x")) {
      versions[key.slice(3)] = val;
    }
  }
  const body: FirmwareResponse = { versions, bundle, checked };
  return Response.json(body, {
    // 1h browser/edge cache is plenty — cron only runs once a day. Keeps
    // the WPF client from hammering us if a user opens the app repeatedly.
    headers: { "cache-control": "public, max-age=3600" },
  });
}

function corsHeaders(): Record<string, string> {
  return {
    "access-control-allow-origin": "*",
    "access-control-allow-headers": "content-type",
    "access-control-allow-methods": "GET, OPTIONS",
  };
}

async function listKeys(env: Env, prefix: string): Promise<string[]> {
  const keys: string[] = [];
  let cursor: string | undefined;
  do {
    const list = await env.KV.list({ prefix, cursor });
    keys.push(...list.keys.map(k => k.name));
    cursor = list.list_complete ? undefined : list.cursor;
  } while (cursor);
  return keys;
}
