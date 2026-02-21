#!/usr/bin/env python3
"""Re-upload complete application files to server"""

import paramiko
import os
import sys

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"

LOCAL_BASE = "E:/projects/whatsappbridge"
REMOTE_BASE = "C:/whatsappbridge"

print("=" * 70)
print("RE-UPLOADING COMPLETE APPLICATION")
print("=" * 70)

try:
    # Connect
    print("\n[1/4] Connecting...")
    ssh = paramiko.SSHClient()
    ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD, timeout=30)
    sftp = ssh.open_sftp()
    print("  Connected")

    # Helper to upload directory recursively
    def upload_dir(local_path, remote_path):
        print(f"  Uploading {os.path.basename(local_path)}...")

        # Create remote directory
        try:
            sftp.mkdir(remote_path)
        except:
            pass  # Already exists

        # Upload all files and subdirectories
        for item in os.listdir(local_path):
            local_item = os.path.join(local_path, item)
            remote_item = remote_path + "/" + item

            if os.path.isfile(local_item):
                # Skip large files and unnecessary items
                if item in ['node_modules', '.git', 'bin', 'obj']:
                    continue
                if item.endswith('.db') or item.endswith('.log'):
                    continue

                try:
                    sftp.put(local_item, remote_item)
                except Exception as e:
                    print(f"    Warning: Failed to upload {item}: {e}")
            elif os.path.isdir(local_item):
                if item not in ['node_modules', '.git', 'bin', 'obj']:
                    upload_dir(local_item, remote_item)

    # Upload Backend
    print("\n[2/4] Uploading Backend (ASP.NET Core)...")
    backend_local = os.path.join(LOCAL_BASE, "Backend")
    backend_remote = REMOTE_BASE + "/Backend"
    upload_dir(backend_local, backend_remote)

    # Upload Frontend
    print("\n[3/4] Uploading Frontend (React)...")
    frontend_local = os.path.join(LOCAL_BASE, "Frontend")
    frontend_remote = REMOTE_BASE + "/Frontend"
    upload_dir(frontend_local, frontend_remote)

    # Upload WhatsApp Service
    print("\n[4/4] Uploading WhatsApp Service (Node.js)...")
    service_local = os.path.join(LOCAL_BASE, "WhatsAppService")
    service_remote = REMOTE_BASE + "/WhatsAppService"
    upload_dir(service_local, service_remote)

    sftp.close()
    ssh.close()

    print("\n" + "=" * 70)
    print("UPLOAD COMPLETE!")
    print("=" * 70)
    print("\nNext: Run fix-deployment.py again")

except Exception as e:
    print(f"\n[ERROR] {e}")
    import traceback
    traceback.print_exc()
    sys.exit(1)
