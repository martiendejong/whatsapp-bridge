#Requires -RunAsAdministrator

<#
.SYNOPSIS
    Deploy WhatsApp Bridge to whatsapp.wreckingball.ai
#>

param(
    [string]$Domain = "whatsapp.wreckingball.ai",
    [string]$DeployPath = "C:\inetpub\whatsappbridge"
)

$ErrorActionPreference = "Stop"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "WhatsApp Bridge - Wreckingball.ai Setup" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Get server IP for DNS record
$serverIP = (Test-Connection -ComputerName $env:COMPUTERNAME -Count 1).IPV4Address.IPAddressToString
Write-Host "Server IP: $serverIP" -ForegroundColor Yellow
Write-Host ""

# Deploy WhatsApp Service
Write-Host "[1/3] Deploying WhatsApp Service..." -ForegroundColor Yellow
& "$PSScriptRoot\deploy-whatsapp-service.ps1" -DeployPath "C:\Services\WhatsAppBridge" -Port 3000

# Deploy Backend API
Write-Host "`n[2/3] Deploying Backend API..." -ForegroundColor Yellow
& "$PSScriptRoot\deploy-backend.ps1" -DeployPath "$DeployPath\api" -SiteName "WhatsAppBridgeAPI" -Port 5000

# Update API settings for production domain
$apiSettings = @"
{
  "ConnectionStrings": {
    "DefaultConnection": "Data Source=$DeployPath\api\whatsappbridge.db"
  },
  "Jwt": {
    "Key": "$(New-Guid)-$(New-Guid)-wreckingball-secure-key",
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
$apiSettings | Out-File -FilePath "$DeployPath\api\appsettings.Production.json" -Encoding UTF8

# Deploy Frontend
Write-Host "`n[3/3] Deploying Frontend..." -ForegroundColor Yellow
& "$PSScriptRoot\deploy-frontend.ps1" `
    -DeployPath "$DeployPath\web" `
    -SiteName "WhatsAppBridgeWeb" `
    -Port 80 `
    -ApiUrl "https://$Domain/api"

# Configure IIS for subdomain
Write-Host "`nConfiguring IIS for $Domain..." -ForegroundColor Yellow
Import-Module WebAdministration

# Update API site binding
Remove-WebBinding -Name "WhatsAppBridgeAPI" -Port 5000 -ErrorAction SilentlyContinue
New-WebBinding -Name "WhatsAppBridgeAPI" -Protocol http -Port 5000 -HostHeader $Domain

# Update Web site binding
Remove-WebBinding -Name "WhatsAppBridgeWeb" -Port 80 -ErrorAction SilentlyContinue
New-WebBinding -Name "WhatsAppBridgeWeb" -Protocol http -Port 80 -HostHeader $Domain

# Configure reverse proxy for /api path
$webConfig = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <system.webServer>
    <rewrite>
      <rules>
        <rule name="API Reverse Proxy" stopProcessing="true">
          <match url="^api/(.*)" />
          <action type="Rewrite" url="http://localhost:5000/api/{R:1}" />
        </rule>
        <rule name="React Routes" stopProcessing="true">
          <match url=".*" />
          <conditions logicalGrouping="MatchAll">
            <add input="{REQUEST_FILENAME}" matchType="IsFile" negate="true" />
            <add input="{REQUEST_FILENAME}" matchType="IsDirectory" negate="true" />
            <add input="{REQUEST_URI}" pattern="^/api/" negate="true" />
          </conditions>
          <action type="Rewrite" url="/" />
        </rule>
      </rules>
    </rewrite>
    <staticContent>
      <mimeMap fileExtension=".json" mimeType="application/json" />
    </staticContent>
  </system.webServer>
</configuration>
"@
$webConfig | Out-File -FilePath "$DeployPath\web\web.config" -Encoding UTF8

# Restart IIS
iisreset /noforce

Write-Host "`n========================================" -ForegroundColor Green
Write-Host "Deployment Complete!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host ""
Write-Host "DNS Configuration Required:" -ForegroundColor Cyan
Write-Host "===========================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Add this A record to wreckingball.ai DNS:" -ForegroundColor Yellow
Write-Host ""
Write-Host "Type:  A" -ForegroundColor White
Write-Host "Name:  whatsapp" -ForegroundColor White
Write-Host "Value: $serverIP" -ForegroundColor White
Write-Host "TTL:   3600 (or default)" -ForegroundColor White
Write-Host ""
Write-Host "After DNS propagation, access your application at:" -ForegroundColor Cyan
Write-Host "http://$Domain" -ForegroundColor White
Write-Host ""
Write-Host "Next Steps:" -ForegroundColor Cyan
Write-Host "1. Add DNS record (see above)" -ForegroundColor White
Write-Host "2. Wait for DNS propagation (5-30 minutes)" -ForegroundColor White
Write-Host "3. Test: http://$Domain" -ForegroundColor White
Write-Host "4. Install SSL certificate (recommended)" -ForegroundColor White
Write-Host "5. Generate encryption keys: C:\whatsappbridge\tools\generate-encryption-keys.ps1" -ForegroundColor White
Write-Host ""
