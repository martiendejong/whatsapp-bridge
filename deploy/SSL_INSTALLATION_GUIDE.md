# SSL Certificate Installation Guide

## Prerequisites

✅ DNS A record added and propagated (whatsapp -> 85.215.217.154)
✅ Port 80 accessible from internet (for Let's Encrypt validation)
✅ WhatsApp Bridge application installed and running

---

## Option 1: Automatic Installation (Recommended)

### From Your Local Machine

Run this Python script after DNS is live:

```bash
cd E:\projects\whatsappbridge\deploy
python install-ssl-remote.py
```

This will:
1. Connect to the server via SSH
2. Verify DNS propagation
3. Upload the SSL installation script
4. Execute the installation automatically
5. Configure HTTPS bindings for web (443) and API (5001)

**Duration:** 2-3 minutes

---

## Option 2: Manual Installation on Server

### Step 1: Verify DNS

On the server, open PowerShell as Administrator and verify DNS:

```powershell
nslookup whatsapp.wreckingball.ai
# Should return: 85.215.217.154
```

If DNS hasn't propagated yet, wait 5-30 minutes and check again.

### Step 2: Run SSL Installation Script

```powershell
cd C:\whatsappbridge\deploy
.\install-ssl-certificate.ps1 -Domain "whatsapp.wreckingball.ai" -Email "martien@wreckingball.ai"
```

The script will:
- Download win-acme (Let's Encrypt client)
- Verify DNS configuration
- Request certificate from Let's Encrypt
- Install certificate in Windows certificate store
- Create HTTPS bindings in IIS (ports 443 and 5001)
- Set up automatic renewal (every 60 days)

### Step 3: Enable HTTPS Redirect (Optional but Recommended)

Force all HTTP traffic to HTTPS:

```powershell
.\enable-https-redirect.ps1 -Domain "whatsapp.wreckingball.ai"
```

---

## What Gets Installed

### Let's Encrypt Certificate

- **Issuer:** Let's Encrypt
- **Validity:** 90 days
- **Auto-Renewal:** Yes (every 60 days via Windows Task Scheduler)
- **Storage:** Windows Certificate Store (LocalMachine\My)

### HTTPS Bindings

- **Web Application:** Port 443 (https://whatsapp.wreckingball.ai)
- **API:** Port 5001 (https://whatsapp.wreckingball.ai:5001)

### Security Headers

Automatically added to responses:
- `Strict-Transport-Security: max-age=31536000` (HSTS)
- `X-Content-Type-Options: nosniff`
- `X-Frame-Options: SAMEORIGIN`
- `X-XSS-Protection: 1; mode=block`

---

## Testing SSL Installation

### 1. Web Application

Visit: https://whatsapp.wreckingball.ai

You should see:
- ✅ Padlock icon in browser
- ✅ Valid certificate from Let's Encrypt
- ✅ No security warnings

### 2. API

Visit: https://whatsapp.wreckingball.ai:5001/swagger

### 3. HTTP Redirect (if enabled)

Visit: http://whatsapp.wreckingball.ai

Should automatically redirect to: https://whatsapp.wreckingball.ai

### 4. SSL Labs Test

For comprehensive SSL security analysis:

https://www.ssllabs.com/ssltest/analyze.html?d=whatsapp.wreckingball.ai

Expected grade: A or A+

---

## Updating Frontend to Use HTTPS API

After SSL is installed, update the frontend to use HTTPS for API calls:

### On the Server

```powershell
cd C:\whatsappbridge\Frontend

# Update .env.production
"VITE_API_URL=https://whatsapp.wreckingball.ai:5001" | Out-File -FilePath ".env.production" -Encoding UTF8

# Rebuild frontend
npm run build

# Copy to IIS
Copy-Item "dist\*" -Destination "C:\inetpub\whatsappbridge-web" -Recurse -Force
```

---

## Certificate Renewal

### Automatic Renewal

win-acme creates a Windows Task Scheduler task that runs daily to check if renewal is needed (certificates are renewed when they have 30 days left).

### Manual Renewal

If needed, you can manually renew:

```powershell
cd C:\Tools\win-acme
.\wacs.exe --renew --baseuri "https://acme-v02.api.letsencrypt.org/"
```

### Check Renewal Status

```powershell
cd C:\Tools\win-acme
.\wacs.exe --list
```

---

## Troubleshooting

### DNS Not Propagating

**Problem:** `nslookup whatsapp.wreckingball.ai` doesn't return 85.215.217.154

**Solution:**
- Wait 5-30 minutes for DNS propagation
- Check DNS settings at your domain registrar
- Use `nslookup whatsapp.wreckingball.ai 8.8.8.8` to check Google's DNS

### Port 80 Not Accessible

**Problem:** Let's Encrypt validation fails with "Cannot reach domain on port 80"

**Solution:**
- Check Windows Firewall: Allow inbound port 80
- Check IIS: Website binding exists for port 80
- Check router/cloud firewall: Port 80 open to internet

```powershell
# Check firewall rule
Get-NetFirewallRule -DisplayName "*HTTP*" | Where-Object { $_.Enabled -eq $true }

# Add firewall rule if missing
New-NetFirewallRule -DisplayName "HTTP (Port 80)" -Direction Inbound -Protocol TCP -LocalPort 80 -Action Allow
```

### Certificate Not Binding to IIS

**Problem:** HTTPS doesn't work after installation

**Solution:**
Manually bind certificate:

```powershell
# Get certificate thumbprint
$cert = Get-ChildItem -Path Cert:\LocalMachine\My | Where-Object { $_.Subject -like "*whatsapp.wreckingball.ai*" }
$thumbprint = $cert.Thumbprint

# Bind to web site
Import-Module WebAdministration
$binding = Get-WebBinding -Name "WhatsAppBridgeWeb" -Protocol https -Port 443
$binding.AddSslCertificate($thumbprint, "my")

# Restart IIS
iisreset
```

### Mixed Content Warnings

**Problem:** Browser shows "mixed content" warnings

**Solution:**
Ensure frontend is using HTTPS for all API calls. Update `VITE_API_URL` to use `https://` and rebuild frontend.

---

## Security Best Practices

### After SSL Installation

1. ✅ **Enable HSTS** (done automatically by enable-https-redirect.ps1)
2. ✅ **Force HTTPS Redirect** (run enable-https-redirect.ps1)
3. ⚠️ **Disable TLS 1.0 and 1.1** (optional, may break older clients)
4. ⚠️ **Enable Certificate Transparency Monitoring**

### Optional: Disable Old TLS Versions

```powershell
# Disable TLS 1.0
New-Item 'HKLM:\SYSTEM\CurrentControlSet\Control\SecurityProviders\SCHANNEL\Protocols\TLS 1.0\Server' -Force
New-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\SecurityProviders\SCHANNEL\Protocols\TLS 1.0\Server' -Name 'Enabled' -Value 0 -PropertyType 'DWORD'

# Disable TLS 1.1
New-Item 'HKLM:\SYSTEM\CurrentControlSet\Control\SecurityProviders\SCHANNEL\Protocols\TLS 1.1\Server' -Force
New-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\SecurityProviders\SCHANNEL\Protocols\TLS 1.1\Server' -Name 'Enabled' -Value 0 -PropertyType 'DWORD'

# Restart required
Restart-Computer
```

---

## Files Created

All SSL-related scripts are in `E:\projects\whatsappbridge\deploy\`:

- **install-ssl-certificate.ps1** - Main SSL installation script (run on server)
- **install-ssl-remote.py** - Remote execution wrapper (run from local machine)
- **enable-https-redirect.ps1** - Force HTTPS redirect + security headers
- **SSL_INSTALLATION_GUIDE.md** - This guide

---

## Summary

**Quick Start (after DNS is live):**

```bash
# From local machine
cd E:\projects\whatsappbridge\deploy
python install-ssl-remote.py
```

**Result:**
- ✅ Free SSL certificate from Let's Encrypt
- ✅ HTTPS on ports 443 (web) and 5001 (API)
- ✅ Automatic renewal every 60 days
- ✅ Production-ready security configuration

**URLs after SSL:**
- https://whatsapp.wreckingball.ai (web)
- https://whatsapp.wreckingball.ai:5001/swagger (API)
