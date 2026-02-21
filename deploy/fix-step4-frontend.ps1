#Requires -RunAsAdministrator

Write-Host "Step 4: Deploying Frontend..." -ForegroundColor Cyan

$webPath = "C:\inetpub\whatsappbridge-web"

try {
    # Build frontend
    Write-Host "  Building React frontend..."
    Set-Location "C:\whatsappbridge\Frontend"

    # Create .env.production
    "VITE_API_URL=http://whatsapp.wreckingball.ai:5000" | Out-File -FilePath ".env.production" -Encoding UTF8

    # Install and build
    npm install 2>&1 | Out-Null
    npm run build 2>&1 | Out-Null

    # Deploy to IIS
    Write-Host "  Deploying to IIS..."
    New-Item -ItemType Directory -Path $webPath -Force | Out-Null
    Copy-Item "dist\*" -Destination $webPath -Recurse -Force

    # Create web.config for React Router
    Write-Host "  Creating web.config..."
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

    # Configure IIS
    Write-Host "  Configuring IIS..."
    Import-Module WebAdministration

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

    Write-Host "[OK] Frontend deployed on port 80" -ForegroundColor Green
    exit 0
} catch {
    Write-Host "[FAIL] Frontend deployment failed: $_" -ForegroundColor Red
    exit 1
}
