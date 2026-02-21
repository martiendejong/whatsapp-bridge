#!/usr/bin/env python3
"""Install SSL certificate remotely after DNS is live"""

import paramiko
import time
from pathlib import Path

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"
DOMAIN = "whatsapp.wreckingball.ai"
EMAIL = "martien@wreckingball.ai"

def main():
    print("="*70)
    print("SSL Certificate Installation - Remote")
    print("="*70)

    local_script = Path(__file__).parent / "install-ssl-certificate.ps1"

    if not local_script.exists():
        print(f"Error: {local_script} not found")
        return

    try:
        print("\n[1/4] Connecting to server...")
        ssh = paramiko.SSHClient()
        ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
        ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD)
        print(f"    Connected to {SSH_HOST}")

        print("\n[2/4] Verifying DNS propagation...")
        stdin, stdout, stderr = ssh.exec_command(f'nslookup {DOMAIN}', get_pty=True)
        output = stdout.read().decode('utf-8', errors='ignore')

        if SSH_HOST in output:
            print(f"    DNS verified: {DOMAIN} -> {SSH_HOST}")
        else:
            print(f"    WARNING: DNS not yet propagated for {DOMAIN}")
            print("    Waiting 30 seconds...")
            time.sleep(30)

        print("\n[3/4] Uploading SSL installation script...")
        sftp = ssh.open_sftp()
        sftp.put(str(local_script), r"C:\whatsappbridge\deploy\install-ssl-certificate.ps1")
        sftp.close()
        print("    Script uploaded")

        print("\n[4/4] Running SSL installation (this may take 2-3 minutes)...")
        stdin, stdout, stderr = ssh.exec_command(
            f'powershell -ExecutionPolicy Bypass -File C:\\whatsappbridge\\deploy\\install-ssl-certificate.ps1 -Domain {DOMAIN} -Email {EMAIL}',
            get_pty=True
        )

        # Print output in real-time
        for line in stdout:
            line_str = line.rstrip()
            # Filter ANSI escape codes but keep important output
            if line_str and '?25' not in line_str:
                # Remove color codes but keep text
                import re
                clean_line = re.sub(r'\x1b\[[0-9;]*[a-zA-Z]', '', line_str)
                if clean_line.strip():
                    print(clean_line)

        print("\n" + "="*70)
        print("SSL Installation Complete!")
        print("="*70)
        print()
        print(f"Your application is now accessible via HTTPS:")
        print(f"  https://{DOMAIN}")
        print(f"  https://{DOMAIN}:5001/swagger")
        print()
        print("The certificate will auto-renew every 60 days.")
        print()

        ssh.close()

    except Exception as e:
        print(f"\nError: {e}")
        import traceback
        traceback.print_exc()

if __name__ == "__main__":
    main()
