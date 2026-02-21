#!/usr/bin/env python3
"""Upload project archive via SFTP and extract on server"""

import paramiko
import os
from pathlib import Path

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"
DOMAIN = "whatsapp.wreckingball.ai"

LOCAL_ARCHIVE = r"E:\temp\whatsappbridge.zip"
REMOTE_ARCHIVE = "C:\\whatsappbridge.zip"

def main():
    print("="*70)
    print("WhatsApp Bridge - SFTP Upload and Deploy")
    print("="*70)

    if not os.path.exists(LOCAL_ARCHIVE):
        print(f"\nError: Archive not found at {LOCAL_ARCHIVE}")
        print("Run complete-deploy.ps1 first to create the archive")
        return

    file_size = os.path.getsize(LOCAL_ARCHIVE) / (1024 * 1024)
    print(f"\nArchive size: {file_size:.2f} MB")

    try:
        print("\n[1/4] Connecting to server...")
        ssh = paramiko.SSHClient()
        ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
        ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD)
        print(f"    Connected to {SSH_HOST}")

        print("\n[2/4] Uploading archive via SFTP...")
        print("    This may take 2-3 minutes...")

        sftp = ssh.open_sftp()

        # Upload with progress
        def progress(transferred, total):
            percent = (transferred / total) * 100
            print(f"\r    Progress: {percent:.1f}% ({transferred/(1024*1024):.1f}/{total/(1024*1024):.1f} MB)", end='', flush=True)

        sftp.put(LOCAL_ARCHIVE, REMOTE_ARCHIVE, callback=progress)
        sftp.close()

        print("\n    Upload complete!")

        print("\n[3/4] Extracting archive on server...")
        stdin, stdout, stderr = ssh.exec_command(
            f'powershell -Command "Expand-Archive -Path \'{REMOTE_ARCHIVE}\' -DestinationPath \'C:\\\' -Force; Remove-Item \'{REMOTE_ARCHIVE}\' -Force"',
            get_pty=True
        )

        # Wait for extraction
        exit_code = stdout.channel.recv_exit_status()
        output = stdout.read().decode('utf-8', errors='ignore')

        if exit_code == 0:
            print("    Extraction complete")
        else:
            print(f"    Warning: Extraction may have issues (exit code {exit_code})")
            print(output)

        print("\n[4/4] Running installation...")
        print("    This will take 15-20 minutes...")
        print("    Installing .NET, Node.js, IIS, deploying services...")

        stdin, stdout, stderr = ssh.exec_command(
            f'powershell -ExecutionPolicy Bypass -File C:\\whatsappbridge\\deploy\\INSTALL_ON_SERVER.ps1 -Domain {DOMAIN}',
            get_pty=True
        )

        # Show output in real-time
        for line in stdout:
            line_str = line.rstrip()
            if line_str and not line_str.startswith('\x1b'):
                print(f"    {line_str}")

        print("\n" + "="*70)
        print("DNS Configuration Required")
        print("="*70)
        print("\nAdd this A record to wreckingball.ai DNS:")
        print()
        print("  Type:  A")
        print("  Name:  whatsapp")
        print(f"  Value: {SSH_HOST}")
        print("  TTL:   3600")
        print()
        print("="*70)
        print("\nAfter DNS propagation (5-30 minutes):")
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
