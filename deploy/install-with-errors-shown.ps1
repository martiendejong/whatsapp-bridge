#Requires -RunAsAdministrator

Write-Host "WhatsApp Bridge Installation" -ForegroundColor Cyan
Write-Host "=" * 70

# Step 1: WhatsApp Service
Write-Host "`n[1/4] WhatsApp Service..." -ForegroundColor Yellow
$servicePath = "C:\Services\WhatsAppBridge"
New-Item -ItemType Directory -Path $servicePath -Force | Out-Null
Copy-Item "C:\whatsappbridge\WhatsAppService\*" -Destination $servicePath -Recurse -Force
Set-Location $servicePath
npm install --production
if ($LASTEXITCODE -ne 0) {
    Write-Host "  [WARN] npm install had warnings" -ForegroundColor Yellow
}

@"
@echo off
cd /d C:\Services\WhatsAppBridge
node index.js
"@ | Out-File -FilePath "$servicePath\start.bat" -Encoding ASCII

$taskName = "WhatsAppBridgeService"
$existing = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if ($existing) { Unregister-ScheduledTask -TaskName $taskName -Confirm:$false }

$action = New-ScheduledTaskAction -Execute "cmd.exe" -Argument "/c C:\Services\WhatsAppBridge\start.bat" -WorkingDirectory "C:\Services\WhatsAppBridge"
$trigger = New-ScheduledTaskTrigger -AtStartup
$principal = New-ScheduledTaskPrincipal -UserID "NT AUTHORITY\SYSTEM" -LogonType ServiceAccount -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings | Out-Null
Start-ScheduledTask -TaskName $taskName
Write-Host "  [OK] WhatsApp Service started" -ForegroundColor Green

# Step 2: Backend API
Write-Host "`n[2/4] Backend API..." -ForegroundColor Yellow
$apiPath = "C:\inetpub\whatsappbridge-api"

if (-not (Test-Path "C:\whatsappbridge\Backend\WhatsAppBridge.API\WhatsAppBridge.API.csproj")) {
    Write-Host "  [FAIL] Backend project file not found!" -ForegroundColor Red
    Write-Host "  Path: C:\whatsappbridge\Backend\WhatsAppBridge.API\" -ForegroundColor Yellow
    ls "C:\whatsappbridge\Backend" -ErrorAction SilentlyContinue
} else {
    # Create output directory first
    New-Item -ItemType Directory -Path $apiPath -Force | Out-Null

    Set-Location "C:\whatsappbridge\Backend\WhatsAppBridge.API"
    Write-Host "  Publishing (this may take 1-2 minutes)..." -ForegroundColor Cyan
    dotnet publish -c Release -o $apiPath

    if ($LASTEXITCODE -eq 0) {
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

        Write-Host "  [OK] Backend API deployed on port 5000" -ForegroundColor Green
    } else {
        Write-Host "  [FAIL] dotnet publish failed (see errors above)" -ForegroundColor Red
    }
}

# Step 3: Frontend
Write-Host "`n[3/4] Frontend..." -ForegroundColor Yellow
if (-not (Test-Path "C:\whatsappbridge\Frontend\package.json")) {
    Write-Host "  [FAIL] Frontend package.json not found!" -ForegroundColor Red
} else {
    Set-Location "C:\whatsappbridge\Frontend"
    "VITE_API_URL=http://whatsapp.wreckingball.ai:5000" | Out-File -FilePath ".env.production" -Encoding UTF8

    Write-Host "  Installing npm packages..." -ForegroundColor Cyan
    npm install

    Write-Host "  Building frontend..." -ForegroundColor Cyan
    npm run build

    if (Test-Path "dist") {
        $webPath = "C:\inetpub\whatsappbridge-web"
        New-Item -ItemType Directory -Path $webPath -Force | Out-Null
        Copy-Item "dist\*" -Destination $webPath -Recurse -Force

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

        Import-Module WebAdministration
        $webAppPoolName = "WhatsAppBridgeWebPool"
        if (Test-Path "IIS:\AppPools\$webAppPoolName") { Remove-WebAppPool -Name $webAppPoolName }
        New-WebAppPool -Name $webAppPoolName | Out-Null

        $webSiteName = "WhatsAppBridgeWeb"
        if (Test-Path "IIS:\Sites\$webSiteName") { Remove-WebSite -Name $webSiteName }
        New-WebSite -Name $webSiteName -PhysicalPath $webPath -Port 80 -ApplicationPool $webAppPoolName -HostHeader "whatsapp.wreckingball.ai" | Out-Null

        Write-Host "  [OK] Frontend deployed on port 80" -ForegroundColor Green
    } else {
        Write-Host "  [FAIL] Frontend build failed (no dist folder)" -ForegroundColor Red
        Write-Host "  Check npm build output above for errors" -ForegroundColor Yellow
    }
}

# Step 4: IIS Restart
Write-Host "`n[4/4] Restarting IIS..." -ForegroundColor Yellow
iisreset /noforce | Out-Null

Write-Host "`n" + ("=" * 70) -ForegroundColor Green
Write-Host "INSTALLATION COMPLETE!" -ForegroundColor Green
Write-Host ("=" * 70) -ForegroundColor Green
Write-Host "`nSite: http://whatsapp.wreckingball.ai"
Write-Host "API:  http://whatsapp.wreckingball.ai:5000/swagger"
