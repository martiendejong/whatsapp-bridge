# WhatsApp Node bridge — live snapshot + fixes (ClickUp 869e52fhq)

This folder is a snapshot of the **legacy Node.js/whatsapp-web.js bridge** that
actually runs on the prod VPS at `C:\inetpub\whatsappservice` (Windows service
`WhatsAppBridgeNode`, port 3000). It is a **separate, independent system** from
the `Dawa`/`WhatsAppBridge.API` code elsewhere in this repo — the two do not
call each other. This one exists only because a handful of legacy call sites
(and the current `/message/send` implementation) still depend on
`whatsapp-web.js` + Puppeteer/Chromium instead of the native Dawa client.

Until now, this service's code (`index.js`, `service-wrapper.cjs`,
`install-service.cjs`) had **no source control at all** — it was hand-edited
directly on the server. `index.js` here was pulled read-only from the live
box on 2026-07-15 and matches what's currently deployed
(`C:\inetpub\whatsappservice\index.js`, last write 2026-07-14 15:43). It has
drifted significantly from the older `deploy/server-index.js` already in this
repo (different routes, added `/diag`, `/messages/raw`, `readySessions`
tracking, etc.) — treat *this* folder as the current source of truth going
forward, not `deploy/server-index.js`.

## What changed vs. the live version

1. **`service-wrapper.cjs`** — fixed the restart-loop bug from 869e52fhq ask
   #5. Windows has no real POSIX signals, so `child.kill('SIGTERM'/'SIGINT')`
   only ever terminates the direct Node child, never its process tree.
   `index.js` spawns Puppeteer, which spawns its own Chromium subprocess —
   killing only the Node child leaves Chromium running, holding the profile
   lock (and sometimes the port), which is why every service restart hit
   `EADDRINUSE` on port 3000. Now uses `taskkill /pid <pid> /t /f` to kill the
   whole tree on stop.
2. **`package.json`** — bumped `whatsapp-web.js` from `^1.23.0` (resolved to
   1.34.6 on the live box) to `^1.34.7`, the current latest release (869e52fhq
   ask #2 — `sendMessage` returning `undefined` is consistent with the
   library falling behind WhatsApp Web's client version requirements).
3. **`install-service.cjs`** — unchanged, included for completeness.

`ask #4` (Dawa API returning `success:true` while nothing is delivered) is a
**separate bug in the Dawa/WhatsAppBridge.API code**, not in this Node
service — see PR for
`fix/869e52fhq-silent-send-failure-swallowed-as-success`.

## What this PR does NOT do

- **Does not touch the live server.** These are source-controlled copies only.
  Deploying them requires a human to run the steps below on the prod VPS
  (85.215.217.154), since the box currently has an unresolved identity/
  impersonation concern (869e52fhq ask #1) that needs to be closed with Frank
  first, and because `npm install` + a service restart there should happen
  with someone able to watch it live.
- **Does not fully fix ask #2.** A version bump alone may not resolve
  `sendMessage` returning `undefined` — whatsapp-web.js has a history of
  needing WhatsApp's current web-client version string, which sometimes needs
  a manual override even on the latest release. Live validation is blocked on
  869e52fhq ask #3 (re-linking the Jengo number via QR scan), which requires a
  human with the physical phone.

## Redeploy steps (for whoever does this — needs an active RDP/SSH session)

```powershell
# 1. Stop the service first (it's already broken, so no live session is at risk)
Stop-Service WhatsAppBridgeNode

# 2. Copy the three files from this folder to C:\inetpub\whatsappservice\,
#    overwriting index.js, service-wrapper.cjs, package.json

# 3. Re-resolve the lockfile against the new whatsapp-web.js range
cd C:\inetpub\whatsappservice
npm install

# 4. Start the service back up
Start-Service WhatsAppBridgeNode

# 5. Confirm no zombie chrome.exe/node.exe survives a subsequent restart:
Restart-Service WhatsAppBridgeNode
Get-Process node,chrome -ErrorAction SilentlyContinue
Get-NetTCPConnection -LocalPort 3000 -ErrorAction SilentlyContinue
```
