#!/usr/bin/env python3
"""Fix CORS to allow frontend origin"""

import paramiko
import json

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"

SCRIPT = r"""# Fix CORS configuration
$apiPath = "C:\inetpub\whatsappbridge-api"
$settingsPath = Join-Path $apiPath "appsettings.Production.json"

# Read current settings
$settings = Get-Content $settingsPath | ConvertFrom-Json

# Update AllowedOrigins
$settings.AllowedOrigins = @(
    "https://whatsapp.wreckingball.ai",
    "http://whatsapp.wreckingball.ai",
    "http://localhost:5173"
)

# Save settings
$settings | ConvertTo-Json -Depth 10 | Set-Content $settingsPath

Write-Host "Updated CORS AllowedOrigins in appsettings.Production.json"

# Restart API site
Stop-Website -Name "WhatsAppBridgeAPI"
Start-Sleep -Seconds 2
Start-Website -Name "WhatsAppBridgeAPI"

Write-Host "Restarted API site"
Write-Host ""
Write-Host "CORS now allows: https://whatsapp.wreckingball.ai"
"""

ssh = paramiko.SSHClient()
ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD)

print("Fixing CORS configuration...\n")

# Upload script
remote_script = "C:\\whatsappbridge\\fix-cors.ps1"
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

errors = stderr.read().decode('utf-8', errors='ignore')
if errors and "error" in errors.lower():
    print(f"\nErrors:\n{errors}")

print("\n" + "="*70)
print("CORS configuration updated!")
print("\nFrontend (https://whatsapp.wreckingball.ai)")
print("can now access API (https://whatsapp.wreckingball.ai:5001)")
print("\nTest signup again")
print("="*70)

ssh.close()
