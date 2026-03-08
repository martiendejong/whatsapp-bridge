#!/usr/bin/env python3
"""Fix start script with proper quoting"""

import paramiko

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"

SCRIPT = r"""# Fix and restart WhatsApp Service
$serviceDir = "C:\Services\WhatsAppBridge"

# Kill any existing node processes for this service
Get-Process node -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "*nodejs*" } | Stop-Process -Force -ErrorAction SilentlyContinue

Write-Host "Creating proper start script..."

# Create startup batch file with proper quoting
$startScript = @"
@echo off
cd /d "$serviceDir"
"C:\Program Files\nodejs\node.exe" index.js > logs\service.log 2>&1
"@

$startScript | Out-File -FilePath "$serviceDir\start.bat" -Encoding ASCII -Force

Write-Host "Starting service..."

# Start in background
Start-Process -FilePath "$serviceDir\start.bat" -WorkingDirectory $serviceDir -WindowStyle Hidden

Start-Sleep -Seconds 5

# Check if running
$listening = netstat -ano | findstr ":3000" | findstr "LISTENING"
if ($listening) {
    Write-Host "SUCCESS! Port 3000 is listening"

    # Test the endpoint
    try {
        $response = Invoke-WebRequest -Uri "http://localhost:3000/health" -UseBasicParsing -TimeoutSec 5 -ErrorAction Stop
        Write-Host "Health check: $($response.StatusCode)"
    } catch {
        Write-Host "Port listening but health endpoint not ready yet"
    }
} else {
    Write-Host "Port 3000 still not listening"
    Write-Host ""
    Write-Host "Log content:"
    Get-Content "$serviceDir\logs\service.log" -ErrorAction SilentlyContinue -Tail 20
}
"""

ssh = paramiko.SSHClient()
ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD)

print("Fixing and restarting WhatsApp Service...\n")

# Upload script
remote_script = "C:\\whatsappbridge\\fix-start.ps1"
sftp = ssh.open_sftp()
with sftp.file(remote_script, 'w') as f:
    f.write(SCRIPT)
sftp.close()

# Execute
stdin, stdout, stderr = ssh.exec_command(f'powershell -ExecutionPolicy Bypass -File "{remote_script}"')

for line in stdout:
    line = line.strip()
    if line:
        print(f"  {line}")

print("\n" + "="*70)
print("If port 3000 is listening, try connecting WhatsApp again!")
print("="*70)

ssh.close()
