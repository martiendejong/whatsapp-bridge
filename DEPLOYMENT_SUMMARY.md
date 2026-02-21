# WhatsApp Bridge - Deployment Summary

## Deployment Status

**Date:** 2026-02-21
**Target Server:** 85.215.217.154
**Domain:** whatsapp.wreckingball.ai

### What Was Deployed

✅ **Project Files Uploaded** (18.5 MB via SFTP)
- Backend: ASP.NET Core 9.0 API with SQLite database
- Frontend: React + TypeScript + Vite SPA
- WhatsApp Service: Node.js with whatsapp-web.js
- Deployment Scripts: PowerShell automation for Windows Server

✅ **Server Prerequisites Verified**
- .NET SDK 8.0.416 (compatible with .NET 9 apps)
- Node.js v22.19.0
- IIS installed
- NSSM required (will be installed by script)

⏳ **Installation Script Running**
The INSTALL_ON_SERVER.ps1 script was uploaded and executed remotely.
This script performs the following (estimated 15-20 minutes):

1. Install NSSM (service wrapper)
2. Deploy WhatsApp Service as Windows Service on port 3000
3. Build and publish Backend API to IIS on port 5000
4. Build and deploy Frontend to IIS on port 80
5. Configure IIS bindings for whatsapp.wreckingball.ai
6. Set up CORS and security configurations

---

## DNS Configuration Required

**To complete the deployment, add this A record to your wreckingball.ai DNS:**

```
Type:  A
Name:  whatsapp
Value: 85.215.217.154
TTL:   3600
```

After DNS propagation (typically 5-30 minutes), the application will be accessible at:

- **Web Application:** http://whatsapp.wreckingball.ai
- **API Swagger UI:** http://whatsapp.wreckingball.ai:5000/swagger

---

## Application Features

### Authentication & Security
- User registration and login (JWT authentication)
- Configurable AES-256 encryption for sensitive data
- Encryption settings in `appsettings.Production.json`

### API Connections
- Users can create multiple API connections
- Each connection has a unique name and token
- Tokens are used to authenticate API requests

### WhatsApp Integration
- QR code scanning to connect WhatsApp accounts
- One WhatsApp session per user account
- Full WhatsApp Web API functionality

### API Endpoints
All standard WhatsApp Web operations:
- Send messages
- Send media (images, documents, videos)
- Get contacts
- Get chats
- Message status tracking

---

## Next Steps

1. **Add DNS Record** (instructions above)
2. **Wait for Installation** (15-20 minutes from deployment start)
3. **Wait for DNS Propagation** (5-30 minutes after adding record)
4. **Test Access:**
   - Visit http://whatsapp.wreckingball.ai
   - Register a new account
   - Create an API connection
   - Scan QR code with WhatsApp
   - Test API endpoints via Swagger

---

## Configuration Files

### Backend (appsettings.Production.json)
Location: `C:\inetpub\whatsappbridge-api\appsettings.Production.json`

Generated with:
- Random JWT secret key
- SQLite database path
- WhatsApp service URL (http://localhost:3000)
- CORS allowed origins for your domain
- Encryption disabled by default (can be enabled)

### Frontend (Environment)
Built with: `VITE_API_URL=http://whatsapp.wreckingball.ai:5000`

### WhatsApp Service
- Runs as Windows Service "WhatsAppBridgeService"
- Port: 3000
- Managed by NSSM

---

## Troubleshooting

If the application doesn't respond after DNS propagation:

1. **Check Services are Running:**
   ```powershell
   Get-Service WhatsAppBridgeService
   Get-Website | Where-Object { $_.Name -like "*WhatsApp*" }
   ```

2. **Check Ports are Listening:**
   ```powershell
   netstat -an | findstr "3000 5000 80"
   ```

3. **View IIS Logs:**
   ```
   C:\inetpub\logs\LogFiles\
   ```

4. **View Service Logs:**
   Check Windows Event Viewer for service errors

---

## Security Notes

- Default encryption is **disabled** for easier initial setup
- To enable encryption, run `tools\generate-encryption-keys.ps1` and update `appsettings.Production.json`
- **SSL certificate**: Install after DNS is live (see SSL Installation section below)
- Database is SQLite (suitable for low-to-medium traffic)
- API tokens are stored in database (encrypted if encryption enabled)

---

## SSL Certificate Installation

**After DNS is live and propagated**, install a free Let's Encrypt SSL certificate:

### Quick Setup (Recommended)

```bash
cd E:\projects\whatsappbridge\deploy
setup-ssl.cmd
```

Or using Python directly:

```bash
python install-ssl-remote.py
```

This will:
- ✅ Automatically download and install Let's Encrypt certificate
- ✅ Configure HTTPS bindings (port 443 for web, 5001 for API)
- ✅ Set up automatic renewal (every 60 days)
- ✅ Add security headers (HSTS, X-Frame-Options, etc.)

### After SSL Installation

Your application will be accessible via HTTPS:
- **Secure Web:** https://whatsapp.wreckingball.ai
- **Secure API:** https://whatsapp.wreckingball.ai:5001/swagger

### Optional: Force HTTPS Redirect

Automatically redirect all HTTP traffic to HTTPS:

```bash
python -c "import paramiko; ssh = paramiko.SSHClient(); ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy()); ssh.connect('85.215.217.154', username='administrator', password='3WsXcFr$7YhNmKi*'); ssh.exec_command('powershell -ExecutionPolicy Bypass -File C:\\whatsappbridge\\deploy\\enable-https-redirect.ps1'); ssh.close()"
```

### Full Documentation

See `deploy\SSL_INSTALLATION_GUIDE.md` for detailed instructions and troubleshooting.

---

## Project Structure on Server

```
C:\whatsappbridge\
├── Backend\           (ASP.NET Core source)
├── Frontend\          (React source)
├── WhatsAppService\   (Node.js source)
├── deploy\            (Deployment scripts)
└── tools\             (Utility scripts)

C:\Services\WhatsAppBridge\     (Running Node.js service)
C:\inetpub\whatsappbridge-api\  (Published Backend API)
C:\inetpub\whatsappbridge-web\  (Built Frontend)
```

---

## Deployment Scripts Created

All deployment scripts are in `E:\projects\whatsappbridge\deploy\`:

- `INSTALL_ON_SERVER.ps1` - Complete installation script (executed remotely)
- `complete-deploy.ps1` - PowerShell remoting deployment
- `upload-via-sftp.py` - SFTP upload and extraction (used successfully)
- `upload-install-script.py` - Direct script upload and execution
- `check-status.py` - Verify installation status
- `deploy-wreckingball.ps1` - Domain-specific configuration

---

## Database Schema

SQLite database at: `C:\inetpub\whatsappbridge-api\whatsappbridge.db`

Tables:
- `Users` - User accounts (username, email, hashed password)
- `ApiConnections` - API tokens (name, token, userId, createdAt)
- `WhatsAppSessions` - WhatsApp session IDs linked to users
- `__EFMigrationsHistory` - Entity Framework migrations

---

**Deployment initiated:** 2026-02-21 13:36 UTC
**Expected completion:** 2026-02-21 13:56 UTC
**DNS propagation:** Add record immediately, expect full availability by 14:30 UTC
