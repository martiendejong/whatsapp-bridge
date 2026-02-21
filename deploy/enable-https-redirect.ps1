#Requires -RunAsAdministrator

<#
.SYNOPSIS
    Enable automatic HTTPS redirect in IIS
.DESCRIPTION
    Configures URL Rewrite rules to redirect all HTTP traffic to HTTPS
#>

param(
    [string]$Domain = "whatsapp.wreckingball.ai"
)

$ErrorActionPreference = "Stop"

Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "Enable HTTPS Redirect" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host ""

# Install URL Rewrite module if not present
Write-Host "[1/3] Checking URL Rewrite module..." -ForegroundColor Yellow

$rewriteModule = Get-WindowsFeature -Name Web-Http-Redirect -ErrorAction SilentlyContinue
if (-not $rewriteModule -or -not $rewriteModule.Installed) {
    Write-Host "  Installing URL Rewrite module..."
    Install-WindowsFeature -Name Web-Http-Redirect -IncludeManagementTools | Out-Null
    Write-Host "  URL Rewrite installed" -ForegroundColor Green
} else {
    Write-Host "  URL Rewrite already installed" -ForegroundColor Green
}

# Configure web.config for frontend
Write-Host "`n[2/3] Configuring HTTPS redirect for frontend..." -ForegroundColor Yellow

$webPath = "C:\inetpub\whatsappbridge-web"
$webConfigPath = Join-Path $webPath "web.config"

$webConfig = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <system.webServer>
    <rewrite>
      <rules>
        <!-- HTTPS Redirect -->
        <rule name="Force HTTPS" stopProcessing="true">
          <match url="(.*)" />
          <conditions>
            <add input="{HTTPS}" pattern="off" />
          </conditions>
          <action type="Redirect" url="https://{HTTP_HOST}/{R:1}" redirectType="Permanent" />
        </rule>

        <!-- React Routes -->
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

    <!-- Security Headers -->
    <httpProtocol>
      <customHeaders>
        <add name="Strict-Transport-Security" value="max-age=31536000; includeSubDomains" />
        <add name="X-Content-Type-Options" value="nosniff" />
        <add name="X-Frame-Options" value="SAMEORIGIN" />
        <add name="X-XSS-Protection" value="1; mode=block" />
      </customHeaders>
    </httpProtocol>
  </system.webServer>
</configuration>
"@

$webConfig | Out-File -FilePath $webConfigPath -Encoding UTF8 -Force
Write-Host "  web.config updated with HTTPS redirect" -ForegroundColor Green

# Configure API for HTTPS
Write-Host "`n[3/3] Updating API configuration..." -ForegroundColor Yellow

$apiPath = "C:\inetpub\whatsappbridge-api"
$appSettingsPath = Join-Path $apiPath "appsettings.Production.json"

if (Test-Path $appSettingsPath) {
    $appSettings = Get-Content $appSettingsPath -Raw | ConvertFrom-Json

    # Update allowed origins to use HTTPS
    if ($appSettings.AllowedOrigins) {
        $appSettings.AllowedOrigins = @(
            "https://$Domain"
        )
    }

    $appSettings | ConvertTo-Json -Depth 10 | Out-File -FilePath $appSettingsPath -Encoding UTF8
    Write-Host "  API configured for HTTPS origins" -ForegroundColor Green
}

# Restart IIS
Write-Host "`n  Restarting IIS..."
iisreset /noforce | Out-Null

Write-Host "`n============================================================" -ForegroundColor Green
Write-Host "HTTPS Redirect Enabled!" -ForegroundColor Green
Write-Host "============================================================" -ForegroundColor Green
Write-Host ""
Write-Host "All HTTP traffic will now be redirected to HTTPS:" -ForegroundColor Cyan
Write-Host "  http://$Domain  ->  https://$Domain" -ForegroundColor White
Write-Host ""
Write-Host "Security headers added:" -ForegroundColor Cyan
Write-Host "  - Strict-Transport-Security (HSTS)" -ForegroundColor White
Write-Host "  - X-Content-Type-Options" -ForegroundColor White
Write-Host "  - X-Frame-Options" -ForegroundColor White
Write-Host "  - X-XSS-Protection" -ForegroundColor White
Write-Host ""
