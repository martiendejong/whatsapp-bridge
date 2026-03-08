#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Deploy only frontend with cache busting"""

import paramiko
import os
import time
import sys

if sys.platform == 'win32':
    sys.stdout.reconfigure(encoding='utf-8')

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "SpaceElevator1tam!"

print("="*70)
print("Deploying Frontend with Cache Clear")
print("="*70)

# Connect to server
print("\n1. Connecting to server...")
ssh = paramiko.SSHClient()
ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD)
sftp = ssh.open_sftp()

# Upload frontend files
print("\n2. Uploading frontend...")
frontend_dist = 'E:\\projects\\whatsappbridge\\Frontend\\dist'
remote_frontend = '/C:/inetpub/whatsappbridge-web'

# Delete old assets first to force cache refresh
print("   Clearing old assets...")
stdin, stdout, stderr = ssh.exec_command('powershell -Command "Remove-Item C:\\inetpub\\whatsappbridge-web\\assets\\* -Force"')
stdout.read()
time.sleep(1)

# Upload index.html
print("   Uploading index.html...")
sftp.put(
    os.path.join(frontend_dist, 'index.html'),
    f'{remote_frontend}/index.html'
)

# Upload assets directory
assets_local = os.path.join(frontend_dist, 'assets')
assets_remote = f'{remote_frontend}/assets'

print("   Uploading assets...")
for filename in os.listdir(assets_local):
    local_path = os.path.join(assets_local, filename)
    remote_path = f'{assets_remote}/{filename}'
    sftp.put(local_path, remote_path)
    print(f"     - {filename}")

# Clear IIS cache
print("\n3. Clearing IIS cache...")
stdin, stdout, stderr = ssh.exec_command('powershell -Command "Restart-WebAppPool -Name DefaultAppPool"')
stdout.read()

time.sleep(2)

# Verify deployment
print("\n4. Verifying deployment...")
stdin, stdout, stderr = ssh.exec_command('powershell -Command "Get-ChildItem C:\\inetpub\\whatsappbridge-web\\assets\\ | Select-Object Name"')
files = stdout.read().decode('utf-8', errors='ignore')
print(f"   Deployed files:\n{files}")

sftp.close()
ssh.close()

print("\n" + "="*70)
print("FRONTEND DEPLOYMENT COMPLETE!")
print("")
print("IMPORTANT: Clear your browser cache!")
print("  - Chrome/Edge: Ctrl+Shift+Delete > Clear cache")
print("  - Or hard reload: Ctrl+F5")
print("")
print("Then refresh: https://whatsapp.wreckingball.ai")
print("="*70)
