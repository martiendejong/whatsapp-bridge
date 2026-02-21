#Requires -RunAsAdministrator

Write-Host "WhatsApp Bridge Installation (without NSSM)" -ForegroundColor Cyan
Write-Host "=" * 70

# Step 1: WhatsApp Service via Task Scheduler
Write-Host "`n[1/4] Setting up WhatsApp Service..." -ForegroundColor Yellow

$servicePath = "C:\Services\WhatsAppBridge"
New-Item -ItemType Directory -Path $servicePath -Force | Out-Null
Copy-Item "C:\whatsappbridge\WhatsAppService\*" -Destination $servicePath -Recurse -Force

Set-Location $servicePath
npm install --production 2>&1 | Out-Null

# Create startup script
@"
@echo off
cd /d C:\Services\WhatsAppBridge
node index.js
"@ | Out-File -FilePath "$servicePath\start.bat" -Encoding ASCII

# Create scheduled task
$action = New-ScheduledTaskAction -Execute "cmd.exe" -Argument "/c C:\Services\WhatsAppBridge\start.bat" -WorkingDirectory "C:\Services\WhatsAppBridge"
$trigger = New-ScheduledTaskTrigger -AtStartup
$principal = New-ScheduledTaskPrincipal -UserID "NT AUTHORITY\SYSTEM" -LogonType ServiceAccount -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable

$taskName = "WhatsAppBridgeService"
$existing = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if ($existing) {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
}

Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings | Out-Null
Start-ScheduledTask -TaskName $taskName

Write-Host "  [OK] WhatsApp Service started" -ForegroundColor Green

# Step 2: Backend API
Write-Host "`n[2/4] Deploying Backend API..." -ForegroundColor Yellow

# Check if we have the backend files
if (-not (Test-Path "C:\whatsappbridge\Backend\WhatsAppBridge.API")) {
    Write-Host "  [WARN] Backend files missing, skipping..." -ForegroundColor Yellow
} else {
    $apiPath = "C:\inetpub\whatsappbridge-api"

    Set-Location "C:\whatsappbridge\Backend\WhatsAppBridge.API"
    dotnet publish -c Release -o $apiPath 2>&1 | Out-Null

    # Create config
    $config = @{
        ConnectionStrings = @{ DefaultConnection = "Data Source=$apiPath\whatsappbridge.db" }
        Jwt = @{
            Key = "wreckingball-prod-$(New-Guid)"
            Issuer = "WhatsAppBridge"
            Audience = "WhatsAppBridgeUsers"
        }
        WhatsAppService = @{ Url = "http://localhost:3000" }
        AllowedOrigins = @("https://whatsapp.wreckingball.ai", "http://whatsapp.wreckingball.ai")
        Encryption = @{ Enabled = $false; Key = ""; IV = "" }
    }
    $config | ConvertTo-Json -Depth 10 | Out-File -FilePath "$apiPath\appsettings.Production.json" -Encoding UTF8

    # IIS
    Import-Module WebAdministration

    $appPoolName = "WhatsAppBridgeAPIPool"
    if (Test-Path "IIS:\AppPools\$appPoolName") { Remove-WebAppPool -Name $appPoolName }
    New-WebAppPool -Name $appPoolName | Out-Null
    Set-ItemProperty "IIS:\AppPools\$appPoolName" -Name "managedRuntimeVersion" -Value ""

    $siteName = "WhatsAppBridgeAPI"
    if (Test-Path "IIS:\Sites\$siteName") { Remove-WebSite -Name $siteName }
    New-WebSite -Name $siteName -PhysicalPath $apiPath -Port 5000 -ApplicationPool $appPoolName | Out-Null

    $acl = Get-Acl $apiPath
    $rule = New-Object System.Security.AccessControl.FileSystemAccessRule("IIS_IUSRS", "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow")
    $acl.SetAccessRule($rule)
    Set-Acl $apiPath $acl

    Write-Host "  [OK] Backend API deployed" -ForegroundColor Green
}

# Step 3: Frontend
Write-Host "`n[3/4] Deploying Frontend..." -ForegroundColor Yellow

if (-not (Test-Path "C:\whatsappbridge\Frontend\package.json")) {
    Write-Host "  [WARN] Frontend files missing, skipping..." -ForegroundColor Yellow
} else {
    Set-Location "C:\whatsappbridge\Frontend"

    "VITE_API_URL=http://whatsapp.wreckingball.ai:5000" | Out-File -FilePath ".env.production" -Encoding UTF8

    npm install 2>&1 | Out-Null
    npm run build 2>&1 | Out-Null

    if (Test-Path "dist") {
        $webPath = "C:\inetpub\whatsappbridge-web"
        New-Item -ItemType Directory -Path $webPath -Force | Out-Null
        Copy-Item "dist\*" -Destination $webPath -Recurse -Force

        # web.config
        @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <system.webServer>
    <rewrite>
      <rules>
        <rule name="React Routes" stopProcessing="true">
          <match url=".*" />
          <conditions logicalGrouping="MatchAll">
            <add input="{REQUEST_FILENAME}" matchType="IsFile" negate="true" />
            <add input="{REQUEST_FILENAME}" matchType="IsDirectory" negate="true" />
          </conditions>
          <action type="Rewrite" url="/" />
        </rule>
      </rules>
    </rewrite>
  </system.webServer>
</configuration>
"@ | Out-File -FilePath "$webPath\web.config" -Encoding UTF8

        # IIS
        Import-Module WebAdministration

        $webAppPoolName = "WhatsAppBridgeWebPool"
        if (Test-Path "IIS:\AppPools\$webAppPoolName") { Remove-WebAppPool -Name $webAppPoolName }
        New-WebAppPool -Name $webAppPoolName | Out-Null

        $webSiteName = "WhatsAppBridgeWeb"
        if (Test-Path "IIS:\Sites\$webSiteName") { Remove-WebSite -Name $webSiteName }
        New-WebSite -Name $webSiteName -PhysicalPath $webPath -Port 80 -ApplicationPool $webAppPoolName -HostHeader "whatsapp.wreckingball.ai" | Out-Null

        Write-Host "  [OK] Frontend deployed" -ForegroundColor Green
    } else {
        Write-Host "  [FAIL] Frontend build failed (no dist folder)" -ForegroundColor Red
    }
}

# Step 4: IIS Restart
Write-Host "`n[4/4] Restarting IIS..." -ForegroundColor Yellow
iisreset /noforce 2>&1 | Out-Null

Start-Sleep -Seconds 3

Write-Host "`n" + ("=" * 70) -ForegroundColor Green
Write-Host "INSTALLATION COMPLETE!" -ForegroundColor Green
Write-Host ("=" * 70) -ForegroundColor Green
Write-Host "`nWhatsApp Service: Task Scheduler (WhatsAppBridgeService)"
Write-Host "Site: http://whatsapp.wreckingball.ai"
Write-Host "API:  http://whatsapp.wreckingball.ai:5000/swagger"
