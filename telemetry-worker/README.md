# DrunkDeer Control firmware-version Worker

Cloudflare Worker that scrapes the official DrunkDeer downloads page once a day and exposes the latest published firmware version per USB PID. The WPF app polls this on launch so it can surface an in-app banner when the connected keyboard's firmware is older than what DrunkDeer ships. **No user data flows the other direction** — there are no `/ping` or `/stats` endpoints, no KV writes per device, no counters. The earlier daily-heartbeat telemetry was removed; GitHub release download counts answer the "is anyone using this?" question well enough without phoning home.

Source is public so anyone auditing the WPF client can verify what the server does (and does not do).

> The folder is still called `telemetry-worker/` so the deployed worker URL (`drunkdeer-telemetry.svumo.workers.dev/firmware`) doesn't change and break clients that have the old URL baked in. The contents now have nothing to do with usage telemetry.

## Endpoints

- `GET /firmware` — **public, no auth.** Returns the latest known firmware version per USB PID, plus the source bundle URL and a UTC timestamp of the last cron run. Cached at the edge for 1h.
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
npx wrangler login                                # browser prompt
npx wrangler kv:namespace create telemetry        # paste the id into wrangler.toml -> kv_namespaces[0].id
npx wrangler kv:namespace create telemetry --preview  # paste into preview_id (used by `wrangler dev`)

npx wrangler deploy
```

After deploy, the Worker URL prints out — looks like `https://drunkdeer-telemetry.<your-subdomain>.workers.dev`. The WPF client's `WpfApp/FirmwareUpdateChecker.cs` has this URL baked in; update there if you fork.

## Local dev

```bash
npx wrangler dev
# Worker is now listening on http://localhost:8787

# Hit /firmware:
curl http://localhost:8787/firmware

# Trigger the cron once for local testing:
curl "http://localhost:8787/__scheduled?cron=0+6+*+*+*"
```

## Privacy audit checklist

- [ ] `wrangler.toml` has `[observability] enabled = false`.
- [ ] `src/index.ts` does not contain `cf-connecting-ip` (grep should return empty).
- [ ] `src/index.ts` has no `POST` handlers (only `GET /firmware` and `GET /`).
- [ ] Manually `wrangler tail` while curling `/firmware` → confirm the only logged content is the cron run, not any per-request data.
