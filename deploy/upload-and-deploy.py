#!/usr/bin/env python3
"""Upload project and deploy to server"""

import paramiko
from scp import SCPClient
import os

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"
DOMAIN = "whatsapp.wreckingball.ai"

def progress(filename, size, sent):
    """Progress callback"""
    percent = (sent / size) * 100 if size > 0 else 0
    print(f"\r  Uploading: {percent:.1f}% ({sent}/{size} bytes)", end='', flush=True)

def main():
    print("="*70)
    print("WhatsApp Bridge - Upload and Deploy")
    print("="*70)

    archive_path = "E:\\projects\\whatsappbridge.tar.gz"

    if not os.path.exists(archive_path):
        print(f"\nError: Archive not found at {archive_path}")
        print("Creating archive...")
        os.system("cd E:\\projects && tar -czf whatsappbridge.tar.gz whatsappbridge\\")

    try:
        print("\n[1/5] Connecting to server...")
        ssh = paramiko.SSHClient()
        ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
        ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD)
        print(f"    Connected to {SSH_HOST}")

        print("\n[2/5] Uploading project archive...")
        with SCPClient(ssh.get_transport(), progress=progress, socket_timeout=300) as scp:
            scp.put(archive_path, 'C:\\whatsappbridge.tar.gz')
        print("\n    Upload complete!")

        print("\n[3/5] Extracting archive on server...")
        stdin, stdout, stderr = ssh.exec_command("powershell -Command \"cd C:\\; tar -xzf whatsappbridge.tar.gz\"", get_pty=True)
        for line in stdout:
            print(f"    {line.rstrip()}")

        print("\n[4/5] Running complete deployment script...")
        deploy_script = f"""
cd C:\\whatsappbridge\\deploy
.\\deploy-wreckingball.ps1 -Domain '{DOMAIN}'
"""
        stdin, stdout, stderr = ssh.exec_command(f"powershell -ExecutionPolicy Bypass -Command \"{deploy_script}\"", get_pty=True)

        for line in stdout:
            line_str = line.rstrip()
            if line_str and not line_str.startswith('['):  # Filter ANSI codes
                print(f"    {line_str}")

        print("\n[5/5] Deployment complete!")

        print("\n" + "="*70)
        print("DNS Configuration Required")
        print("="*70)
        print("\nAdd dit A record aan wreckingball.ai DNS:")
        print()
        print(f"  Type:  A")
        print(f"  Name:  whatsapp")
        print(f"  Value: {SSH_HOST}")
        print(f"  TTL:   3600")
        print()
        print("Na DNS propagatie (5-30 minuten):")
        print(f"  Website: http://{DOMAIN}")
        print(f"  API:     http://{DOMAIN}:5000/swagger")
        print()

        ssh.close()

    except Exception as e:
        print(f"\nError: {e}")
        import traceback
        traceback.print_exc()

if __name__ == "__main__":
    main()
