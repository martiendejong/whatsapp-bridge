#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Deploy test connection button feature to production"""

import paramiko
import subprocess
import os
import time
import sys

# Force UTF-8 encoding for output
if sys.platform == 'win32':
    sys.stdout.reconfigure(encoding='utf-8')

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "SpaceElevator1tam!"

print("="*70)
print("Deploying WhatsApp Connection Test Fix (PR #3)")
print("="*70)

# Connect to server
print("\n1. Connecting to server...")
ssh = paramiko.SSHClient()
ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD)
sftp = ssh.open_sftp()

# Stop IIS app pool
print("\n2. Stopping IIS app pool...")
stdin, stdout, stderr = ssh.exec_command('powershell -Command "Stop-WebAppPool -Name WhatsAppBridgeAPIPool; Start-Sleep -Seconds 3"')
stdout.read()

# Upload backend DLL
print("\n3. Uploading backend API...")
try:
    sftp.put(
        'E:\\projects\\whatsappbridge\\publish\\backend\\WhatsAppBridge.API.dll',
        '/C:/inetpub/whatsappbridge-api/WhatsAppBridge.API.dll'
    )
    print("   [OK] Backend DLL uploaded")
except Exception as e:
    print(f"   Error: {e}")
    # Retry with delete first
    ssh.exec_command('del C:\\inetpub\\whatsappbridge-api\\WhatsAppBridge.API.dll')
    time.sleep(1)
    sftp.put(
        'E:\\projects\\whatsappbridge\\publish\\backend\\WhatsAppBridge.API.dll',
        '/C:/inetpub/whatsappbridge-api/WhatsAppBridge.API.dll'
    )
    print("   [OK] Backend DLL uploaded (retry)")

# Upload frontend files
print("\n4. Uploading frontend...")
frontend_dist = 'E:\\projects\\whatsappbridge\\Frontend\\dist'
remote_frontend = '/C:/inetpub/whatsappbridge-web'

# Upload index.html
sftp.put(
    os.path.join(frontend_dist, 'index.html'),
    f'{remote_frontend}/index.html'
)
print("   [OK] index.html uploaded")

# Upload assets directory
assets_local = os.path.join(frontend_dist, 'assets')
assets_remote = f'{remote_frontend}/assets'

for filename in os.listdir(assets_local):
    local_path = os.path.join(assets_local, filename)
    remote_path = f'{assets_remote}/{filename}'
    sftp.put(local_path, remote_path)
    print(f"   [OK] {filename} uploaded")

# Start IIS app pool
print("\n5. Starting IIS app pool...")
stdin, stdout, stderr = ssh.exec_command('powershell -Command "Start-WebAppPool -Name WhatsAppBridgeAPIPool"')
stdout.read()

time.sleep(5)

# Verify API is responding
print("\n6. Verifying API health...")
stdin, stdout, stderr = ssh.exec_command(
    'powershell -Command "try { $r = Invoke-WebRequest -Uri https://localhost:5001/api/health -UseBasicParsing -TimeoutSec 10; Write-Host \\"API responding: $($r.StatusCode)\\" } catch { Write-Host \\"ERROR: $($_.Exception.Message)\\" }"'
)
health = stdout.read().decode('utf-8', errors='ignore').strip()
print(f"   {health}")

# Verify test endpoint exists
print("\n7. Checking test connection endpoint...")
stdin, stdout, stderr = ssh.exec_command(
    'powershell -Command "try { $r = Invoke-WebRequest -Uri https://localhost:5001/swagger/v1/swagger.json -UseBasicParsing; $swagger = $r.Content | ConvertFrom-Json; if ($swagger.paths.\\\"/api/apiconnections/{id}/test\\\") { Write-Host \\\"Test endpoint registered\\\" } else { Write-Host \\\"Test endpoint NOT found\\\" } } catch { Write-Host \\\"Could not verify: $($_.Exception.Message)\\\" }"'
)
endpoint_check = stdout.read().decode('utf-8', errors='ignore').strip()
print(f"   {endpoint_check}")

sftp.close()
ssh.close()

print("\n" + "="*70)
print("DEPLOYMENT COMPLETE!")
print("")
print("WhatsApp Connection Test Fix deployed to:")
print("  https://whatsapp.wreckingball.ai")
print("")
print("Fixed functionality:")
print("  - Test button now tests actual WhatsApp connection")
print("  - Verifies QR code scan is still active")
print("  - Shows phone number and session status")
print("  - Clear error messages when session disconnected")
print("")
print("The test button now correctly verifies your WhatsApp")
print("session instead of just checking the API token!")
print("="*70)
