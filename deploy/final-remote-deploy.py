#!/usr/bin/env python3
"""Final deployment - execute installation script remotely"""

import paramiko
import base64

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"
DOMAIN = "whatsapp.wreckingball.ai"

# Read the installation script
with open("INSTALL_ON_SERVER.ps1", "r", encoding="utf-8") as f:
    install_script = f.read()

def main():
    print("="*70)
    print("WhatsApp Bridge - Final Deployment")
    print("="*70)

    try:
        print("\n[1/3] Connecting to server...")
        ssh = paramiko.SSHClient()
        ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
        ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD)
        print(f"    Connected to {SSH_HOST}")

        # Encode the script in base64 to avoid escaping issues
        script_b64 = base64.b64encode(install_script.encode('utf-8')).decode('ascii')

        print("\n[2/3] Uploading and executing installation script...")
        print("    This will take 15-20 minutes...")

        remote_command = f"""
$script = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('{script_b64}'))
$script | Out-File -FilePath C:\\install-whatsappbridge.ps1 -Encoding UTF8
powershell -ExecutionPolicy Bypass -File C:\\install-whatsappbridge.ps1 -Domain '{DOMAIN}'
"""

        stdin, stdout, stderr = ssh.exec_command(remote_command, get_pty=True)

        # Print output in real-time
        for line in stdout:
            line_str = line.rstrip()
            # Filter out ANSI escape codes and empty lines
            if line_str and not line_str.startswith('\x1b'):
                print(f"    {line_str}")

        print("\n[3/3] Deployment complete!")

        print("\n" + "="*70)
        print("DNS RECORD TOEVOEGEN")
        print("="*70)
        print("\nVoeg dit A record toe aan je wreckingball.ai DNS:")
        print()
        print("  Type:  A")
        print("  Name:  whatsapp")
        print("  Value: 85.215.217.154")
        print("  TTL:   3600")
        print()
        print("=" *70)
        print("\nNa DNS propagatie (5-30 minuten):")
        print(f"  Website: http://{DOMAIN}")
        print(f"  API:     http://{DOMAIN}:5000/swagger")
        print()
        print("Login en gebruik:")
        print("  1. Ga naar http://{DOMAIN}")
        print("  2. Klik op 'Register' en maak een account")
        print("  3. Maak een API connection (krijg een token)")
        print("  4. Scan QR code met je WhatsApp")
        print("  5. Gebruik de API endpoints")
        print()

        ssh.close()

    except Exception as e:
        print(f"\nError: {e}")
        import traceback
        traceback.print_exc()

if __name__ == "__main__":
    main()
