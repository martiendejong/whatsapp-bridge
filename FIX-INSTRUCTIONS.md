# Quick Fix: Empty appsettings.Production.json

## Problem
The backend fails to start in IIS with no error logs because `appsettings.Production.json` is empty (0 bytes).

## Solution
Replace the empty file with valid JSON:

```bash
# Navigate to deployment directory
cd C:\inetpub\whatsappbridge-api

# Create/replace appsettings.Production.json with this content:
```

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Warning",
      "Microsoft.AspNetCore": "Warning",
      "Microsoft.EntityFrameworkCore": "Warning"
    }
  }
}
```

## Verification
Test that the application can start:

```powershell
cd C:\inetpub\whatsappbridge-api
$env:ASPNETCORE_ENVIRONMENT = "Production"
dotnet WhatsAppBridge.API.dll
```

You should see:
- Entity Framework initialization
- Database connection successful
- HTTP pipeline starting
- May fail at port binding (normal if port is in use)

## Restart IIS
After fixing the file:

```powershell
# Method 1: Using IIS Manager GUI
# Stop and start the WhatsAppBridge-API app pool

# Method 2: Using appcmd
C:\Windows\System32\inetsrv\appcmd.exe stop apppool WhatsAppBridge-API
C:\Windows\System32\inetsrv\appcmd.exe start apppool WhatsAppBridge-API

# Method 3: Using PowerShell with WebAdministration module
Import-Module WebAdministration
Restart-WebAppPool -Name "WhatsAppBridge-API"
```

## Check if Working
```bash
# Check if port 19470 is listening
netstat -ano | findstr 19470

# Test HTTP connection
curl http://localhost:19470/
```

## Status
✓ FIXED - 2026-03-09
