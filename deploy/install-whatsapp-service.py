#!/usr/bin/env python3
"""Install and start WhatsApp Service"""

import paramiko
import time

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "SpaceElevator1tam!"

SCRIPT = r"""# Install WhatsApp Service
Write-Host "Installing WhatsApp Service..."

# Install NSSM
$nssmUrl = "https://nssm.cc/release/nssm-2.24.zip"
$nssmZip = "C:\Temp\nssm.zip"
$nssmDir = "C:\nssm"

Write-Host "Downloading NSSM..."
New-Item -ItemType Directory -Path "C:\Temp" -Force | Out-Null
Invoke-WebRequest -Uri $nssmUrl -OutFile $nssmZip -UseBasicParsing

Write-Host "Extracting NSSM..."
Expand-Archive -Path $nssmZip -DestinationPath "C:\Temp\nssm" -Force
Copy-Item "C:\Temp\nssm\nssm-2.24\win64\nssm.exe" -Destination $nssmDir -Force
$env:Path += ";$nssmDir"

Write-Host "NSSM installed to $nssmDir"

# Install WhatsApp service
$serviceName = "WhatsAppBridgeService"
$serviceDir = "C:\Services\WhatsAppBridge"
$nodeExe = "C:\Program Files\nodejs\node.exe"
$appJs = Join-Path $serviceDir "index.js"

Write-Host ""
Write-Host "Installing Windows Service..."

& "$nssmDir\nssm.exe" install $serviceName $nodeExe $appJs
& "$nssmDir\nssm.exe" set $serviceName AppDirectory $serviceDir
& "$nssmDir\nssm.exe" set $serviceName DisplayName "WhatsApp Bridge Service"
& "$nssmDir\nssm.exe" set $serviceName Description "WhatsApp Web.js service for WhatsApp Bridge"
& "$nssmDir\nssm.exe" set $serviceName Start SERVICE_AUTO_START
& "$nssmDir\nssm.exe" set $serviceName AppStdout "C:\Services\WhatsAppBridge\logs\service.log"
& "$nssmDir\nssm.exe" set $serviceName AppStderr "C:\Services\WhatsAppBridge\logs\service-error.log"

Write-Host "Service installed"

# Create logs directory
New-Item -ItemType Directory -Path "$serviceDir\logs" -Force | Out-Null

# Install npm dependencies if needed
Write-Host ""
Write-Host "Checking npm dependencies..."
Push-Location $serviceDir
if (-not (Test-Path "node_modules")) {
    Write-Host "Installing npm packages..."
    npm install
} else {
    Write-Host "Dependencies already installed"
}
Pop-Location

# Start service
Write-Host ""
Write-Host "Starting WhatsApp Service..."
Start-Service -Name $serviceName

Start-Sleep -Seconds 3

# Check status
$service = Get-Service -Name $serviceName
Write-Host ""
Write-Host "Service Status: $($service.Status)"

# Check if port is listening
$listening = netstat -ano | findstr ":3000" | findstr "LISTENING"
if ($listening) {
    Write-Host "Port 3000 is LISTENING - Service is running!"
} else {
    Write-Host "WARNING: Port 3000 not listening yet"
    Write-Host "Check logs at: $serviceDir\logs\service-error.log"
}
"""

ssh = paramiko.SSHClient()
ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD, timeout=15)

print("="*70)
print("Installing WhatsApp Service")
print("="*70)
print()

# Upload script
remote_script = "C:\\whatsappbridge\\install-whatsapp-service.ps1"
sftp = ssh.open_sftp()
with sftp.file(remote_script, 'w') as f:
    f.write(SCRIPT)
sftp.close()

print("Script uploaded, executing (this may take 1-2 minutes)...\n")

# Execute
stdin, stdout, stderr = ssh.exec_command(f'powershell -ExecutionPolicy Bypass -File "{remote_script}"', get_pty=True)

# Read output in real-time
for line in stdout:
    line = line.strip()
    if line and not line.startswith('['):
        print(f"  {line}")

errors = stderr.read().decode('utf-8', errors='ignore')
if errors and "error" in errors.lower():
    print(f"\nErrors:\n{errors}")

print("\n" + "="*70)
print("WhatsApp Service installation complete!")
print("\nTry connecting WhatsApp again - QR code should now appear")
print("="*70)

ssh.close()
