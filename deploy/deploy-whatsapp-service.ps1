#Requires -RunAsAdministrator

param(
    [string]$DeployPath = "C:\Services\WhatsAppBridge",
    [string]$ServiceName = "WhatsAppBridgeService",
    [int]$Port = 3000
)

Write-Host "Deploying WhatsApp Service..." -ForegroundColor Green

# Create deployment directory
if (-not (Test-Path $DeployPath)) {
    New-Item -ItemType Directory -Path $DeployPath | Out-Null
}

# Copy WhatsApp Service files
Write-Host "Copying service files..." -ForegroundColor Yellow
$servicePath = Split-Path $PSScriptRoot -Parent | Join-Path -ChildPath "WhatsAppService"
Copy-Item "$servicePath\*" -Destination $DeployPath -Recurse -Force

# Install Node.js dependencies
Write-Host "Installing dependencies..." -ForegroundColor Yellow
Set-Location $DeployPath
npm install --production

# Remove existing service if it exists
$existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existingService) {
    Write-Host "Stopping and removing existing service..." -ForegroundColor Yellow
    nssm stop $ServiceName
    nssm remove $ServiceName confirm
    Start-Sleep -Seconds 2
}

# Install as Windows service using NSSM
Write-Host "Installing Windows service..." -ForegroundColor Yellow
$nodePath = (Get-Command node).Source
$serviceScript = "$DeployPath\index.js"

nssm install $ServiceName $nodePath $serviceScript
nssm set $ServiceName AppDirectory $DeployPath
nssm set $ServiceName AppEnvironmentExtra "PORT=$Port"
nssm set $ServiceName DisplayName "WhatsApp Bridge Service"
nssm set $ServiceName Description "WhatsApp Web integration service for WhatsApp Bridge"
nssm set $ServiceName Start SERVICE_AUTO_START
nssm set $ServiceName AppStdout "$DeployPath\service.log"
nssm set $ServiceName AppStderr "$DeployPath\service-error.log"

# Start the service
Write-Host "Starting service..." -ForegroundColor Yellow
nssm start $ServiceName

# Wait for service to start
Start-Sleep -Seconds 3

# Check service status
$service = Get-Service -Name $ServiceName
if ($service.Status -eq "Running") {
    Write-Host "`nWhatsApp Service deployment complete!" -ForegroundColor Green
    Write-Host "Service Status: Running" -ForegroundColor Cyan
    Write-Host "Service URL: http://localhost:$Port" -ForegroundColor Cyan
    Write-Host "Logs: $DeployPath\service.log" -ForegroundColor Cyan
} else {
    Write-Host "`nService failed to start. Check logs at: $DeployPath\service-error.log" -ForegroundColor Red
}
