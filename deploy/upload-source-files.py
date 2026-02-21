#!/usr/bin/env python3
"""Upload source files and extract on server"""

import paramiko
import os

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"

print("="*70)
print("UPLOADING SOURCE FILES")
print("="*70)

try:
    ssh = paramiko.SSHClient()
    ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD)
    sftp = ssh.open_sftp()

    # Upload Backend
    print("\n[1/4] Uploading Backend source...")
    backend_zip = "E:/temp/backend-source.zip"
    if os.path.exists(backend_zip):
        size_mb = os.path.getsize(backend_zip) / (1024 * 1024)
        print(f"  Size: {size_mb:.2f} MB")
        sftp.put(backend_zip, "C:/Temp/backend.zip")
        print("  Uploaded")
    else:
        print("  [ERROR] backend-source.zip not found")

    # Upload Frontend
    print("\n[2/4] Uploading Frontend source...")
    frontend_zip = "E:/temp/frontend-source.zip"
    if os.path.exists(frontend_zip):
        size_mb = os.path.getsize(frontend_zip) / (1024 * 1024)
        print(f"  Size: {size_mb:.2f} MB")
        sftp.put(frontend_zip, "C:/Temp/frontend.zip")
        print("  Uploaded")
    else:
        print("  [ERROR] frontend-source.zip not found")

    sftp.close()

    # Extract on server
    print("\n[3/4] Extracting Backend...")
    cmd = 'powershell -Command "Expand-Archive -Path C:\\Temp\\backend.zip -DestinationPath C:\\whatsappbridge\\Backend\\WhatsAppBridge.API -Force"'
    stdin, stdout, stderr = ssh.exec_command(cmd)
    output = stdout.read().decode('utf-8', errors='ignore')
    error = stderr.read().decode('utf-8', errors='ignore')
    if error:
        print(f"  Warning: {error}")
    else:
        print("  Extracted")

    print("\n[4/4] Extracting Frontend...")
    cmd = 'powershell -Command "Expand-Archive -Path C:\\Temp\\frontend.zip -DestinationPath C:\\whatsappbridge\\Frontend -Force"'
    stdin, stdout, stderr = ssh.exec_command(cmd)
    output = stdout.read().decode('utf-8', errors='ignore')
    error = stderr.read().decode('utf-8', errors='ignore')
    if error:
        print(f"  Warning: {error}")
    else:
        print("  Extracted")

    ssh.close()

    print("\n" + "="*70)
    print("SOURCE FILES UPLOADED!")
    print("="*70)
    print("\nNext: Run installation again")

except Exception as e:
    print(f"\n[ERROR] {e}")
    import traceback
    traceback.print_exc()
