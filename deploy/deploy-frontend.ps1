#Requires -RunAsAdministrator

param(
    [string]$DeployPath = "C:\inetpub\whatsappbridge-web",
    [string]$SiteName = "WhatsAppBridgeWeb",
    [int]$Port = 80,
    [string]$ApiUrl = "http://localhost:5000"
)

Write-Host "Deploying WhatsApp Bridge Frontend..." -ForegroundColor Green

# Build the frontend
Write-Host "Building frontend..." -ForegroundColor Yellow
$frontendPath = Split-Path $PSScriptRoot -Parent | Join-Path -ChildPath "Frontend"
Set-Location $frontendPath

# Create .env.production
$envProduction = "VITE_API_URL=$ApiUrl"
$envProduction | Out-File -FilePath ".env.production" -Encoding UTF8

npm install
npm run build

# Create deployment directory
if (-not (Test-Path $DeployPath)) {
    New-Item -ItemType Directory -Path $DeployPath | Out-Null
}

# Copy build files
Write-Host "Copying build files..." -ForegroundColor Yellow
Copy-Item "$frontendPath\dist\*" -Destination $DeployPath -Recurse -Force

# Create web.config for IIS
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
    <staticContent>
      <mimeMap fileExtension=".json" mimeType="application/json" />
    </staticContent>
  </system.webServer>
</configuration>
"@

$webConfig | Out-File -FilePath "$DeployPath\web.config" -Encoding UTF8

# Create IIS App Pool
Write-Host "Creating IIS App Pool..." -ForegroundColor Yellow
Import-Module WebAdministration

$appPoolName = "${SiteName}AppPool"
if (Test-Path "IIS:\AppPools\$appPoolName") {
    Remove-WebAppPool -Name $appPoolName
}

New-WebAppPool -Name $appPoolName
Set-ItemProperty "IIS:\AppPools\$appPoolName" -Name "managedRuntimeVersion" -Value ""

# Create IIS Site
Write-Host "Creating IIS Site..." -ForegroundColor Yellow
if (Test-Path "IIS:\Sites\$SiteName") {
    Remove-WebSite -Name $SiteName
}

New-WebSite -Name $SiteName -PhysicalPath $DeployPath -Port $Port -ApplicationPool $appPoolName

# Install URL Rewrite module if not present
$urlRewriteInstalled = Get-WindowsFeature -Name "IIS-URLRewrite" -ErrorAction SilentlyContinue
if (-not $urlRewriteInstalled -or $urlRewriteInstalled.InstallState -ne "Installed") {
    Write-Host "Installing URL Rewrite module..." -ForegroundColor Yellow
    $urlRewriteUrl = "https://download.microsoft.com/download/1/2/8/128E2E22-C1B9-44A4-BE2A-5859ED1D4592/rewrite_amd64_en-US.msi"
    $urlRewriteInstaller = "$env:TEMP\urlrewrite.msi"
    Invoke-WebRequest -Uri $urlRewriteUrl -OutFile $urlRewriteInstaller
    Start-Process -FilePath "msiexec.exe" -ArgumentList "/i", $urlRewriteInstaller, "/quiet", "/norestart" -Wait
    Remove-Item $urlRewriteInstaller
}

# Start the site
Start-WebSite -Name $SiteName

Write-Host "`nFrontend deployment complete!" -ForegroundColor Green
Write-Host "Web URL: http://localhost:$Port" -ForegroundColor Cyan
