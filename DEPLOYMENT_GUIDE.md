# WhatsApp Bridge - Production Deployment Guide

Complete guide for deploying WhatsApp Bridge to a Windows VPS server.

## Prerequisites

- Windows Server 2019 or later
- Administrator access
- At least 4GB RAM
- 20GB free disk space
- Internet connection

## Step 1: Prepare the Server

1. **RDP into your Windows VPS**

2. **Download the project**

```powershell
# Clone from git or upload the zip file
git clone https://github.com/your-org/whatsappbridge.git C:\whatsappbridge
# OR
# Extract zip to C:\whatsappbridge
```

3. **Open PowerShell as Administrator**

```powershell
# Allow script execution
Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope LocalMachine -Force

# Navigate to deployment directory
cd C:\whatsappbridge\deploy
```

## Step 2: Install Prerequisites

```powershell
.\install-prerequisites.ps1
```

This script installs:
- .NET 9 SDK
- Node.js 20 LTS
- IIS with all required features
- ASP.NET Core Hosting Bundle
- NSSM (Windows Service Manager)
- URL Rewrite module

**NOTE**: You may need to restart the server after this step.

## Step 3: Deploy the Application

### Option A: Deploy Everything at Once

```powershell
.\deploy-all.ps1
```

### Option B: Deploy Components Individually

```powershell
# 1. Deploy WhatsApp Service (must be first)
.\deploy-whatsapp-service.ps1

# 2. Deploy Backend API
.\deploy-backend.ps1

# 3. Deploy Frontend
.\deploy-frontend.ps1
```

## Step 4: Configure Security

### Generate Encryption Keys

```powershell
cd C:\whatsappbridge\tools
.\generate-encryption-keys.ps1
```

This will output encryption keys. **Save these securely!**

### Update Configuration

1. Open the configuration file:

```powershell
notepad C:\inetpub\whatsappbridge-api\appsettings.Production.json
```

2. Update the encryption settings:

```json
{
  "Encryption": {
    "Enabled": true,
    "Key": "your-generated-key-here",
    "IV": "your-generated-iv-here"
  }
}
```

3. Update allowed origins if using a custom domain:

```json
{
  "AllowedOrigins": [
    "http://yourdomain.com",
    "https://yourdomain.com"
  ]
}
```

4. Restart IIS:

```powershell
iisreset
```

## Step 5: Configure Firewall

```powershell
# Allow HTTP (port 80)
New-NetFirewallRule -DisplayName "WhatsApp Bridge Web" -Direction Inbound -Protocol TCP -LocalPort 80 -Action Allow

# Allow HTTPS (port 443) - if using SSL
New-NetFirewallRule -DisplayName "WhatsApp Bridge Web SSL" -Direction Inbound -Protocol TCP -LocalPort 443 -Action Allow

# Allow API (port 5000)
New-NetFirewallRule -DisplayName "WhatsApp Bridge API" -Direction Inbound -Protocol TCP -LocalPort 5000 -Action Allow
```

## Step 6: Configure SSL (Recommended)

### Using Let's Encrypt with Certify The Web

1. Download and install [Certify The Web](https://certifytheweb.com/)

2. Create SSL certificate for your domain

3. In IIS Manager:
   - Select "WhatsAppBridgeWeb" site
   - Bindings → Add → HTTPS
   - Select your SSL certificate

4. Update frontend deployment to use HTTPS:

```powershell
.\deploy-frontend.ps1 -Port 443 -ApiUrl "https://yourdomain.com:5000"
```

## Step 7: Test the Deployment

1. **Check WhatsApp Service**

```powershell
# Check service status
Get-Service WhatsAppBridgeService

# Check service logs
Get-Content C:\Services\WhatsAppBridge\service.log -Tail 20
```

2. **Check Backend API**

Open browser: `http://your-server:5000/swagger`

3. **Check Frontend**

Open browser: `http://your-server`

## Step 8: Create First User

1. Navigate to `http://your-server`
2. Click "Register"
3. Create your admin account

## Step 9: Monitor and Maintain

### Service Logs

```powershell
# WhatsApp Service logs
Get-Content C:\Services\WhatsAppBridge\service.log -Tail 50 -Wait

# IIS logs (Backend API)
Get-Content C:\inetpub\logs\LogFiles\W3SVC*\*.log -Tail 50

# Windows Event Viewer
eventvwr.msc
```

### Restart Services

```powershell
# Restart WhatsApp Service
Restart-Service WhatsAppBridgeService

# Restart IIS
iisreset

# Restart specific site
Restart-WebAppPool -Name "WhatsAppBridgeAPIAppPool"
```

### Backup Database

```powershell
# Database is located at:
# C:\inetpub\whatsappbridge-api\whatsappbridge.db

# Create backup
Copy-Item "C:\inetpub\whatsappbridge-api\whatsappbridge.db" `
          "C:\Backups\whatsappbridge-$(Get-Date -Format 'yyyy-MM-dd').db"
```

### Update Application

```powershell
# Pull latest changes
cd C:\whatsappbridge
git pull

# Redeploy
cd deploy
.\deploy-all.ps1 -SkipPrerequisites
```

## Troubleshooting

### Issue: WhatsApp Service won't start

**Solution:**

```powershell
# Check logs
Get-Content C:\Services\WhatsAppBridge\service-error.log

# Common fixes:
# 1. Ensure Node.js is in PATH
# 2. Check port 3000 is not in use
netstat -ano | findstr :3000

# 3. Reinstall service
cd C:\whatsappbridge\deploy
.\deploy-whatsapp-service.ps1
```

### Issue: Backend API returns 500 errors

**Solution:**

```powershell
# Enable detailed errors
# Edit appsettings.Production.json
# Add: "ASPNETCORE_ENVIRONMENT": "Development"

# Check database permissions
icacls C:\inetpub\whatsappbridge-api\whatsappbridge.db /grant "IIS_IUSRS:(M)"

# Restart IIS
iisreset
```

### Issue: Frontend shows blank page

**Solution:**

```powershell
# Check API URL in browser console (F12)
# Ensure CORS is configured correctly

# Rebuild and redeploy frontend
cd C:\whatsappbridge\deploy
.\deploy-frontend.ps1 -ApiUrl "http://your-actual-api-url:5000"
```

### Issue: QR Code doesn't appear

**Solution:**

```powershell
# Check WhatsApp Service is running
Get-Service WhatsAppBridgeService

# Check service can reach the API
Invoke-WebRequest http://localhost:3000/health

# Restart service
Restart-Service WhatsAppBridgeService
```

## Performance Tuning

### IIS Application Pool Settings

```powershell
# Increase memory limit
Set-ItemProperty "IIS:\AppPools\WhatsAppBridgeAPIAppPool" -Name "recycling.periodicRestart.memory" -Value 1048576

# Disable idle timeout
Set-ItemProperty "IIS:\AppPools\WhatsAppBridgeAPIAppPool" -Name "processModel.idleTimeout" -Value "00:00:00"

# Enable 32-bit applications if needed
Set-ItemProperty "IIS:\AppPools\WhatsAppBridgeAPIAppPool" -Name "enable32BitAppOnWin64" -Value $true
```

### Database Optimization

```powershell
# SQLite vacuum (compress database)
sqlite3 C:\inetpub\whatsappbridge-api\whatsappbridge.db "VACUUM;"

# Create indexes
sqlite3 C:\inetpub\whatsappbridge-api\whatsappbridge.db "CREATE INDEX IF NOT EXISTS idx_sessions_userid ON WhatsAppSessions(UserId);"
```

## Security Checklist

- [ ] Encryption enabled in appsettings.Production.json
- [ ] Strong JWT key generated and configured
- [ ] SSL certificate installed and configured
- [ ] Firewall rules configured
- [ ] Database backups scheduled
- [ ] Service logs monitored
- [ ] Windows updates enabled
- [ ] Administrator password is strong
- [ ] Remote Desktop limited to specific IPs (if possible)

## Support

For issues not covered here, check:
1. Service logs: `C:\Services\WhatsAppBridge\service.log`
2. IIS logs: `C:\inetpub\logs\LogFiles\W3SVC*\`
3. Windows Event Viewer
4. GitHub issues

## Next Steps

After successful deployment:
1. Create API connections
2. Connect WhatsApp by scanning QR code
3. Test API endpoints
4. Integrate with your applications
5. Monitor usage and performance
