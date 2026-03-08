# Test Connection Button Feature - Deployment Package

## Summary

PR #2 has been merged, built, and tested locally. The deployment package is ready for production.

## What Changed

### Backend
- **New endpoint:** `POST /api/apiconnections/{id}/test`
- **File:** `WhatsAppBridge.API.dll` (updated)

### Frontend
- **Test button** added to API Connections page
- **Files:** `index.html` + all `assets/*` files (updated)

## Deployment Steps for whatsapp.wreckingball.ai

### Option 1: SSH Deployment (if credentials available)

```bash
cd E:\projects\whatsappbridge\deploy
python update-test-connection-feature.py
```

**Note:** This script currently fails with authentication error. Update SSH password in script if you have current credentials.

### Option 2: Manual Deployment via RDP

1. **RDP into server:** 85.215.217.154

2. **Stop IIS App Pool:**
   ```powershell
   Stop-WebAppPool -Name WhatsAppBridgeAPIPool
   Start-Sleep -Seconds 3
   ```

3. **Upload Backend DLL:**
   - Source: `E:\projects\whatsappbridge\publish\backend\WhatsAppBridge.API.dll`
   - Destination: `C:\inetpub\whatsappbridge-api\WhatsAppBridge.API.dll`

4. **Upload Frontend Files:**
   - Source: `E:\projects\whatsappbridge\Frontend\dist\*`
   - Destination: `C:\inetpub\whatsappbridge-web\`
   - Copy all files (index.html + assets folder)

5. **Start IIS App Pool:**
   ```powershell
   Start-WebAppPool -Name WhatsAppBridgeAPIPool
   Start-Sleep -Seconds 5
   ```

6. **Verify API Health:**
   ```powershell
   Invoke-WebRequest -Uri https://localhost:5001/api/health -UseBasicParsing
   ```

### Option 3: FTP/SFTP Upload

If FTP is configured on the server:

1. **Connect via FTP client** (FileZilla, WinSCP, etc.)
   - Host: 85.215.217.154
   - Protocol: SFTP or FTP

2. **Stop IIS** (via RDP or if you have remote PowerShell access)

3. **Upload files** to paths mentioned in Option 2

4. **Start IIS** (via RDP or remote PowerShell)

## Post-Deployment Verification

### 1. API Health Check
```bash
curl https://whatsapp.wreckingball.ai:5001/api/health
```

### 2. Swagger Documentation Check
```bash
curl https://whatsapp.wreckingball.ai:5001/swagger/v1/swagger.json | jq '.paths."/api/apiconnections/{id}/test"'
```

Should return the test endpoint definition.

### 3. Browser Test

1. Navigate to: https://whatsapp.wreckingball.ai
2. Log in with your account
3. Go to "API Connections" page
4. Click "Test" button on any connection
5. Verify:
   - Button shows "Testing..." during request
   - Success message appears (green background)
   - "Last Used" timestamp updates

## Rollback (if needed)

If issues occur after deployment:

1. **Backend rollback:**
   - Restore previous `WhatsAppBridge.API.dll` from backup
   - Or git checkout previous commit and rebuild

2. **Frontend rollback:**
   - Git checkout previous commit: `git checkout 2becfc1`
   - Rebuild: `VITE_API_URL=https://whatsapp.wreckingball.ai:5001 npm run build`
   - Re-upload dist files

## Build Artifacts Location

- **Backend:** `E:\projects\whatsappbridge\publish\backend\`
- **Frontend:** `E:\projects\whatsappbridge\Frontend\dist\`

## Testing Checklist

- [ ] Backend API responds (health check)
- [ ] Frontend loads successfully
- [ ] Login works
- [ ] API Connections page loads
- [ ] Test button appears
- [ ] Test button works (shows success/failure)
- [ ] Last Used timestamp updates
- [ ] No console errors in browser

## Support

If deployment fails:
1. Check IIS logs: `C:\inetpub\logs\LogFiles\`
2. Check API logs in server Event Viewer
3. Verify file permissions on uploaded files
4. Ensure app pool is running

---

**Deployment Package Created:** 2026-03-08
**PR:** #2 (merged)
**Feature:** Test Connection Button
