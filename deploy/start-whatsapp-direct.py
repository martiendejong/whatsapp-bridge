#!/usr/bin/env python3
"""Start WhatsApp service directly with Node.js"""

import paramiko

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"

SCRIPT = r"""# Start WhatsApp Service directly
$serviceDir = "C:\Services\WhatsAppBridge"

Write-Host "Starting WhatsApp Service..."

# Install dependencies if needed
Push-Location $serviceDir

if (-not (Test-Path "node_modules")) {
    Write-Host "Installing npm dependencies (this may take a minute)..."
    npm install --production
}

Write-Host ""
Write-Host "Starting Node.js process..."

# Start in background
$nodeExe = "C:\Program Files\nodejs\node.exe"
$appJs = "index.js"

# Create startup batch file
$startScript = @"
@echo off
cd /d $serviceDir
start /B $nodeExe $appJs > logs\service.log 2>&1
"@

$startScript | Out-File -FilePath "$serviceDir\start.bat" -Encoding ASCII

# Run the script
& "$serviceDir\start.bat"

Pop-Location

Write-Host ""
Write-Host "Waiting for service to start..."
Start-Sleep -Seconds 5

# Check if running
$listening = netstat -ano | findstr ":3000" | findstr "LISTENING"
if ($listening) {
    Write-Host "SUCCESS! WhatsApp Service is running on port 3000"
} else {
    Write-Host "Service started but port not listening yet"
    Write-Host "Check logs at: $serviceDir\logs\service.log"
}
"""

ssh = paramiko.SSHClient()
ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD, timeout=15)

print("="*70)
print("Starting WhatsApp Service (Direct Node.js)")
print("="*70)
print()

# Upload script
remote_script = "C:\\whatsappbridge\\start-whatsapp.ps1"
sftp = ssh.open_sftp()
with sftp.file(remote_script, 'w') as f:
    f.write(SCRIPT)
sftp.close()

print("Executing startup script...\n")

# Execute
stdin, stdout, stderr = ssh.exec_command(f'powershell -ExecutionPolicy Bypass -File "{remote_script}"')

for line in stdout:
    line = line.strip()
    if line and not line.startswith('['):
        print(f"  {line}")

print("\n" + "="*70)
print("WhatsApp Service started!")
print("\nRefresh the WhatsApp page and try connecting again")
print("="*70)

ssh.close()
