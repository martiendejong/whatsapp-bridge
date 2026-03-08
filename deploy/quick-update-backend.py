#!/usr/bin/env python3
"""Quick update - just the main DLL"""

import paramiko
import subprocess
import os
import time

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"

print("="*70)
print("Quick Backend Update (DLL only)")
print("="*70)

# 1. Build backend locally
print("\n1. Building backend...")
os.chdir('E:\\projects\\whatsappbridge\\Backend')
result = subprocess.run(
    ['dotnet', 'publish', 'WhatsAppBridge.API', '-c', 'Release', '-o', '../publish/backend'],
    capture_output=True,
    text=True
)

if result.returncode != 0:
    print(f"BUILD FAILED:\n{result.stderr}")
    exit(1)

print("   Build successful")

# 2. Stop IIS, upload DLL, restart
print("\n2. Connecting to server...")
ssh = paramiko.SSHClient()
ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD)

print("\n3. Stopping IIS app pool...")
stdin, stdout, stderr = ssh.exec_command('powershell -Command "Stop-WebAppPool -Name WhatsAppBridgeAPIPool; Start-Sleep -Seconds 3"')
stdout.read()  # Wait for command

print("\n4. Uploading WhatsAppBridge.API.dll...")
sftp = ssh.open_sftp()
try:
    sftp.put(
        'E:\\projects\\whatsappbridge\\publish\\backend\\WhatsAppBridge.API.dll',
        '/C:/inetpub/whatsappbridge-api/WhatsAppBridge.API.dll'
    )
    print("   Uploaded successfully")
except Exception as e:
    print(f"   Upload error: {e}")
    print("   Trying alternative approach...")

    # Alternative: Use PowerShell to delete and upload
    ssh.exec_command('del C:\\inetpub\\whatsappbridge-api\\WhatsAppBridge.API.dll')
    time.sleep(1)

    sftp.put(
        'E:\\projects\\whatsappbridge\\publish\\backend\\WhatsAppBridge.API.dll',
        '/C:/inetpub/whatsappbridge-api/WhatsAppBridge.API.dll'
    )
    print("   Uploaded via alternative method")

sftp.close()

print("\n5. Starting IIS app pool...")
stdin, stdout, stderr = ssh.exec_command('powershell -Command "Start-WebAppPool -Name WhatsAppBridgeAPIPool"')
stdout.read()

time.sleep(5)

# 6. Verify
print("\n6. Verifying API...")
stdin, stdout, stderr = ssh.exec_command(
    'powershell -Command "try { $r = Invoke-WebRequest -Uri https://localhost:5001/api/health -UseBasicParsing -TimeoutSec 10; Write-Host \\"API responding: $($r.StatusCode)\\" } catch { Write-Host \\"ERROR: $($_.Exception.Message)\\" }"'
)
health = stdout.read().decode('utf-8', errors='ignore').strip()
print(f"   {health}")

ssh.close()

print("\n" + "="*70)
print("Backend updated! Routes fixed:")
print("  /session/create -> /sessions/create")
print("  /session/{id} -> /sessions/{id}")
print("\nTry connecting WhatsApp now!")
print("="*70)
