# WhatsApp Engine Switching (Dawa / Baileys)

The bridge can talk to WhatsApp through one of two engines. An admin picks which one; the
choice is global and persists across restarts.

| Engine | What it is | Runs as |
|---|---|---|
| `dawa` (default) | C# port of Baileys, lives in `/Dawa` | In-process |
| `baileys` | The real `@whiskeysockets/baileys` library | Node.js sidecar process, one per session (`/baileys-sidecar`) |

Why switch: Dawa's protocol fingerprint ages between manual ports, which has been the main
driver of session bans (see the 2026-08-29 ban analysis). Baileys upstream updates its
fingerprint continuously. Running Baileys directly removes that lag; Dawa remains the default
and the fallback.

## Switching (admin only)

UI: **Account Settings → WhatsApp Engine** (visible only to admins).

API (JWT of a user with `IsAdmin`):

```
GET /api/admin/engine
→ { "engine": "dawa", "availableEngines": ["dawa","baileys"], "activeSessions": [...] }

PUT /api/admin/engine
{ "engine": "baileys", "restartSessions": true }
```

- Without `restartSessions`, the new engine applies whenever a session next (re)connects
  (app-pool recycle, disconnect/reconnect, new session).
- With `restartSessions: true`, all active sessions are disconnected and restored on the new
  engine immediately.

## Credentials are per engine — first switch needs a QR re-pair

- Dawa creds: `{sessionsDir}/{sessionId}/creds.json`
- Baileys creds: `{sessionsDir}/{sessionId}/baileys-auth/` (multi-file auth state)

The formats are not interchangeable. The first time a session connects on the other engine it
has no creds there, so it stays disconnected until the QR is re-scanned from the sessions
page. Each pairing registers a new linked device on the phone — do NOT flip back and forth
rapidly; repeated re-pairing from a datacenter IP is a known ban footprint. Once both engines
have paired creds, switching reuses them without new QR scans (note that this means two
linked devices exist; unlink the unused one in WhatsApp if a switch is meant to be permanent).

## How the Baileys engine works

`BaileysEngine.cs` spawns `node baileys-sidecar/index.js --session-dir <dir>` per session and
speaks newline-delimited JSON over stdin/stdout (commands with request ids, plus unsolicited
`qr`/`connected`/`message`/... events). No ports are opened; the sidecar exits when the API
closes stdin or dies. Everything downstream (message store, SQLite persistence, webhooks,
guardrail, media decrypt/Whisper enrichment) is engine-agnostic — the sidecar maps Baileys
payloads onto the same `IncomingMessage` shape Dawa produces.

Not supported on Baileys: `sessions/{id}/send-retry-receipt` (Dawa-specific escape hatch;
returns 400 with an explanatory error — Baileys handles retries internally).

## Deployment requirements for the Baileys engine

- Node.js ≥ 18 on the server (configurable via `WhatsApp:Baileys:NodePath`, default `node`).
- `baileys-sidecar/` (index.js + package.json) is copied into the publish output by the csproj;
  `deploy/deploy.py` runs `npm install` in it during the build step, so `node_modules` ships
  with the deploy. If npm is missing at build time the deploy continues with a warning and
  only the Dawa engine works.
- Optional config overrides: `WhatsApp:Engine` (initial default before an admin ever saved a
  choice), `WhatsApp:Baileys:NodePath`, `WhatsApp:Baileys:SidecarScript`.

## Storage

- Engine choice: `AppSettings` table, key `WhatsAppEngine` (self-healed in Program.cs).
- `WhatsAppSessions.Engine` column records which engine a session last connected with
  (informational, shown in the admin UI).
- Sidecar chat/contact cache: `{sessionDir}/baileys-store.json`.
