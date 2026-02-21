# WhatsApp Bridge - Productie Deployment Advies

**Repository:** https://github.com/martiendejong/whatsappbridge
**Server:** 85.215.217.154 (whatsapp.wreckingball.ai)
**Datum:** 21 februari 2026

---

## 📊 Huidige Status

### ✅ Werkend
- **Frontend:** Live op http://whatsapp.wreckingball.ai
- **DNS:** Correct geconfigureerd en gepropageerd
- **IIS:** Draait en geconfigureerd
- **Source code:** Op server aanwezig in `C:\whatsappbridge`

### ❌ Niet Werkend
- **Backend API** (port 5000): NuGet package restore faalt
- **WhatsApp Service** (port 3000): NPM package install faalt

---

## 🔴 Kernprobleem: Geen Internet Access voor Package Managers

**Symptomen:**
```
error NU1100: Unable to resolve 'BCrypt.Net-Next (>= 4.1.0)' for 'net8.0'
error NU1100: Unable to resolve 'Microsoft.AspNetCore.Authentication.JwtBearer'
npm error ENOENT: no such file or directory, open 'package.json'
```

**Oorzaak:**
Server kan niet verbinden met package repositories (NuGet.org, NPMjs.org)

---

## 💡 Oplossingsopties

### Optie 1: Firewall/Netwerk Oplossen (BESTE OPTIE)

**Voordelen:**
- ✅ Schoonste oplossing
- ✅ Makkelijke updates in de toekomst
- ✅ Standard deployment workflow werkt

**Stappen:**

1. **Test internetverbinding op server:**
   ```powershell
   # Inloggen via SSH
   ssh administrator@85.215.217.154

   # Test connectiviteit
   Test-NetConnection api.nuget.org -Port 443
   Test-NetConnection registry.npmjs.org -Port 443
   Test-NetConnection github.com -Port 443
   ```

2. **Als tests falen, firewall openen voor:**
   - `api.nuget.org` (HTTPS:443)
   - `nuget.org` (HTTPS:443)
   - `registry.npmjs.org` (HTTPS:443)
   - `github.com` (HTTPS:443) - voor win-acme (SSL)

3. **Deployment opnieuw runnen:**
   ```bash
   cd E:\projects\whatsappbridge\deploy
   python run-final-install.py
   ```

4. **Verificatie:**
   ```bash
   python detailed-check.py
   ```

**Tijd:** 30 minuten (als je firewall access hebt)

---

### Optie 2: Offline Deployment (WORKAROUND)

**Voordelen:**
- ✅ Werkt zonder internet op server
- ✅ Voorgecompileerde binaries zijn betrouwbaarder

**Nadelen:**
- ❌ Updates vereisen lokale compile + upload
- ❌ Meer handmatig werk

**Stappen:**

#### 2A. Backend Offline Deployment

1. **Lokaal publishen (op development machine):**
   ```bash
   cd E:\projects\whatsappbridge\Backend\WhatsAppBridge.API
   dotnet publish -c Release -o E:\temp\backend-published
   ```

2. **Upload naar server:**
   ```python
   # Script: upload-compiled-backend.py
   import paramiko

   ssh = paramiko.SSHClient()
   ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
   ssh.connect('85.215.217.154', username='administrator', password='[VAULT]')

   # Upload hele directory
   # (script aanwezig in deploy folder)
   ```

3. **IIS configureren op server:**
   ```powershell
   # Zie: deploy/configure-backend-iis.ps1
   ```

#### 2B. WhatsApp Service Offline Deployment

1. **Lokaal dependencies installeren:**
   ```bash
   cd E:\projects\whatsappbridge\WhatsAppService
   npm install
   ```

2. **Hele node_modules uploaden:**
   ```bash
   # Dit is GROOT (~200MB), maar werkt zonder internet
   tar -czf whatsapp-service-full.tar.gz *
   # Upload via SFTP
   ```

3. **Service configureren:**
   ```powershell
   # Scheduled Task al aangemaakt
   # Alleen node_modules extracten
   ```

**Tijd:** 2-3 uur (eerste keer), 30 min (updates)

---

### Optie 3: Hybride Deployment (COMPROMIS)

**Backend:** Offline (compiled binaries)
**WhatsApp Service:** Offline (with node_modules)
**Frontend:** Al werkend

**Voordelen:**
- ✅ Werkt meteen
- ✅ Minder afhankelijk van server internet

**Nadelen:**
- ❌ Deployment complexer
- ❌ Upload tijd langer

**Tijd:** 1-2 uur

---

### Optie 4: Cloud Deployment i.p.v. VPS (ARCHITECTUUR WIJZIGING)

**Alternatief:** Deploy naar Azure/AWS in plaats van eigen VPS

**Voordelen:**
- ✅ Geen firewall problemen
- ✅ Auto-scaling
- ✅ Managed services
- ✅ SSL automatisch

**Nadelen:**
- ❌ Maandelijkse kosten (~$50-100)
- ❌ Vendor lock-in
- ❌ Complete re-deployment

**Platforms:**
- **Azure App Service:** ASP.NET native support
- **AWS Elastic Beanstalk:** Multi-container support
- **DigitalOcean App Platform:** Eenvoudigste setup

**Tijd:** 4-6 uur (eerste keer)

---

## 🎯 Aanbevolen Aanpak

### Korte Termijn (NU)

**Optie 2: Offline Deployment**
- Reden: Je hebt nu geen tijd/toegang om firewall in te regelen
- Werk meteen, geen blokkades

**Actie:**
1. Lokaal backend publishen
2. Upload compiled binaries (script klaar)
3. WhatsApp Service met node_modules uploaden
4. Configureren + testen

**Scripts gereed:**
- `deploy/offline-backend-deploy.ps1` (nieuw aan te maken)
- `deploy/offline-service-deploy.ps1` (nieuw aan te maken)

### Lange Termijn (BETER)

**Optie 1: Firewall Oplossen**
- Reden: Proper solution, makkelijke updates
- Voor productie is dit de schoonste oplossing

**Vraag aan hosting provider:**
- "Kan ik outbound HTTPS (port 443) naar api.nuget.org en registry.npmjs.org enablen?"
- Of: "Heeft de VPS een proxy waar ik packages doorheen kan halen?"

---

## 📋 Actielijst voor Optie 2 (Offline - AANBEVOLEN NU)

### Stap 1: Backend Offline Deployment (15 min)

```bash
# 1. Lokaal publishen
cd E:\projects\whatsappbridge\Backend\WhatsAppBridge.API
dotnet publish -c Release -o E:\temp\backend-published

# 2. Config genereren
# (script maakt appsettings.Production.json)

# 3. Upload
python deploy/upload-compiled-backend.py

# 4. IIS configureren op server
# (script doet dit automatisch)
```

### Stap 2: WhatsApp Service Offline (20 min)

```bash
# 1. Lokaal npm install
cd E:\projects\whatsappbridge\WhatsAppService
npm install

# 2. Tar maken (inclusief node_modules)
tar -czf whatsapp-service-complete.tar.gz index.js package.json node_modules/

# 3. Upload + extract
python deploy/upload-complete-service.py
```

### Stap 3: SSL Certificate (10 min)

```bash
# Na backend/service werken
cd E:\projects\whatsappbridge\deploy
python install-ssl-remote.py
```

### Stap 4: Verificatie (5 min)

```bash
# Check alle services
python final-status-check.py

# Test endpoints
curl http://whatsapp.wreckingball.ai
curl http://whatsapp.wreckingball.ai:5000/swagger
curl http://whatsapp.wreckingball.ai:3000/health
```

**Totale tijd:** ~50 minuten

---

## 🛠️ Scripts Die Ik Nog Moet Maken

Als je voor Optie 2 gaat, maak ik deze scripts:

1. **upload-compiled-backend.py**
   - Upload E:\temp\backend-published naar C:\inetpub\whatsappbridge-api
   - Configureer IIS
   - Set permissions

2. **upload-complete-service.py**
   - Upload complete service (met node_modules)
   - Extract op server
   - Start scheduled task

3. **offline-deployment-guide.md**
   - Stap-voor-stap guide
   - Troubleshooting

**Zeg het als je deze wilt, maak ik ze nu.**

---

## ⚠️ Belangrijke Aandachtspunten

### Security

**Momenteel:**
- ✅ Private GitHub repo
- ✅ Wachtwoorden NIET in code
- ❌ SSL certificate nog niet geïnstalleerd (HTTP only)
- ❌ API tokens in appsettings.json (server-side, OK-ish)

**Voor productie:**
1. **SSL VERPLICHT** - installeren zodra backend werkt
2. **Secrets in environment variables** - niet in appsettings.json
3. **Firewall regels** - alleen 80/443 open naar buiten

### Performance

**Huidige setup:**
- Frontend: Static files (snel)
- Backend: Single-instance ASP.NET
- WhatsApp Service: Single Node.js process

**Voor schalen (later):**
- Load balancer
- Multiple backend instances
- Redis voor session sharing

### Monitoring

**Wat je zou moeten monitoren:**
- IIS Application Pool crashes
- WhatsApp Service scheduled task failures
- Disk space (logs kunnen groeien)
- SSL certificate expiry (Let's Encrypt = 90 dagen)

**Tools:**
- Windows Event Viewer
- IIS logs
- Custom health check script (kan ik maken)

---

## 💰 Kosten Overzicht

### Huidige VPS Aanpak
- **VPS:** Je hebt al (€0 extra)
- **Domain:** Je hebt al (€0 extra)
- **SSL:** Gratis (Let's Encrypt)
- **Totaal:** €0/maand extra

### Cloud Alternatief (ter vergelijking)
- **Azure App Service:** ~$55/maand (B1 tier)
- **AWS Elastic Beanstalk:** ~$50/maand
- **DigitalOcean:** ~$12/maand (basic tier)

**Conclusie:** VPS is veel goedkoper, dus waard om werkend te krijgen.

---

## 📞 Volgende Stap

**Jouw keuze:**

1. **Optie 1 proberen** - Check firewall/internet op server eerst
2. **Optie 2 uitvoeren** - Offline deployment nu (ik maak scripts)
3. **Optie 4 overwegen** - Cloud deployment i.p.v. VPS

**Mijn advies:** Start met **Optie 2** (offline), dan later Optie 1 (firewall) als je toegang krijgt.

Zeg maar wat je wilt doen, dan pak ik het aan.
