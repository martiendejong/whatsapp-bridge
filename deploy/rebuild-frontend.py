#!/usr/bin/env python3
"""Rebuild frontend with correct API URL and deploy"""

import paramiko
import os

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"

print("="*70)
print("Rebuilding Frontend with Relative API URL")
print("="*70)
print()

# Step 1: Build locally with correct env
print("Step 1: Building frontend locally...")
print("  Setting VITE_API_URL=https://whatsapp.wreckingball.ai:5001")

frontend_path = r"E:\projects\whatsappbridge\Frontend"

# Create .env.production file
env_content = "VITE_API_URL=https://whatsapp.wreckingball.ai:5001\n"
env_path = os.path.join(frontend_path, ".env.production")

with open(env_path, 'w') as f:
    f.write(env_content)

print(f"  Created {env_path}")

# Build frontend
print("  Running npm run build...")
import subprocess

result = subprocess.run(
    ["npm", "run", "build"],
    cwd=frontend_path,
    capture_output=True,
    text=True,
    shell=True
)

if result.returncode != 0:
    print(f"  ERROR: Build failed")
    print(result.stderr)
    exit(1)

print("  Build successful!")

# Step 2: Upload to server
print("\nStep 2: Uploading to server...")

ssh = paramiko.SSHClient()
ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD, timeout=15)

# Clear old files
print("  Clearing old frontend files...")
stdin, stdout, stderr = ssh.exec_command(r'powershell -Command "Remove-Item C:\inetpub\whatsappbridge-web\assets\*.js -Force; Remove-Item C:\inetpub\whatsappbridge-web\assets\*.css -Force"')
stdout.read()  # Wait for completion

# Upload new build
dist_path = os.path.join(frontend_path, "dist")
remote_path = "/C:/inetpub/whatsappbridge-web"

print(f"  Uploading from {dist_path}...")

sftp = ssh.open_sftp()

# Upload index.html
local_index = os.path.join(dist_path, "index.html")
remote_index = remote_path + "/index.html"
sftp.put(local_index, remote_index)
print("    - index.html")

# Upload assets directory
assets_local = os.path.join(dist_path, "assets")
assets_remote = remote_path + "/assets"

if os.path.exists(assets_local):
    # Create remote assets directory if needed
    try:
        sftp.mkdir(assets_remote)
    except:
        pass  # Directory exists

    # Upload all files in assets
    for filename in os.listdir(assets_local):
        local_file = os.path.join(assets_local, filename)
        remote_file = assets_remote + "/" + filename
        if os.path.isfile(local_file):
            sftp.put(local_file, remote_file)
            print(f"    - assets/{filename}")

sftp.close()

print("\n  Upload complete!")

# Step 3: Restart IIS site
print("\nStep 3: Restarting IIS site...")
stdin, stdout, stderr = ssh.exec_command(r'powershell -Command "Stop-Website -Name WhatsAppBridgeWeb; Start-Sleep -Seconds 1; Start-Website -Name WhatsAppBridgeWeb"')
stdout.read()
print("  Site restarted")

ssh.close()

print("\n" + "="*70)
print("Frontend rebuild and deployment complete!")
print("\nAPI URL is now relative (/api)")
print("All requests go through reverse proxy")
print("\nTest signup at: https://whatsapp.wreckingball.ai")
print("="*70)
