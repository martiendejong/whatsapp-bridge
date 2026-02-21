#!/usr/bin/env python3
"""Run the installation script on the server"""

import paramiko

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"
DOMAIN = "whatsapp.wreckingball.ai"

def main():
    print("="*70)
    print("Running WhatsApp Bridge Installation")
    print("="*70)

    try:
        print("\n[1/2] Connecting to server...")
        ssh = paramiko.SSHClient()
        ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
        ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD)
        print(f"    Connected to {SSH_HOST}")

        print("\n[2/2] Running installation (this will take 15-20 minutes)...")
        stdin, stdout, stderr = ssh.exec_command(
            f'powershell -ExecutionPolicy Bypass -File C:\\whatsappbridge\\deploy\\INSTALL_ON_SERVER.ps1 -Domain {DOMAIN}',
            get_pty=True
        )

        # Print output
        for line in stdout:
            line_str = line.rstrip()
            # Filter ANSI escape codes
            if line_str and not any(x in line_str for x in ['\x1b', '\x1b[', '[?25']):
                print(line_str)

        print("\n" + "="*70)
        print("Installation Complete!")
        print("="*70)
        print("\nDNS Record to Add:")
        print("  Type:  A")
        print("  Name:  whatsapp")
        print(f"  Value: {SSH_HOST}")
        print("  TTL:   3600")
        print()
        print(f"After DNS propagation:")
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
