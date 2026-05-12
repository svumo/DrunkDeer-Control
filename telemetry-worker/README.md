# DrunkDeer Control telemetry Worker

The Cloudflare Worker that receives anonymous daily heartbeats from the [DrunkDeer Control](https://github.com/svumo/DrunkDeer-Control) WPF app and exposes aggregate stats behind a bearer token. Source is public so anyone auditing the client can verify what the server does.

## What it stores

Per ping: a 36-hour `seen:YYYY-MM-DD:<deviceid>` key (so we don't double-count the same device on the same day) plus increments to four counters: `dau:<date>`, `mau:<month>:<deviceid>`, `kb_pid:<month>:0x????`, `app:<month>:1.x.y`, `os:<month>:Win<version>`.

Health/abuse signals (35-day TTL, no per-device data): `total_pings:<date>` (every valid attempt, including dedup'd repeats) and `error:<date>:<reason>` (one counter per rejection reason — `too_large`, `bad_content_type`, `invalid_json`, `bad_id`, `not_found`, `exception`). Lets us see if the worker is being hammered or quietly 5xx'ing without any device-level data.

What it does **not** store: IPs, raw payloads, anything that lets you correlate fields across a single user. The Worker never reads `cf-connecting-ip`; invocation logs are off.

## Endpoints

- `POST /ping` — accepts the JSON heartbeat from the WPF client. Validates shape, dedups, bumps counters, returns `204`.
- `GET /stats` — requires `Authorization: Bearer ${STATS_TOKEN}`, returns aggregates as JSON.
- `GET /firmware` — **public, no auth.** Returns the latest known firmware version per USB PID, plus the source bundle URL and a UTC timestamp of the last cron run. The WPF client polls this on launch to surface an in-app update banner. Cached at the edge for 1h.
- `GET /` — short banner pointing back to the main repo.

## Firmware version channel

A daily cron (`0 6 * * *` UTC, see `wrangler.toml`) scrapes [drunkdeer.com/pages/downloads](https://drunkdeer.com/pages/downloads), finds the latest `DrunkdeerUpdaterV*.zip` link on the page, downloads it (~16 MB) into Worker memory, unzips it with [`fflate`](https://www.npmjs.com/package/fflate), and parses `config/config.ini` to extract each `[DEVICEn]` section's `VidAndPid` + `Version`. The resulting `pid → version` map is written to KV under `fw:0x????` keys (no TTL — overwritten next run), alongside `fw:bundle_url` and `fw:bundle_checked_at` for visibility.

`GET /firmware` shape:

```json
{
  "versions": { "0x2383": "0x0008", "0x2384": "0x0014", "0x2391": "0x000a" },
  "bundle": "https://cdn.shopify.com/s/files/.../DrunkdeerUpdaterV2.3.1.zip?v=1761113068",
  "checked": "2026-05-12T06:00:01.234Z"
}
```

Empty `versions` is the expected shape before the first cron run after deploy. Trigger it manually with `wrangler dev --test-scheduled` + `curl "http://localhost:8787/__scheduled?cron=0+6+*+*+*"` for local testing.

## First-time deploy

```bash
# from the telemetry-worker/ folder
npm install
npx wrangler login                        # browser prompt
npx wrangler kv:namespace create telemetry        # paste the id into wrangler.toml -> kv_namespaces[0].id
npx wrangler kv:namespace create telemetry --preview  # paste into preview_id (used by `wrangler dev`)

# Generate a random 64-char hex token and paste when prompted.
# Keep it somewhere safe — you'll need it to read /stats.
npx wrangler secret put STATS_TOKEN

npx wrangler deploy
```

After deploy, the Worker URL prints out — looks like `https://drunkdeer-telemetry.<your-subdomain>.workers.dev`. **Copy that URL** and update `WpfApp/UsageReporter.cs` in the main repo:

```csharp
private const string Endpoint = "https://drunkdeer-telemetry.<your-subdomain>.workers.dev/ping";
```

## Reading stats

```bash
TOKEN=<the secret you set above>
curl -H "Authorization: Bearer $TOKEN" \
     https://drunkdeer-telemetry.<your-subdomain>.workers.dev/stats
```

Returns:

```json
{
  "generated_at": "2026-05-07T12:34:56.000Z",
  "dau_last_30d": [{"date":"2026-04-08","count":89}, ..., {"date":"2026-05-07","count":1247}],
  "mau_current": 4823,
  "by_kb_pid": { "0x2383": 612, "0x2391": 89, "0x2a08": 4122, "none": 14 },
  "by_app": { "1.4.2": 23, "1.5.0": 4800 },
  "by_os": { "Win10.0.26200": 2104, "Win10.0.22631": 1812 },
  "total_pings_last_30d": [{"date":"2026-05-07","count":1289}, ...],
  "errors_last_30d": [{"date":"2026-05-07","reason":"bad_id","count":2}]
}
```

`total_pings_last_30d` counts every valid `/ping` request (including dedup'd repeats from the same device on the same day) — useful for spotting abuse. Days before the feature was first deployed are omitted (the worker records the first-tracked date in `meta:total_pings_since`), so the array can be shorter than 30 entries until ~30 days after deploy. `errors_last_30d` is per-day-per-reason; an empty array is the happy case.

## Local dev

```bash
npx wrangler dev
# Worker is now listening on http://localhost:8787

# In another terminal, simulate a ping:
curl -X POST http://localhost:8787/ping \
     -H 'Content-Type: application/json' \
     -d '{"id":"abcdef0123456789","app":"1.6.0","os":"Microsoft Windows NT 10.0.26200.0","kb_pid":"0x2383","kb_fw":"0.48","ts":1730000000}'
```

To test `/stats` locally, set the secret in `.dev.vars`:

```
STATS_TOKEN=test-local-token
```

Then `curl -H "Authorization: Bearer test-local-token" http://localhost:8787/stats`.

## Free-tier sizing

Cloudflare Workers free tier as of late 2025: 100k requests/day, 1k KV writes/day per namespace. With one ping per user per day we comfortably support 1k DAU on free; past that, KV writes become the bottleneck (each ping does ~5 writes). Either upgrade to paid (~$5/month flat) or batch the writes into Durable Objects.

## Privacy audit checklist

- [ ] `wrangler.toml` has `[observability] enabled = false`.
- [ ] `src/index.ts` does not contain `cf-connecting-ip` (grep should return empty).
- [ ] `STATS_TOKEN` is set and never committed (it's a Worker secret, not a file).
- [ ] Manually curl `/stats` without the token → expect `401`.
- [ ] Manually `wrangler tail` during a real ping → confirm the only logged content is what's in this Worker's source.
