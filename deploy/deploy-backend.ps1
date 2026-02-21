#Requires -RunAsAdministrator

param(
    [string]$DeployPath = "C:\inetpub\whatsappbridge-api",
    [string]$SiteName = "WhatsAppBridgeAPI",
    [int]$Port = 5000
)

Write-Host "Deploying WhatsApp Bridge Backend..." -ForegroundColor Green

# Build the backend
Write-Host "Building backend..." -ForegroundColor Yellow
$backendPath = Split-Path $PSScriptRoot -Parent | Join-Path -ChildPath "Backend\WhatsAppBridge.API"
Set-Location $backendPath
dotnet publish -c Release -o "$DeployPath"

# Create appsettings.Production.json if it doesn't exist
$productionSettings = @"
{
  "ConnectionStrings": {
    "DefaultConnection": "Data Source=$DeployPath\whatsappbridge.db"
  },
  "Jwt": {
    "Key": "$(New-Guid)-$(New-Guid)",
    "Issuer": "WhatsAppBridge",
    "Audience": "WhatsAppBridgeUsers"
  },
  "WhatsAppService": {
    "Url": "http://localhost:3000"
  },
  "AllowedOrigins": [
    "http://localhost",
    "https://localhost"
  ],
  "Encryption": {
    "Enabled": false,
    "Key": "",
    "IV": ""
  }
}
"@

$productionSettingsPath = "$DeployPath\appsettings.Production.json"
if (-not (Test-Path $productionSettingsPath)) {
    $productionSettings | Out-File -FilePath $productionSettingsPath -Encoding UTF8
    Write-Host "Created appsettings.Production.json - IMPORTANT: Update Encryption settings if needed!" -ForegroundColor Yellow
}

# Create IIS App Pool
Write-Host "Creating IIS App Pool..." -ForegroundColor Yellow
Import-Module WebAdministration

$appPoolName = "${SiteName}AppPool"
if (Test-Path "IIS:\AppPools\$appPoolName") {
    Remove-WebAppPool -Name $appPoolName
}

New-WebAppPool -Name $appPoolName
Set-ItemProperty "IIS:\AppPools\$appPoolName" -Name "managedRuntimeVersion" -Value ""
Set-ItemProperty "IIS:\AppPools\$appPoolName" -Name "enable32BitAppOnWin64" -Value $false

# Create IIS Site
Write-Host "Creating IIS Site..." -ForegroundColor Yellow
if (Test-Path "IIS:\Sites\$SiteName") {
    Remove-WebSite -Name $SiteName
}

New-WebSite -Name $SiteName -PhysicalPath $DeployPath -Port $Port -ApplicationPool $appPoolName

# Set permissions
Write-Host "Setting permissions..." -ForegroundColor Yellow
$acl = Get-Acl $DeployPath
$rule = New-Object System.Security.AccessControl.FileSystemAccessRule("IIS_IUSRS", "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow")
$acl.SetAccessRule($rule)
Set-Acl $DeployPath $acl

# Start the site
Start-WebSite -Name $SiteName

Write-Host "`nBackend deployment complete!" -ForegroundColor Green
Write-Host "API URL: http://localhost:$Port" -ForegroundColor Cyan
Write-Host "Swagger: http://localhost:$Port/swagger" -ForegroundColor Cyan
