# WhatsApp Bridge — Agent Context

You are working on **WhatsApp Bridge**, a multi-tenant WhatsApp Web API (custom Signal/Noise
protocol client, no browser automation) with a React admin UI for connecting, sending, and
browsing WhatsApp conversations.

---

## Deployment — READ THIS BEFORE DEPLOYING ANYTHING

**`deploy/deploy.py` is the ONLY supported way to deploy this project, for humans and agents
alike.** Every other script under `deploy/` (`deploy-all.ps1`, `deploy-backend.ps1`,
`remote-deploy.py`, `upload-via-sftp.py`, `complete-deploy.ps1`, etc.) is legacy/one-off
troubleshooting history from past manual VPS deploys. Do not run them. Do not write a new
one-off deploy script. If `deploy/deploy.py` can't do what you need, extend it.

Why this rule exists: login broke twice (2026-08-01, 2026-08-04) because a frontend rebuild
silently produced a bundle without the API base URL, and once because a partial file upload
served a 500.30. None of the legacy scripts verify a build before uploading it, verify an
upload before serving it, or can undo a bad deploy. `deploy/deploy.py` gates all three:

- Refuses to build/upload if `Frontend/.env.production` doesn't declare exactly
  `VITE_API_URL=https://api.whatsapp.wreckingball.ai`, and refuses again if the built JS bundle
  doesn't actually contain that URL.
- Uploads to a temp remote folder, verifies every uploaded file's size against the local
  file, then moves the temp folder into place server-side — never serves a partial upload.
- Always replaces the full backend DLL set (delete-then-move-all), never a single-DLL swap
  (that's what caused the 2026-07-31 500.30). Server-only files (`appsettings.Production.json`,
  `appsettings.Secrets.json`, the SQLite DB) are excluded from both the upload and the delete.
- Runs post-deploy smoke checks against the live site (`/api/version`, a probe login, the
  live bundle asset, WhatsApp session continuity) and **automatically rolls back** from the
  pre-deploy backup if any of them fail.

```bash
# Prove a broken bundle gets refused — build + gate check only, touches no server:
python deploy/deploy.py --check-only

# Real deploy (needs WHATSAPPBRIDGE_DEPLOY_SSH_PASSWORD / WHATSAPPBRIDGE_PROBE_PASSWORD env
# vars — see the script's module docstring for exactly which vault credential each comes from):
python deploy/deploy.py --yes
```

Read `deploy/deploy.py`'s module docstring before running a real deploy — it documents the
exact env vars and vault credential names.

---

## Stack

| Layer | Tech |
|---|---|
| Backend API | ASP.NET Core 8 (`Backend/WhatsAppBridge.API`), IIS OutOfProcess hosting |
| WhatsApp protocol | Custom C# Signal/Noise client, `Dawa/` (no external repo dependency) |
| Database | SQLite (`whatsappbridge.db`, EF Core `EnsureCreated`) |
| Frontend | React + TypeScript + Vite (`Frontend/`) |
| Auth | JWT Bearer (+ optional 2FA) |

**Repo:** https://github.com/martiendejong/whatsappbridge
**Production:** https://whatsapp.wreckingball.ai (server `85.215.217.154`)
**Trunk branch:** `master` (no `develop` — all PRs target `master`)

## Production Architecture

```
Browser → IIS (whatsapp.wreckingball.ai, port 443)
            ├─ Frontend static files — C:\inetpub\whatsappbridge-web\
            └─ Backend API (ASP.NET Core, OutOfProcess) — C:\inetpub\whatsappbridge-api\
                 └─ WhatsApp Service (Node.js, port 3000) — C:\Services\WhatsAppBridge\
```

The frontend calls the API through IIS on the public URL — never `:5001` directly, which
bypasses the `MS-ASPNETCORE-TOKEN` IIS sets and produces 500s. This is exactly the class of
bug `deploy/deploy.py`'s bundle-URL gate exists to catch. See `DEPLOYMENT.md` for the full
incident history.

## How to Run Locally

```bash
# Backend
cd Backend/WhatsAppBridge.API && dotnet run

# Frontend (separate terminal)
cd Frontend && npm run dev
```

## Important Rules

- Never commit `appsettings.Production.json`, `appsettings.Secrets.json`, or `*.db` files —
  gitignored, server-only.
- Never single-DLL-swap the backend, locally or remotely — always a full publish +
  full-set replace (`deploy/deploy.py` does this correctly; nothing else should touch
  `C:\inetpub\whatsappbridge-api` directly).
- `Frontend/.env.production` must declare `VITE_API_URL=https://whatsapp.wreckingball.ai`
  exactly (no port, no trailing slash) — `deploy/deploy.py` refuses to build otherwise.
- Credentials (SSH, probe login) come from vault, never hardcoded in a script — several of
  the legacy `deploy/` scripts violate this; don't follow that pattern in new code.
