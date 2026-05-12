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

export default {
  async fetch(req: Request, env: Env): Promise<Response> {
    // Top-level catch so any unexpected throw becomes a counted "exception"
    // instead of a silent 500. The console.error feeds CF's live tail in case
    // a stack trace is useful — observability is otherwise off in wrangler.toml.
    try {
      const url = new URL(req.url);
      if (req.method === "POST" && url.pathname === "/ping") return await handlePing(req, env);
      if (req.method === "GET" && url.pathname === "/stats") return await handleStats(req, env);
      if (req.method === "GET" && url.pathname === "/") return new Response("DrunkDeer Control telemetry — see github.com/svumo/DrunkDeer-Control", { status: 200 });
      await recordError(env, "not_found");
      return new Response("not found", { status: 404 });
    } catch (e) {
      console.error("worker exception:", e);
      await recordError(env, "exception").catch(() => { /* don't loop */ });
      return new Response("internal error", { status: 500 });
    }
  },
};

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

  const [dauCounts, totalCounts, mauKeys, kbKeys, appKeys, osKeys, errorKeys] = await Promise.all([
    Promise.all(dauKeys.map(k => readCounter(env, k))),
    Promise.all(totalKeys.map(k => readCounter(env, k))),
    listKeys(env, `mau:${month}:`),
    listKeys(env, `kb_pid:${month}:`),
    listKeys(env, `app:${month}:`),
    listKeys(env, `os:${month}:`),
    listKeys(env, `error:`),
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
  const total_pings_last_30d = last30Dates.map((d, i) => ({ date: d, count: totalCounts[i] }))
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
