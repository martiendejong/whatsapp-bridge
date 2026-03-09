# WhatsApp Bridge - Deployment Guide

## Critical Configuration

### Frontend Environment Variables

**IMPORTANT:** The frontend uses different `.env` files for different environments:

- `.env.development` - Used during `npm run dev`
- `.env.production` - **Used during `npm run build`**

### Common Issue: Wrong API URL in Production

**Problem:** Frontend calls `https://whatsapp.wreckingball.ai:5001/api/...` instead of `https://whatsapp.wreckingball.ai/api/...`

**Symptoms:**
- Network requests show port `:5001` in DevTools
- 500 Internal Server Error on API calls
- Error in logs: `'MS-ASPNETCORE-TOKEN' does not match the expected pairing token`
- QR code popup doesn't appear

**Root Cause:**
The `.env.production` file had the wrong URL with port 5001:
```env
VITE_API_URL=https://whatsapp.wreckingball.ai:5001  ❌ WRONG
```

Port 5001 is the ASP.NET Core backend's **internal** port. The frontend should call the **public** URL (port 443) which IIS proxies to the backend.

**Correct Configuration:**
```env
VITE_API_URL=https://whatsapp.wreckingball.ai  ✅ CORRECT
```

### Why This Matters

When the frontend calls port 5001 directly:
1. It bypasses IIS completely
2. Requests hit the ASP.NET Core process directly
3. The `MS-ASPNETCORE-TOKEN` header doesn't match (IIS sets this token for security)
4. All API requests fail with 500 errors
5. Login fails → Session creation fails → No QR code

## Deployment Process

### Automated Deployment (Recommended)

Use the complete deployment script:

```powershell
C:\scripts\deploy-whatsappbridge-full.ps1
```

This script:
1. ✅ Verifies `.env.production` has correct URL (fixes it if wrong)
2. ✅ Builds backend (`dotnet publish`)
3. ✅ Builds frontend (`npm run build`)
4. ✅ Deploys both to production server
5. ✅ Ensures WhatsApp service is running
6. ✅ Restarts IIS app pools
7. ✅ Verifies deployment

### Manual Deployment

If deploying manually:

#### 1. Verify Frontend Configuration

```powershell
# Check .env.production
Get-Content E:\projects\whatsappbridge\Frontend\.env.production
```

Expected content:
```env
VITE_API_URL=https://whatsapp.wreckingball.ai
```

If it contains `:5001`, fix it:
```powershell
Set-Content -Path "E:\projects\whatsappbridge\Frontend\.env.production" -Value "VITE_API_URL=https://whatsapp.wreckingball.ai"
```

#### 2. Build Backend

```powershell
cd E:\projects\whatsappbridge\Backend\WhatsAppBridge.API
dotnet publish -c Release -o publish --no-self-contained
```

#### 3. Build Frontend

```powershell
cd E:\projects\whatsappbridge\Frontend
npm run build
```

#### 4. Deploy Backend

```powershell
C:\scripts\deploy-latest-backend.ps1
```

#### 5. Deploy Frontend

```powershell
C:\scripts\deploy-frontend.ps1
```

## Architecture

### Production Stack

```
User Browser
    ↓ HTTPS (port 443)
IIS (whatsapp.wreckingball.ai)
    ↓
    ├─→ Frontend (Static files) - Port 80/443
    │   C:\inetpub\whatsappbridge-web\
    │
    └─→ Backend API (ASP.NET Core) - Port 5001 (internal)
        C:\inetpub\whatsappbridge-api\
        ↓
        WhatsApp Service (Node.js) - Port 3000
        C:\Services\WhatsAppBridge\
```

### Key Points

1. **Frontend** is served as static files by IIS
2. **Backend API** runs as ASP.NET Core process (OutOfProcess hosting)
   - IIS spawns `dotnet.exe` process
   - Backend listens on random port (e.g., 31119, 47741)
   - IIS proxies requests from port 443 → backend port
3. **WhatsApp Service** runs as standalone Node.js process on port 3000

### Frontend API Calls

**Correct flow:**
```
Browser → https://whatsapp.wreckingball.ai/api/auth/login
         ↓ (IIS receives on port 443)
         → ASP.NET Core backend (via MS-ASPNETCORE-TOKEN header)
         ↓ (port 5001 internal proxy)
         → AuthController.Login()
```

**Incorrect flow (when port 5001 specified):**
```
Browser → https://whatsapp.wreckingball.ai:5001/api/auth/login
         ↓ (bypasses IIS, hits backend directly)
         → ASP.NET Core backend (no valid MS-ASPNETCORE-TOKEN)
         ↓
         ❌ 400 Bad Request / 500 Internal Server Error
```

## Testing After Deployment

1. **Clear browser cache** (Ctrl+Shift+R)
2. Go to https://whatsapp.wreckingball.ai
3. Open DevTools → Network tab
4. Login with test credentials
5. **Verify:** API calls go to `https://whatsapp.wreckingball.ai/api/...` (NO `:5001`)
6. Click "Connect WhatsApp"
7. **Expected:** QR code popup appears within 5-10 seconds

## Troubleshooting

### QR Code Not Appearing

Check in DevTools Network tab:

**Problem 1: API calls show `:5001` port**
- Solution: Rebuild frontend with correct `.env.production`, redeploy

**Problem 2: Session creation returns `qrCode: null`**
- Check: WhatsApp service on port 3000
- Test: `Invoke-WebRequest http://localhost:3000/health` (on server)
- Fix: Restart WhatsApp service

**Problem 3: 500 errors on API calls**
- Check: IIS logs and application logs
- Common cause: MS-ASPNETCORE-TOKEN mismatch (port 5001 issue)
- Fix: Verify frontend uses correct API URL

### WhatsApp Service Not Running

```powershell
# On production server
cd C:\Services\WhatsAppBridge
node index.js  # Should start and listen on port 3000
```

Or use the startup script:
```powershell
C:\scripts\start-whatsapp-correct-path.ps1
```

## Verification Scripts

- `C:\scripts\FINAL-WHATSAPP-TEST.ps1` - Complete system test
- `C:\scripts\check-production-database.ps1` - Database verification
- `C:\scripts\verify-deployment-versions.ps1` - File version check

## Credentials

- **Server:** 85.215.217.154
- **User:** administrator
- **Password:** SpaceElevator1tam!
- **App Login:** info@martiendejong.nl / W5@RY03@s%xa!N

## Issue History

### 2026-03-09: CORS Configuration Missing Production Domain

**Problem:** Session creation fails with 500 error when clicking "Connect WhatsApp"

**Symptoms:**
- Network request to `/api/whatsapp/sessions/create` returns 500 error
- No logs in application (request never reaches backend)
- CORS error in browser console (if visible)

**Root Cause:**
The `appsettings.json` `AllowedOrigins` only contained localhost URLs:
```json
"AllowedOrigins": [
  "http://localhost:5173",
  "http://localhost:5000",
  "http://localhost:9237"
]
```

Production domain `https://whatsapp.wreckingball.ai` was missing, causing CORS to block all API requests from the production frontend.

**Resolution:**
1. Added production domain to `AllowedOrigins`:
```json
"AllowedOrigins": [
  "http://localhost:5173",
  "http://localhost:5000",
  "http://localhost:9237",
  "https://whatsapp.wreckingball.ai"  ← ADDED
]
```
2. Rebuilt backend: `dotnet publish`
3. Deployed to production
4. Updated deployment script to check CORS configuration

**Prevention:**
- Use `C:\scripts\deploy-whatsappbridge-full.ps1` (now checks CORS)
- Script automatically adds production domain if missing

### 2026-03-08: Frontend Port 5001 Bug

**Problem:** QR code popup not appearing after clicking "Connect WhatsApp"

**Root Cause:** `.env.production` contained `https://whatsapp.wreckingball.ai:5001` instead of `https://whatsapp.wreckingball.ai`

**Impact:**
- Frontend bypassed IIS and called backend directly on port 5001
- MS-ASPNETCORE-TOKEN header mismatch
- All API calls failed with 500 errors
- Login failed → No QR code generation

**Resolution:**
1. Fixed `.env.production` to use correct URL (without port)
2. Rebuilt frontend: `npm run build`
3. Deployed to production
4. Created automated deployment script with configuration verification
5. Documented issue in DEPLOYMENT.md

**Prevention:**
- Use `C:\scripts\deploy-whatsappbridge-full.ps1` for all deployments
- Script automatically checks and fixes `.env.production` before building
- Verification step ensures correct API URL is used
