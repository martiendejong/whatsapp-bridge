#Requires -RunAsAdministrator

Write-Host "WhatsApp Bridge - Direct Installation" -ForegroundColor Cyan
Write-Host "=====================================" -ForegroundColor Cyan

# Stap 1: NSSM
Write-Host "`n[1/5] NSSM installeren..." -ForegroundColor Yellow
if (-not (Test-Path "C:\Windows\System32\nssm.exe")) {
    Invoke-WebRequest -Uri "https://nssm.cc/release/nssm-2.24.zip" -OutFile "$env:TEMP\nssm.zip"
    Expand-Archive -Path "$env:TEMP\nssm.zip" -DestinationPath "$env:TEMP\nssm" -Force
    Copy-Item "$env:TEMP\nssm\nssm-2.24\win64\nssm.exe" "C:\Windows\System32\" -Force
    Write-Host "  NSSM geinstalleerd"
} else {
    Write-Host "  NSSM al geinstalleerd"
}

# Stap 2: WhatsApp Service
Write-Host "`n[2/5] WhatsApp Service..." -ForegroundColor Yellow
$servicePath = "C:\Services\WhatsAppBridge"
New-Item -ItemType Directory -Path $servicePath -Force | Out-Null
Copy-Item "C:\whatsappbridge\WhatsAppService\*" -Destination $servicePath -Recurse -Force

Set-Location $servicePath
npm install --production

$existingService = Get-Service -Name "WhatsAppBridgeService" -ErrorAction SilentlyContinue
if ($existingService) {
    nssm stop WhatsAppBridgeService
    nssm remove WhatsAppBridgeService confirm
}

$nodePath = (Get-Command node).Source
nssm install WhatsAppBridgeService $nodePath "$servicePath\index.js"
nssm set WhatsAppBridgeService AppDirectory $servicePath
nssm set WhatsAppBridgeService AppEnvironmentExtra "PORT=3000"
nssm start WhatsAppBridgeService

Write-Host "  WhatsApp Service gestart"

# Stap 3: Backend API
Write-Host "`n[3/5] Backend API..." -ForegroundColor Yellow
$apiPath = "C:\inetpub\whatsappbridge-api"
Set-Location "C:\whatsappbridge\Backend\WhatsAppBridge.API"
dotnet publish -c Release -o $apiPath

$config = @{
    ConnectionStrings = @{
        DefaultConnection = "Data Source=$apiPath\whatsappbridge.db"
    }
    Jwt = @{
        Key = "wreckingball-prod-$(New-Guid)"
        Issuer = "WhatsAppBridge"
        Audience = "WhatsAppBridgeUsers"
    }
    WhatsAppService = @{
        Url = "http://localhost:3000"
    }
    AllowedOrigins = @(
        "https://whatsapp.wreckingball.ai",
        "http://whatsapp.wreckingball.ai"
    )
    Encryption = @{
        Enabled = $false
        Key = ""
        IV = ""
    }
}
$config | ConvertTo-Json -Depth 10 | Out-File -FilePath "$apiPath\appsettings.Production.json" -Encoding UTF8

Import-Module WebAdministration
$appPoolName = "WhatsAppBridgeAPIPool"
if (Test-Path "IIS:\AppPools\$appPoolName") {
    Remove-WebAppPool -Name $appPoolName
}
New-WebAppPool -Name $appPoolName | Out-Null
Set-ItemProperty "IIS:\AppPools\$appPoolName" -Name "managedRuntimeVersion" -Value ""

$siteName = "WhatsAppBridgeAPI"
if (Test-Path "IIS:\Sites\$siteName") {
    Remove-WebSite -Name $siteName
}
New-WebSite -Name $siteName -PhysicalPath $apiPath -Port 5000 -ApplicationPool $appPoolName | Out-Null

$acl = Get-Acl $apiPath
$rule = New-Object System.Security.AccessControl.FileSystemAccessRule("IIS_IUSRS", "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow")
$acl.SetAccessRule($rule)
Set-Acl $apiPath $acl

Write-Host "  Backend API deployed"

# Stap 4: Frontend
Write-Host "`n[4/5] Frontend..." -ForegroundColor Yellow
Set-Location "C:\whatsappbridge\Frontend"
"VITE_API_URL=http://whatsapp.wreckingball.ai:5000" | Out-File -FilePath ".env.production" -Encoding UTF8
npm install
npm run build

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

$webAppPoolName = "WhatsAppBridgeWebPool"
if (Test-Path "IIS:\AppPools\$webAppPoolName") {
    Remove-WebAppPool -Name $webAppPoolName
}
New-WebAppPool -Name $webAppPoolName | Out-Null

$webSiteName = "WhatsAppBridgeWeb"
if (Test-Path "IIS:\Sites\$webSiteName") {
    Remove-WebSite -Name $webSiteName
}
New-WebSite -Name $webSiteName -PhysicalPath $webPath -Port 80 -ApplicationPool $webAppPoolName -HostHeader "whatsapp.wreckingball.ai" | Out-Null

Write-Host "  Frontend deployed"

# Stap 5: IIS restart
Write-Host "`n[5/5] IIS herstarten..." -ForegroundColor Yellow
iisreset /noforce

Write-Host "`n=====================================" -ForegroundColor Green
Write-Host "INSTALLATIE COMPLEET!" -ForegroundColor Green
Write-Host "=====================================" -ForegroundColor Green
Write-Host "`nSite: http://whatsapp.wreckingball.ai"
Write-Host "API:  http://whatsapp.wreckingball.ai:5000/swagger"
