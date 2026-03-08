#!/usr/bin/env python3
"""Complete production-ready WhatsApp Service deployment"""

import paramiko
import time
import sys

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"

DEPLOYMENT_SCRIPT = r"""
# Complete WhatsApp Service Deployment
Write-Host "======================================================================" -ForegroundColor Cyan
Write-Host "WhatsApp Service - Production Deployment" -ForegroundColor Cyan
Write-Host "======================================================================" -ForegroundColor Cyan
Write-Host ""

$serviceDir = "C:\Services\WhatsAppBridge"
$serviceName = "WhatsAppBridgeService"

# Step 1: Install NSSM (using Chocolatey as fallback)
Write-Host "Step 1: Installing NSSM..." -ForegroundColor Yellow

# Try Chocolatey first
$choco = Get-Command choco -ErrorAction SilentlyContinue
if ($choco) {
    Write-Host "  Installing via Chocolatey..."
    choco install nssm -y --force
} else {
    # Install Chocolatey first
    Write-Host "  Installing Chocolatey package manager..."
    Set-ExecutionPolicy Bypass -Scope Process -Force
    [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.ServicePointManager]::SecurityProtocol -bor 3072
    Invoke-Expression ((New-Object System.Net.WebClient).DownloadString('https://community.chocolatey.org/install.ps1'))

    # Refresh PATH
    $env:Path = [System.Environment]::GetEnvironmentVariable("Path","Machine") + ";" + [System.Environment]::GetEnvironmentVariable("Path","User")

    Write-Host "  Installing NSSM via Chocolatey..."
    choco install nssm -y --force
}

# Verify NSSM
$nssmPath = (Get-Command nssm -ErrorAction SilentlyContinue).Source
if ($nssmPath) {
    Write-Host "  NSSM installed: $nssmPath" -ForegroundColor Green
} else {
    Write-Host "  ERROR: NSSM installation failed!" -ForegroundColor Red
    exit 1
}

# Step 2: Install npm dependencies
Write-Host ""
Write-Host "Step 2: Installing npm dependencies..." -ForegroundColor Yellow

Push-Location $serviceDir

# Clean install
if (Test-Path "node_modules") {
    Write-Host "  Removing old node_modules..."
    Remove-Item -Path "node_modules" -Recurse -Force
}

Write-Host "  Running npm install..."
npm install --production

# Install Chromium
Write-Host "  Installing Chromium for Puppeteer..."
npx puppeteer browsers install chrome

Pop-Location

Write-Host "  Dependencies installed" -ForegroundColor Green

# Step 3: Remove old service if exists
Write-Host ""
Write-Host "Step 3: Cleaning up old service..." -ForegroundColor Yellow

$existingService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existingService) {
    Write-Host "  Stopping existing service..."
    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2

    Write-Host "  Removing existing service..."
    & nssm remove $serviceName confirm
}

# Kill any running node processes
Get-Process node -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "*nodejs*" } | Stop-Process -Force

Write-Host "  Cleanup complete" -ForegroundColor Green

# Step 4: Install service with NSSM
Write-Host ""
Write-Host "Step 4: Installing Windows Service..." -ForegroundColor Yellow

$nodeExe = "C:\Program Files\nodejs\node.exe"
$appJs = Join-Path $serviceDir "index.js"
$logDir = Join-Path $serviceDir "logs"

# Create logs directory
New-Item -ItemType Directory -Path $logDir -Force | Out-Null

# Install service
& nssm install $serviceName $nodeExe $appJs

# Configure service
& nssm set $serviceName AppDirectory $serviceDir
& nssm set $serviceName DisplayName "WhatsApp Bridge Service"
& nssm set $serviceName Description "WhatsApp Web.js service for WhatsApp Bridge API"
& nssm set $serviceName Start SERVICE_AUTO_START
& nssm set $serviceName AppStdout "$logDir\service.log"
& nssm set $serviceName AppStderr "$logDir\service-error.log"
& nssm set $serviceName AppRotateFiles 1
& nssm set $serviceName AppRotateBytes 10485760  # 10MB
& nssm set $serviceName AppExit Default Restart
& nssm set $serviceName AppRestartDelay 5000  # 5 seconds

Write-Host "  Service installed" -ForegroundColor Green

# Step 5: Start service
Write-Host ""
Write-Host "Step 5: Starting service..." -ForegroundColor Yellow

Start-Service -Name $serviceName

Start-Sleep -Seconds 5

# Check status
$service = Get-Service -Name $serviceName
Write-Host "  Service status: $($service.Status)" -ForegroundColor $(if ($service.Status -eq 'Running') { 'Green' } else { 'Red' })

# Step 6: Verify functionality
Write-Host ""
Write-Host "Step 6: Verifying service..." -ForegroundColor Yellow

# Check if port is listening
$listening = netstat -ano | findstr ":3000" | findstr "LISTENING"
if ($listening) {
    Write-Host "  Port 3000 is LISTENING" -ForegroundColor Green
} else {
    Write-Host "  WARNING: Port 3000 not listening" -ForegroundColor Red
    Write-Host ""
    Write-Host "  Checking error log..."
    Get-Content "$logDir\service-error.log" -Tail 20 -ErrorAction SilentlyContinue
}

# Test health endpoint
try {
    $response = Invoke-WebRequest -Uri "http://localhost:3000/health" -UseBasicParsing -TimeoutSec 5
    Write-Host "  Health check: OK (Status $($response.StatusCode))" -ForegroundColor Green
} catch {
    Write-Host "  Health check: FAILED" -ForegroundColor Red
}

Write-Host ""
Write-Host "======================================================================" -ForegroundColor Cyan
Write-Host "Deployment Complete!" -ForegroundColor Cyan
Write-Host "======================================================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Service Name: $serviceName"
Write-Host "Service Status: $($service.Status)"
Write-Host "Logs: $logDir"
Write-Host "Port: 3000"
Write-Host ""
Write-Host "To check logs: Get-Content $logDir\service-error.log -Tail 50"
Write-Host "To restart: Restart-Service $serviceName"
Write-Host ""
"""

def main():
    print("="*70)
    print("Starting Complete WhatsApp Service Deployment")
    print("="*70)
    print("\nThis will:")
    print("  1. Install NSSM (via Chocolatey)")
    print("  2. Install all npm dependencies + Chromium")
    print("  3. Configure Windows Service with auto-restart")
    print("  4. Start and verify the service")
    print("\nEstimated time: 5-10 minutes")
    print("\n" + "="*70)
    input("\nPress Enter to start deployment...")

    try:
        ssh = paramiko.SSHClient()
        ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
        print("\nConnecting to server...")
        ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD, timeout=15)
        print("Connected!\n")

        # Upload script
        print("Uploading deployment script...")
        remote_script = "C:\\whatsappbridge\\complete-deployment.ps1"
        sftp = ssh.open_sftp()
        with sftp.file(remote_script, 'w') as f:
            f.write(DEPLOYMENT_SCRIPT)
        sftp.close()
        print("Script uploaded\n")

        # Execute deployment
        print("Executing deployment (this will take several minutes)...\n")
        print("-"*70)

        stdin, stdout, stderr = ssh.exec_command(
            f'powershell -ExecutionPolicy Bypass -File "{remote_script}"',
            get_pty=True
        )

        # Stream output in real-time
        for line in stdout:
            line = line.strip()
            if line and not line.startswith('['):  # Filter ANSI codes
                print(line)

        print("-"*70)

        # Final verification
        print("\nFinal verification...")
        stdin2, stdout2, stderr2 = ssh.exec_command(
            'powershell -Command "Get-Service WhatsAppBridgeService | Select-Object Name,Status,StartType"'
        )
        print(stdout2.read().decode('utf-8', errors='ignore'))

        ssh.close()

        print("\n" + "="*70)
        print("DEPLOYMENT SUCCESSFUL!")
        print("="*70)
        print("\nYou can now try connecting WhatsApp from the web interface!")
        print("The QR code should appear within 30 seconds.")

    except Exception as e:
        print(f"\n\nERROR during deployment: {e}")
        import traceback
        traceback.print_exc()
        sys.exit(1)

if __name__ == "__main__":
    main()
