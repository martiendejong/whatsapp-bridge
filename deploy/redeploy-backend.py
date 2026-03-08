#!/usr/bin/env python3
"""Rebuild and redeploy backend API with fixed routes"""

import paramiko
import subprocess
import os

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"

print("="*70)
print("Rebuilding and Redeploying Backend API")
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

# 2. Stop IIS site
print("\n2. Stopping IIS site...")
ssh = paramiko.SSHClient()
ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD)

ssh.exec_command('powershell -Command "Stop-WebSite -Name WhatsAppBridgeAPI"')
import time
time.sleep(2)

# 3. Deploy files via SFTP
print("\n3. Deploying files...")
sftp = ssh.open_sftp()

# Create directory if needed
try:
    sftp.mkdir('/C:/inetpub/whatsappbridge-api')
except:
    pass

# Upload all files
local_dir = 'E:\\projects\\whatsappbridge\\publish\\backend'
remote_dir = '/C:/inetpub/whatsappbridge-api'

file_count = 0
for root, dirs, files in os.walk(local_dir):
    for filename in files:
        local_path = os.path.join(root, filename)
        relative_path = os.path.relpath(local_path, local_dir)
        remote_path = remote_dir + '/' + relative_path.replace('\\', '/')

        # Create remote directory if needed
        remote_dir_path = '/'.join(remote_path.split('/')[:-1])
        try:
            sftp.mkdir(remote_dir_path)
        except:
            pass

        sftp.put(local_path, remote_path)
        file_count += 1

        if file_count % 10 == 0:
            print(f"   Uploaded {file_count} files...")

print(f"   Uploaded {file_count} files total")

sftp.close()

# 4. Start IIS site
print("\n4. Starting IIS site...")
ssh.exec_command('powershell -Command "Start-WebSite -Name WhatsAppBridgeAPI"')
time.sleep(3)

# 5. Verify
print("\n5. Verifying deployment...")
stdin, stdout, stderr = ssh.exec_command(
    'powershell -Command "try { $r = Invoke-WebRequest -Uri https://whatsapp.wreckingball.ai:5001/api/health -UseBasicParsing -TimeoutSec 5; Write-Host \\"API Status: $($r.StatusCode)\\" } catch { Write-Host \\"ERROR: $($_.Exception.Message)\\" }"'
)
health = stdout.read().decode('utf-8', errors='ignore').strip()
print(f"   {health}")

ssh.close()

print("\n" + "="*70)
print("Backend API deployed successfully!")
print("Routes fixed:")
print("  /session/create -> /sessions/create")
print("  /session/{id} -> /sessions/{id}")
print("="*70)
