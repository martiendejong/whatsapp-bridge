# WhatsApp Engine Switching (Dawa / Baileys / WaWeb)

The bridge can talk to WhatsApp through one of three engines. An admin picks which one; the
choice is global and persists across restarts.

| Engine | What it is | Runs as |
|---|---|---|
| `dawa` (default) | C# port of Baileys, lives in `/Dawa` | In-process |
| `baileys` | The real `@whiskeysockets/baileys` library | Node.js sidecar process, one per session (`/baileys-sidecar`) |
| `waweb` | The REAL WhatsApp Web client, driven by `whatsapp-web.js` in a headless Chromium | Node.js sidecar process + one headless browser per session (`/waweb-sidecar`) |

Why switch: Dawa's protocol fingerprint ages between manual ports, which has been the main
driver of session bans (see the 2026-08-29 ban analysis). Baileys upstream updates its
fingerprint continuously — but it is still a reimplemented protocol client, and both have
kept losing sessions to fingerprint-based bans. The `waweb` engine takes the other route:
it runs the authentic WhatsApp Web client in a real browser, so there is no protocol
fingerprint to detect at all. Remaining risks on `waweb` are behavioral (message volume,
datacenter IP reputation, frequent re-pairing) and upstream: WhatsApp Web UI changes can
break `whatsapp-web.js` until it updates (the version is pinned — watch upstream releases).
Dawa remains the default and the fallback.

## Switching (admin only)

UI: **Account Settings → WhatsApp Engine** (visible only to admins).

API (JWT of a user with `IsAdmin`):

```
GET /api/admin/engine
→ { "engine": "dawa", "availableEngines": ["dawa","baileys","waweb"], "activeSessions": [...] }

PUT /api/admin/engine
{ "engine": "waweb", "restartSessions": true }
```

- Without `restartSessions`, the new engine applies whenever a session next (re)connects
  (app-pool recycle, disconnect/reconnect, new session).
- With `restartSessions: true`, all active sessions are disconnected and restored on the new
  engine immediately.

## Credentials are per engine — first switch needs a QR re-pair

- Dawa creds: `{sessionsDir}/{sessionId}/creds.json`
- Baileys creds: `{sessionsDir}/{sessionId}/baileys-auth/` (multi-file auth state)
- WaWeb creds: `{sessionsDir}/{sessionId}/waweb-auth/` (persistent Chromium profile managed
  by whatsapp-web.js LocalAuth, profile folder `session-bridge/`)

The formats are not interchangeable. The first time a session connects on another engine it
has no creds there, so it stays disconnected until the QR is re-scanned from the sessions
page. Each pairing registers a new linked device on the phone — do NOT flip back and forth
rapidly; repeated re-pairing from a datacenter IP is a known ban footprint. Once an engine
has paired creds, switching back to it reuses them without a new QR scan (note that this
means multiple linked devices exist; unlink the unused ones in WhatsApp if a switch is meant
to be permanent).

## How the sidecar engines work

`BaileysEngine.cs` and `WaWebEngine.cs` (both built on `SidecarEngine.cs`) spawn
`node <sidecar>/index.js --session-dir <dir>` per session and speak newline-delimited JSON
over stdin/stdout (commands with request ids, plus unsolicited `qr`/`connected`/`message`/...
events). No ports are opened; the sidecar exits when the API closes stdin or dies.
Everything downstream (message store, SQLite persistence, webhooks, guardrail, media
decrypt/Whisper enrichment) is engine-agnostic — each sidecar maps its library's payloads
onto the same `IncomingMessage` shape Dawa produces. The waweb sidecar also normalizes jids:
WhatsApp Web uses `xxx@c.us` internally, the bridge always sees Baileys-style
`xxx@s.whatsapp.net` (groups are `xxx@g.us` in both).

Not supported on Baileys: `sessions/{id}/send-retry-receipt` (Dawa-specific escape hatch;
returns 400 with an explanatory error — Baileys handles retries internally).

Not supported on WaWeb (surfaced as `NotSupportedException` / explanatory errors):
- `send-retry-receipt` (same as Baileys)
- on-demand deep history sync (`requestHistorySync`) — `fetchMessageHistory` works and
  returns the messages directly via `chat.fetchMessages`
- presence subscription (`subscribePresence`) — WA Web exposes no presence subscribe API
- LID resolution (`resolveLid`) returns null (same graceful behavior as Baileys versions
  without lidMapping support)

## Deployment requirements for the sidecar engines

- Node.js ≥ 18 on the server (configurable via `WhatsApp:Baileys:NodePath` /
  `WhatsApp:WaWeb:NodePath`, default `node`).
- The sidecar folders (`baileys-sidecar/`, `waweb-sidecar/` — index.js + package.json) are
  copied into the publish output by the csproj; `deploy/deploy.py` runs `npm install` in
  them during the build step, so `node_modules` ships with the deploy. If npm is missing at
  build time the deploy continues with a warning and only the Dawa engine works.
- **waweb additionally needs a Chrome/Chromium on the server.** Puppeteer normally
  downloads its own Chromium on `npm install` (~170 MB) — deploy.py suppresses that
  (`PUPPETEER_SKIP_DOWNLOAD=1`) to keep deploys small. Install Chrome on the server and set
  `WhatsApp:WaWeb:ChromePath` (e.g. `C:\Program Files\Google\Chrome\Application\chrome.exe`);
  the engine passes it to the sidecar as `WAWEB_CHROME_PATH`. Without it the waweb engine
  fails on connect with a "could not find Chrome" error from Puppeteer.
- **waweb resource cost:** one headless Chromium per connected session, roughly 300-500 MB
  RAM each (plus CPU spikes on sync). Budget accordingly before moving many sessions over.
- Optional config overrides: `WhatsApp:Engine` (initial default before an admin ever saved a
  choice), `WhatsApp:Baileys:NodePath`, `WhatsApp:Baileys:SidecarScript`,
  `WhatsApp:WaWeb:NodePath`, `WhatsApp:WaWeb:SidecarScript`, `WhatsApp:WaWeb:ChromePath`.

## Storage

- Engine choice: `AppSettings` table, key `WhatsAppEngine` (self-healed in Program.cs).
- `WhatsAppSessions.Engine` column records which engine a session last connected with
  (informational, shown in the admin UI).
- Baileys sidecar chat/contact cache: `{sessionDir}/baileys-store.json` (the waweb sidecar
  needs no cache file — chats/contacts come straight from the live WA Web client).
