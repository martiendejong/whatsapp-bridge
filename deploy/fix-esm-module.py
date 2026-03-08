#!/usr/bin/env python3
"""Fix ES module configuration"""

import paramiko
import json

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"

SCRIPT = r"""# Fix package.json for ES modules
$serviceDir = "C:\Services\WhatsAppBridge"
$packageJson = Join-Path $serviceDir "package.json"

Write-Host "Updating package.json..."

# Read current package.json
$pkg = Get-Content $packageJson | ConvertFrom-Json

# Add type: module
$pkg | Add-Member -MemberType NoteProperty -Name "type" -Value "module" -Force

# Save
$pkg | ConvertTo-Json -Depth 10 | Set-Content $packageJson

Write-Host "Added 'type': 'module' to package.json"

# Restart service
Write-Host ""
Write-Host "Restarting service..."

# Kill existing
Get-Process node -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "*nodejs*" } | Stop-Process -Force -ErrorAction SilentlyContinue

Start-Sleep -Seconds 2

# Start again
Start-Process -FilePath "$serviceDir\start.bat" -WorkingDirectory $serviceDir -WindowStyle Hidden

Start-Sleep -Seconds 5

# Check status
$listening = netstat -ano | findstr ":3000" | findstr "LISTENING"
if ($listening) {
    Write-Host "SUCCESS! WhatsApp Service is running on port 3000"
} else {
    Write-Host "Still not listening. Checking logs..."
    Get-Content "$serviceDir\logs\service.log" -Tail 15
}
"""

ssh = paramiko.SSHClient()
ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD)

print("Fixing ES module configuration...\n")

# Upload script
remote_script = "C:\\whatsappbridge\\fix-esm.ps1"
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
print("ES module fix applied!")
print("\nIf service is running, refresh and try connecting WhatsApp")
print("="*70)

ssh.close()
