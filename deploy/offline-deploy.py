#!/usr/bin/env python3
"""
Offline Deployment Script for WhatsApp Bridge
Uploads pre-compiled backend + WhatsApp Service with node_modules
"""

import paramiko
import os
import sys
from pathlib import Path

# Server config
SERVER = "85.215.217.154"
USERNAME = "administrator"
PASSWORD = "3WsXcFr$7YhNmKi*"

# Local paths
BACKEND_PUBLISHED = r"E:\temp\backend-published"
WHATSAPP_SERVICE = r"E:\projects\whatsappbridge\WhatsAppService"

# Remote paths
REMOTE_BACKEND = r"C:\inetpub\whatsappbridge-api"
REMOTE_SERVICE = r"C:\Services\WhatsAppBridge"

def upload_directory(sftp, local_path, remote_path, exclude_patterns=None):
    """Recursively upload directory"""
    if exclude_patterns is None:
        exclude_patterns = []

    print(f"[DIR] Uploading {local_path} -> {remote_path}")

    # Ensure remote directory exists
    try:
        sftp.stat(remote_path)
    except FileNotFoundError:
        print(f"   Creating remote directory: {remote_path}")
        sftp.mkdir(remote_path)

    for item in os.listdir(local_path):
        # Skip excluded patterns
        if any(pattern in item for pattern in exclude_patterns):
            continue

        local_item = os.path.join(local_path, item)
        remote_item = remote_path + "\\" + item

        if os.path.isfile(local_item):
            print(f"   [FILE] {item}")
            sftp.put(local_item, remote_item)
        elif os.path.isdir(local_item):
            upload_directory(sftp, local_item, remote_item, exclude_patterns)

def main():
    print("[DEPLOY] WhatsApp Bridge - Offline Deployment")
    print("=" * 60)

    # Connect to server
    print(f"\n[CONNECT] Connecting to {SERVER}...")
    ssh = paramiko.SSHClient()
    ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())

    try:
        ssh.connect(SERVER, username=USERNAME, password=PASSWORD)
        sftp = ssh.open_sftp()
        print("[OK] Connected")

        # Step 1: Upload Backend
        print("\n[UPLOAD] Step 1: Uploading Backend API...")
        if not os.path.exists(BACKEND_PUBLISHED):
            print(f"[ERROR] Backend not found: {BACKEND_PUBLISHED}")
            print("   Run: cd Backend/WhatsAppBridge.API && dotnet publish -c Release -o E:\\temp\\backend-published")
            return 1

        upload_directory(sftp, BACKEND_PUBLISHED, REMOTE_BACKEND)
        print("[OK] Backend uploaded")

        # Step 2: Upload WhatsApp Service
        print("\n[UPLOAD] Step 2: Uploading WhatsApp Service...")
        if not os.path.exists(WHATSAPP_SERVICE):
            print(f"[ERROR] Service not found: {WHATSAPP_SERVICE}")
            return 1

        # Check if node_modules exists
        if not os.path.exists(os.path.join(WHATSAPP_SERVICE, "node_modules")):
            print("[WARN]  node_modules not found. Run: cd WhatsAppService && npm install")
            return 1

        upload_directory(sftp, WHATSAPP_SERVICE, REMOTE_SERVICE, exclude_patterns=['.git', '__pycache__'])
        print("[OK] WhatsApp Service uploaded")

        # Step 3: Restart services
        print("\n[RESTART] Step 3: Restarting services...")

        # Restart IIS Application Pool
        stdin, stdout, stderr = ssh.exec_command(
            'powershell -Command "Restart-WebAppPool -Name WhatsAppBridgeAPI"'
        )
        print(f"   IIS Pool: {stdout.read().decode().strip() or '[OK] Restarted'}")

        # Restart WhatsApp Service
        stdin, stdout, stderr = ssh.exec_command(
            'powershell -Command "Restart-Service WhatsAppBridgeService -Force"'
        )
        error = stderr.read().decode().strip()
        if error and "Cannot find" in error:
            print("   [WARN]  Service not found, attempting to start...")
            stdin, stdout, stderr = ssh.exec_command(
                'schtasks /Run /TN "WhatsApp Bridge Service"'
            )
            print(f"   Scheduled Task: {stdout.read().decode().strip()}")
        else:
            print("   [OK] WhatsApp Service restarted")

        # Step 4: Verify services
        print("\n[VERIFY] Step 4: Verifying deployment...")

        stdin, stdout, stderr = ssh.exec_command(
            'powershell -Command "Get-WebAppPool | Where-Object {$_.Name -like \'*WhatsApp*\'} | Select-Object Name,State"'
        )
        print("   IIS Status:")
        print("  ", stdout.read().decode().strip())

        stdin, stdout, stderr = ssh.exec_command(
            'netstat -an | findstr "5000 3000"'
        )
        ports = stdout.read().decode().strip()
        if ports:
            print("   Ports listening:")
            for line in ports.split('\n')[:4]:  # First 4 lines
                print("  ", line.strip())
        else:
            print("   [WARN]  Ports not yet listening (may take a few seconds)")

        print("\n[OK] Deployment complete!")
        print("\nTest URLs:")
        print("  http://whatsapp.wreckingball.ai")
        print("  http://whatsapp.wreckingball.ai:5000/swagger")
        print("  http://whatsapp.wreckingball.ai:3000/health")

        sftp.close()
        ssh.close()
        return 0

    except Exception as e:
        print(f"\n[ERROR] Deployment failed: {e}")
        import traceback
        traceback.print_exc()
        return 1

if __name__ == "__main__":
    sys.exit(main())
