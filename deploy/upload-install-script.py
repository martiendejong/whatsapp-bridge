#!/usr/bin/env python3
"""Upload INSTALL_ON_SERVER.ps1 and run it"""

import paramiko
from pathlib import Path

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"
DOMAIN = "whatsapp.wreckingball.ai"

def main():
    print("="*70)
    print("Upload and Run Installation Script")
    print("="*70)

    local_script = Path(__file__).parent / "INSTALL_ON_SERVER.ps1"

    if not local_script.exists():
        print(f"Error: {local_script} not found")
        return

    try:
        print("\n[1/3] Connecting to server...")
        ssh = paramiko.SSHClient()
        ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
        ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD)
        print(f"    Connected to {SSH_HOST}")

        print("\n[2/3] Uploading installation script via SFTP...")
        sftp = ssh.open_sftp()
        sftp.put(str(local_script), r"C:\whatsappbridge\deploy\INSTALL_ON_SERVER.ps1")
        sftp.close()
        print("    Upload complete!")

        print("\n[3/3] Running installation (15-20 minutes)...")
        stdin, stdout, stderr = ssh.exec_command(
            f'powershell -ExecutionPolicy Bypass -File C:\\whatsappbridge\\deploy\\INSTALL_ON_SERVER.ps1 -Domain {DOMAIN}',
            get_pty=True
        )

        # Print output
        for line in stdout:
            line_str = line.rstrip()
            if line_str and '?25' not in line_str and '\x1b' not in line_str[:10]:
                print(line_str)

        print("\n" + "="*70)
        print("DNS RECORD TO ADD")
        print("="*70)
        print("\nAdd this A record to wreckingball.ai DNS:")
        print()
        print("  Type:  A")
        print("  Name:  whatsapp")
        print(f"  Value: {SSH_HOST}")
        print("  TTL:   3600")
        print()
        print("="*70)
        print(f"\nAfter DNS propagation (5-30 minutes):")
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
