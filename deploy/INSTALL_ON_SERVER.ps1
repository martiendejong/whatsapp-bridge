#Requires -RunAsAdministrator

<#
.SYNOPSIS
    Complete WhatsApp Bridge installation for whatsapp.wreckingball.ai
.DESCRIPTION
    Run this script directly on your Windows server to install WhatsApp Bridge
#>

param(
    [string]$Domain = "whatsapp.wreckingball.ai"
)

$ErrorActionPreference = "Stop"

Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "WhatsApp Bridge - Server Installation" -ForegroundColor Cyan
Write-Host "Domain: $Domain" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host ""

# Step 1: Download project from local machine or repository
Write-Host "[1/6] Downloading WhatsApp Bridge..." -ForegroundColor Yellow

# Check if running on wreckingball server
if (Test-Path "E:\projects\whatsappbridge") {
    Write-Host "  Copying from E:\projects\whatsappbridge..."
    Copy-Item -Path "E:\projects\whatsappbridge" -Destination "C:\whatsappbridge" -Recurse -Force
} else {
    Write-Host "  Creating project directory..."
    New-Item -ItemType Directory -Path "C:\whatsappbridge" -Force | Out-Null

    # Download via GitHub or create minimal version
    Write-Host "  Please upload the WhatsApp Bridge files to C:\whatsappbridge"
    Write-Host "  Or copy from E:\projects\whatsappbridge if available"
    Read-Host "Press Enter when files are ready..."
}

# Step 2: Install prerequisites
Write-Host "`n[2/6] Installing prerequisites..." -ForegroundColor Yellow

# .NET 9
if (-not (dotnet --version 2>$null)) {
    Write-Host "  Installing .NET 9 SDK..."
    Invoke-WebRequest -Uri "https://dotnet.microsoft.com/download/dotnet/scripts/v1/dotnet-install.ps1" -OutFile "$env:TEMP\dotnet-install.ps1"
    & "$env:TEMP\dotnet-install.ps1" -Channel 9.0
}

# Node.js
if (-not (node --version 2>$null)) {
    Write-Host "  Node.js not found. Please install from https://nodejs.org"
    Read-Host "Press Enter after installing Node.js..."
}

# IIS
Write-Host "  Installing IIS..."
Install-WindowsFeature -Name Web-Server -IncludeManagementTools | Out-Null

# NSSM
if (-not (Test-Path "C:\Windows\System32\nssm.exe")) {
    Write-Host "  Installing NSSM..."
    Invoke-WebRequest -Uri "https://nssm.cc/release/nssm-2.24.zip" -OutFile "$env:TEMP\nssm.zip"
    Expand-Archive -Path "$env:TEMP\nssm.zip" -DestinationPath "$env:TEMP\nssm" -Force
    Copy-Item "$env:TEMP\nssm\nssm-2.24\win64\nssm.exe" "C:\Windows\System32\" -Force
}

# Step 3: Deploy WhatsApp Service
Write-Host "`n[3/6] Deploying WhatsApp Service..." -ForegroundColor Yellow

$servicePath = "C:\Services\WhatsAppBridge"
New-Item -ItemType Directory -Path $servicePath -Force | Out-Null
Copy-Item "C:\whatsappbridge\WhatsAppService\*" -Destination $servicePath -Recurse -Force

Set-Location $servicePath
npm install --production

# Install as Windows service
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

Write-Host "  WhatsApp Service installed and running"

# Step 4: Deploy Backend API
Write-Host "`n[4/6] Deploying Backend API..." -ForegroundColor Yellow

$apiPath = "C:\inetpub\whatsappbridge-api"
Set-Location "C:\whatsappbridge\Backend\WhatsAppBridge.API"
dotnet publish -c Release -o $apiPath

# Create production config
$productionConfig = @"
{
  "ConnectionStrings": {
    "DefaultConnection": "Data Source=$apiPath\whatsappbridge.db"
  },
  "Jwt": {
    "Key": "$(New-Guid)-$(New-Guid)-wreckingball-production-key-secure",
    "Issuer": "WhatsAppBridge",
    "Audience": "WhatsAppBridgeUsers"
  },
  "WhatsAppService": {
    "Url": "http://localhost:3000"
  },
  "AllowedOrigins": [
    "https://$Domain",
    "http://$Domain"
  ],
  "Encryption": {
    "Enabled": false,
    "Key": "",
    "IV": ""
  }
}
"@
$productionConfig | Out-File -FilePath "$apiPath\appsettings.Production.json" -Encoding UTF8

# Create IIS App Pool and Site
Import-Module WebAdministration

$appPoolName = "WhatsAppBridgeAPIPool"
if (Test-Path "IIS:\AppPools\$appPoolName") {
    Remove-WebAppPool -Name $appPoolName
}
New-WebAppPool -Name $appPoolName
Set-ItemProperty "IIS:\AppPools\$appPoolName" -Name "managedRuntimeVersion" -Value ""

$siteName = "WhatsAppBridgeAPI"
if (Test-Path "IIS:\Sites\$siteName") {
    Remove-WebSite -Name $siteName
}
New-WebSite -Name $siteName -PhysicalPath $apiPath -Port 5000 -ApplicationPool $appPoolName

# Set permissions
$acl = Get-Acl $apiPath
$rule = New-Object System.Security.AccessControl.FileSystemAccessRule("IIS_IUSRS", "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow")
$acl.SetAccessRule($rule)
Set-Acl $apiPath $acl

Write-Host "  Backend API deployed on port 5000"

# Step 5: Deploy Frontend
Write-Host "`n[5/6] Deploying Frontend..." -ForegroundColor Yellow

Set-Location "C:\whatsappbridge\Frontend"

# Create .env.production
"VITE_API_URL=http://$Domain:5000" | Out-File -FilePath ".env.production" -Encoding UTF8

npm install
npm run build

$webPath = "C:\inetpub\whatsappbridge-web"
New-Item -ItemType Directory -Path $webPath -Force | Out-Null
Copy-Item "dist\*" -Destination $webPath -Recurse -Force

# Create web.config
$webConfig = @"
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
"@
$webConfig | Out-File -FilePath "$webPath\web.config" -Encoding UTF8

# Create IIS Site
$webAppPoolName = "WhatsAppBridgeWebPool"
if (Test-Path "IIS:\AppPools\$webAppPoolName") {
    Remove-WebAppPool -Name $webAppPoolName
}
New-WebAppPool -Name $webAppPoolName

$webSiteName = "WhatsAppBridgeWeb"
if (Test-Path "IIS:\Sites\$webSiteName") {
    Remove-WebSite -Name $webSiteName
}
New-WebSite -Name $webSiteName -PhysicalPath $webPath -Port 80 -ApplicationPool $webAppPoolName -HostHeader $Domain

Write-Host "  Frontend deployed on port 80"

# Step 6: Final configuration
Write-Host "`n[6/6] Final configuration..." -ForegroundColor Yellow

# Restart IIS
iisreset /noforce

# Get server IP
$serverIP = (Test-Connection -ComputerName $env:COMPUTERNAME -Count 1).IPV4Address.IPAddressToString

Write-Host "`n============================================================" -ForegroundColor Green
Write-Host "Installation Complete!" -ForegroundColor Green
Write-Host "============================================================" -ForegroundColor Green
Write-Host ""
Write-Host "DNS Configuration:" -ForegroundColor Cyan
Write-Host "==================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Add this A record to wreckingball.ai DNS:" -ForegroundColor Yellow
Write-Host ""
Write-Host "  Type:  A" -ForegroundColor White
Write-Host "  Name:  whatsapp" -ForegroundColor White
Write-Host "  Value: $serverIP" -ForegroundColor White
Write-Host "  TTL:   3600" -ForegroundColor White
Write-Host ""
Write-Host "After DNS propagation:" -ForegroundColor Cyan
Write-Host "  Website: http://$Domain" -ForegroundColor White
Write-Host "  API:     http://$Domain:5000/swagger" -ForegroundColor White
Write-Host ""
Write-Host "Services Running:" -ForegroundColor Cyan
Write-Host "  WhatsApp Service: " -NoNewline -ForegroundColor White
(Get-Service WhatsAppBridgeService).Status
Write-Host "  IIS Sites: " -NoNewline -ForegroundColor White
(Get-Website | Where-Object { $_.Name -like "*WhatsAppBridge*" }).Count
Write-Host " sites running" -ForegroundColor White
Write-Host ""
