// DrunkDeer Control telemetry Worker.
//
// Receives daily heartbeats from the WPF client (UsageReporter.cs) and
// deduplicates them by hashed device ID so we can compute DAU/MAU/install
// counts. The full client-side payload schema and privacy guardrails are
// documented in WpfApp/UsageReporter.cs in the main repo.
//
// What this Worker DOES:
//   - Accepts POST /ping with a small JSON payload, validates shape, and
//     bumps daily/monthly counters in KV iff the device hasn't already
//     pinged today.
//   - Exposes GET /stats behind a bearer token to return aggregates.
//
// What this Worker EXPLICITLY does NOT do:
//   - Read cf-connecting-ip or any IP-bearing header. The maintainer never
//     sees user IPs.
//   - Store the raw payload. Only counter increments and a per-device
//     "seen today" key (auto-expires in ~36h) are written.
//   - Cross-correlate fields. A device's keyboard PID and OS are bumped as
//     independent counters per month — there's no way to recover the
//     combination "user X had keyboard Y on OS Z".
//
// Firmware-version channel (added later — see GET /firmware + scheduled
// handler below). Daily cron scrapes drunkdeer.com/pages/downloads, fetches
// the latest DrunkdeerUpdaterV*.zip, parses config/config.ini in-memory,
// and writes per-PID versions to KV under `fw:0x????` keys. The endpoint
// is public, no auth — the client just reads it on launch to decide
// whether to nag the user about a firmware update.

import { unzipSync } from "fflate";

interface Env {
  KV: KVNamespace;
  STATS_TOKEN: string;
}

interface PingBody {
  id?: string;        // hashed device ID, 16 lowercase hex chars
  app?: string;       // e.g. "1.5.0"
  os?: string;        // e.g. "Microsoft Windows NT 10.0.26200.0"
  kb_pid?: string;    // e.g. "0x2383"
  kb_fw?: string;     // e.g. "0.09"
  ts?: number;        // unix seconds
}

interface StatsResponse {
  generated_at: string;
  dau_last_30d: { date: string; count: number }[];
  mau_current: number;
  by_kb_pid: Record<string, number>;
  by_app: Record<string, number>;
  by_os: Record<string, number>;
  // Health/abuse signals. Lets the maintainer see if the worker is being
  // hammered, getting bad payloads, or quietly 5xx'ing without anyone noticing.
  total_pings_last_30d: { date: string; count: number }[];
  errors_last_30d: { date: string; reason: string; count: number }[];
}

type ErrorReason =
  | "too_large"
  | "bad_content_type"
  | "invalid_json"
  | "bad_id"
  | "exception"
  | "not_found";

const MAX_BODY_BYTES = 1024;
const DEVICE_ID_RE = /^[0-9a-f]{16}$/;
const KB_PID_RE = /^0x[0-9a-f]{4}$/;
const APP_VERSION_RE = /^\d{1,3}(\.\d{1,3}){1,3}$/;
const SEEN_TTL_SECONDS = 60 * 60 * 36;        // 36h, longer than a calendar day in any zone
const MAU_TTL_SECONDS = 60 * 60 * 24 * 35;    // 35d so a "monthly unique" survives the next-month rollover
const HEALTH_TTL_SECONDS = 60 * 60 * 24 * 35; // total_pings + errors expire after 35d; nobody cares about ancient noise

// Records the first date `total_pings:<date>` was tracked, so /stats can
// distinguish "no pings that day" from "feature wasn't deployed yet" (which
// otherwise both look like count=0). Set lazily on the first valid ping
// after deploy; persisted forever (no TTL).
const TOTAL_PINGS_SINCE_KEY = "meta:total_pings_since";

// Module-scope cache so we don't read KV on every ping just to check whether
// the marker has already been written. Workers reuse this between requests
// within the same isolate; cold starts re-read once.
let totalPingsSinceCache: string | null = null;

export default {
  async fetch(req: Request, env: Env): Promise<Response> {
    // Top-level catch so any unexpected throw becomes a counted "exception"
    // instead of a silent 500. The console.error feeds CF's live tail in case
    // a stack trace is useful — observability is otherwise off in wrangler.toml.
    try {
      const url = new URL(req.url);
      if (req.method === "POST" && url.pathname === "/ping") return await handlePing(req, env);
      if (req.method === "GET" && url.pathname === "/stats") return await handleStats(req, env);
      if (req.method === "GET" && url.pathname === "/firmware") return await handleFirmware(env);
      if (req.method === "GET" && url.pathname === "/") return new Response("DrunkDeer Control telemetry — see github.com/svumo/DrunkDeer-Control", { status: 200 });
      await recordError(env, "not_found");
      return new Response("not found", { status: 404 });
    } catch (e) {
      console.error("worker exception:", e);
      await recordError(env, "exception").catch(() => { /* don't loop */ });
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
        headers: { "User-Agent": "drunkdeer-telemetry-worker/1.0" },
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
// the matches and pick the lexicographically last one (V2.3.1 > V2.2.0).
async function findLatestBundleUrl(): Promise<string | null> {
  const res = await fetch("https://drunkdeer.com/pages/downloads", {
    headers: { "User-Agent": "drunkdeer-telemetry-worker/1.0" },
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

async function handlePing(req: Request, env: Env): Promise<Response> {
  const today = new Date().toISOString().slice(0, 10);   // 2026-05-07
  const month = today.slice(0, 7);                        // 2026-05

  // Reject anything bigger than 1KB outright — our real payload is ~150 bytes.
  const lenStr = req.headers.get("content-length");
  if (lenStr && Number(lenStr) > MAX_BODY_BYTES) {
    await recordError(env, "too_large");
    return new Response("payload too large", { status: 413 });
  }

  const ct = req.headers.get("content-type") ?? "";
  if (!ct.includes("application/json")) {
    await recordError(env, "bad_content_type");
    return new Response("expected application/json", { status: 415 });
  }

  let body: PingBody;
  try {
    body = await req.json<PingBody>();
  } catch {
    await recordError(env, "invalid_json");
    return new Response("invalid json", { status: 400 });
  }

  if (!body.id || !DEVICE_ID_RE.test(body.id)) {
    await recordError(env, "bad_id");
    return new Response("bad id", { status: 400 });
  }

  // Past this point the ping is valid — count it in total even if dedup
  // makes us skip the unique-counters bump.
  await bumpCounter(env, `total_pings:${today}`, HEALTH_TTL_SECONDS);
  await ensureTotalPingsSince(env, today);

  const seenKey = `seen:${today}:${body.id}`;

  // Dedup: if we've already counted this device today, return 204 without
  // bumping anything. KV's eventual consistency is fine here — duplicate
  // bumps on the same day are rare and harmless (off-by-one in DAU).
  const already = await env.KV.get(seenKey);
  if (already) return new Response(null, { status: 204 });

  await env.KV.put(seenKey, "1", { expirationTtl: SEEN_TTL_SECONDS });

  await Promise.all([
    bumpCounter(env, `dau:${today}`),
    bumpCounter(env, `mau:${month}:${body.id}`, MAU_TTL_SECONDS),
    body.kb_pid && (KB_PID_RE.test(body.kb_pid) || body.kb_pid === "none") ? bumpCounter(env, `kb_pid:${month}:${body.kb_pid}`) : Promise.resolve(),
    body.app && APP_VERSION_RE.test(body.app) ? bumpCounter(env, `app:${month}:${body.app}`) : Promise.resolve(),
    body.os ? bumpCounter(env, `os:${month}:${truncateOs(body.os)}`) : Promise.resolve(),
  ]);

  return new Response(null, { status: 204 });
}

// Lazily set the "tracking started on" marker for total_pings. One KV read
// per cold isolate (cached after) and a single write the first time the
// worker is ever invoked after deploy. The marker has no TTL.
async function ensureTotalPingsSince(env: Env, today: string): Promise<void> {
  if (totalPingsSinceCache) return;
  const existing = await env.KV.get(TOTAL_PINGS_SINCE_KEY);
  if (existing) {
    totalPingsSinceCache = existing;
    return;
  }
  await env.KV.put(TOTAL_PINGS_SINCE_KEY, today);
  totalPingsSinceCache = today;
}

// Daily counter, one per reason. Bounded by ErrorReason union so we can never
// accidentally explode the keyspace via attacker-controlled strings.
async function recordError(env: Env, reason: ErrorReason): Promise<void> {
  const today = new Date().toISOString().slice(0, 10);
  await bumpCounter(env, `error:${today}:${reason}`, HEALTH_TTL_SECONDS);
}

async function handleStats(req: Request, env: Env): Promise<Response> {
  const auth = req.headers.get("authorization");
  if (!auth || auth !== `Bearer ${env.STATS_TOKEN}`) {
    return new Response("unauthorized", { status: 401 });
  }

  const today = new Date().toISOString().slice(0, 10);
  const month = today.slice(0, 7);

  const last30Dates: string[] = [];
  for (let i = 0; i < 30; i++) {
    last30Dates.push(new Date(Date.now() - i * 86400000).toISOString().slice(0, 10));
  }

  const dauKeys = last30Dates.map(d => `dau:${d}`);
  const totalKeys = last30Dates.map(d => `total_pings:${d}`);

  const [dauCounts, totalCounts, mauKeys, kbKeys, appKeys, osKeys, errorKeys, totalPingsSince] = await Promise.all([
    Promise.all(dauKeys.map(k => readCounter(env, k))),
    Promise.all(totalKeys.map(k => readCounter(env, k))),
    listKeys(env, `mau:${month}:`),
    listKeys(env, `kb_pid:${month}:`),
    listKeys(env, `app:${month}:`),
    listKeys(env, `os:${month}:`),
    listKeys(env, `error:`),
    env.KV.get(TOTAL_PINGS_SINCE_KEY),
  ]);

  const [mauValues, kbValues, appValues, osValues, errorValues] = await Promise.all([
    Promise.all(mauKeys.map(k => readCounter(env, k))),
    Promise.all(kbKeys.map(k => readCounter(env, k))),
    Promise.all(appKeys.map(k => readCounter(env, k))),
    Promise.all(osKeys.map(k => readCounter(env, k))),
    Promise.all(errorKeys.map(k => readCounter(env, k))),
  ]);

  const dau_last_30d = last30Dates.map((d, i) => ({ date: d, count: dauCounts[i] }))
    .sort((a, b) => a.date.localeCompare(b.date));
  // Drop days before total_pings was first tracked — otherwise they show as
  // count=0, which is indistinguishable from "feature was deployed but nobody
  // pinged that day". If the marker is missing (no pings yet ever), keep all
  // days so the response shape stays a 30-entry array of zeros.
  const total_pings_last_30d = last30Dates.map((d, i) => ({ date: d, count: totalCounts[i] }))
    .filter(e => !totalPingsSince || e.date >= totalPingsSince)
    .sort((a, b) => a.date.localeCompare(b.date));

  // error:YYYY-MM-DD:reason → { date, reason, count }, only include entries in the last 30 days.
  const last30Set = new Set(last30Dates);
  const errors_last_30d = errorKeys
    .map((k, i) => {
      const parts = k.split(":");
      // ["error", "YYYY-MM-DD", "reason"]
      return { date: parts[1], reason: parts.slice(2).join(":"), count: errorValues[i] };
    })
    .filter(e => last30Set.has(e.date) && e.count > 0)
    .sort((a, b) => a.date.localeCompare(b.date) || a.reason.localeCompare(b.reason));

  const stats: StatsResponse = {
    generated_at: new Date().toISOString(),
    dau_last_30d,
    mau_current: mauValues.filter(v => v > 0).length,
    by_kb_pid: groupByLastSegment(kbKeys, kbValues),
    by_app: groupByLastSegment(appKeys, appValues),
    by_os: groupByLastSegment(osKeys, osValues),
    total_pings_last_30d,
    errors_last_30d,
  };

  return Response.json(stats, {
    headers: { "cache-control": "no-store" },
  });
}

// KV doesn't have atomic increments. Read-modify-write is fine for a population
// in the thousands pinging once per day — collision rate is negligible. If we
// ever scale past that we can switch to Durable Objects.
async function bumpCounter(env: Env, key: string, ttl?: number): Promise<void> {
  const current = await env.KV.get(key);
  const next = (current ? Number(current) : 0) + 1;
  await env.KV.put(key, String(next), ttl ? { expirationTtl: ttl } : undefined);
}

async function readCounter(env: Env, key: string): Promise<number> {
  const v = await env.KV.get(key);
  return v ? Number(v) : 0;
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

function groupByLastSegment(keys: string[], values: number[]): Record<string, number> {
  const out: Record<string, number> = {};
  for (let i = 0; i < keys.length; i++) {
    const segment = keys[i].split(":").pop() ?? keys[i];
    out[segment] = (out[segment] ?? 0) + values[i];
  }
  return out;
}

function truncateOs(raw: string): string {
  // "Microsoft Windows NT 10.0.26200.0" → "Win10.0.26200"
  const m = raw.match(/(\d+\.\d+\.\d+)/);
  return m ? `Win${m[1]}` : "WinUnknown";
}
