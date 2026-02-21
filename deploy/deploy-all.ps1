#Requires -RunAsAdministrator

<#
.SYNOPSIS
    Complete deployment script for WhatsApp Bridge application
.DESCRIPTION
    Deploys the backend API, WhatsApp service, and frontend to a Windows VPS
.PARAMETER SkipPrerequisites
    Skip prerequisites installation
#>

param(
    [switch]$SkipPrerequisites
)

$ErrorActionPreference = "Stop"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "WhatsApp Bridge - Complete Deployment" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Get deployment directory
$deployDir = $PSScriptRoot

# Install prerequisites
if (-not $SkipPrerequisites) {
    Write-Host "`n[1/4] Installing prerequisites..." -ForegroundColor Yellow
    & "$deployDir\install-prerequisites.ps1"
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Prerequisites installation failed!" -ForegroundColor Red
        exit 1
    }
} else {
    Write-Host "`n[1/4] Skipping prerequisites..." -ForegroundColor Yellow
}

# Deploy WhatsApp Service
Write-Host "`n[2/4] Deploying WhatsApp Service..." -ForegroundColor Yellow
& "$deployDir\deploy-whatsapp-service.ps1"
if ($LASTEXITCODE -ne 0) {
    Write-Host "WhatsApp Service deployment failed!" -ForegroundColor Red
    exit 1
}

# Deploy Backend API
Write-Host "`n[3/4] Deploying Backend API..." -ForegroundColor Yellow
& "$deployDir\deploy-backend.ps1"
if ($LASTEXITCODE -ne 0) {
    Write-Host "Backend deployment failed!" -ForegroundColor Red
    exit 1
}

# Deploy Frontend
Write-Host "`n[4/4] Deploying Frontend..." -ForegroundColor Yellow
& "$deployDir\deploy-frontend.ps1"
if ($LASTEXITCODE -ne 0) {
    Write-Host "Frontend deployment failed!" -ForegroundColor Red
    exit 1
}

# Final summary
Write-Host "`n========================================" -ForegroundColor Green
Write-Host "Deployment Complete!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host ""
Write-Host "Services:" -ForegroundColor Cyan
Write-Host "  - Backend API: http://localhost:5000" -ForegroundColor White
Write-Host "  - WhatsApp Service: http://localhost:3000" -ForegroundColor White
Write-Host "  - Frontend: http://localhost" -ForegroundColor White
Write-Host ""
Write-Host "Next Steps:" -ForegroundColor Cyan
Write-Host "  1. Configure encryption in appsettings.Production.json (optional but recommended)" -ForegroundColor White
Write-Host "  2. Open http://localhost in your browser" -ForegroundColor White
Write-Host "  3. Register a new account" -ForegroundColor White
Write-Host "  4. Create an API connection" -ForegroundColor White
Write-Host "  5. Connect your WhatsApp by scanning the QR code" -ForegroundColor White
Write-Host ""
Write-Host "Configuration Files:" -ForegroundColor Cyan
Write-Host "  - Backend: C:\inetpub\whatsappbridge-api\appsettings.Production.json" -ForegroundColor White
Write-Host "  - Service Logs: C:\Services\WhatsAppBridge\service.log" -ForegroundColor White
Write-Host ""
