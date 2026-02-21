#!/usr/bin/env python3
"""Deploy WhatsApp Bridge - download project on server and deploy"""

import paramiko
import time

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"
DOMAIN = "whatsapp.wreckingball.ai"

def execute_command(ssh, command, description=""):
    """Execute command and print output"""
    if description:
        print(f"\n[*] {description}")

    stdin, stdout, stderr = ssh.exec_command(command, get_pty=True)

    # Read output in real-time
    for line in stdout:
        print(f"    {line.rstrip()}")

    return stdout.channel.recv_exit_status()

def main():
    print("="*70)
    print("WhatsApp Bridge - Remote Deployment to whatsapp.wreckingball.ai")
    print("="*70)

    try:
        print("\n[1/7] Connecting to server...")
        ssh = paramiko.SSHClient()
        ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
        ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD, timeout=30)
        print(f"    Connected to {SSH_HOST}")

        print("\n[2/7] Creating deployment directory...")
        execute_command(ssh, "mkdir C:\\whatsappbridge 2>nul || echo OK")

        print("\n[3/7] Copying project from local machine...")
        execute_command(ssh,
            "powershell -Command \"Copy-Item -Path 'E:\\projects\\whatsappbridge\\*' -Destination 'C:\\whatsappbridge\\' -Recurse -Force\"",
            "Copying from E: drive")

        print("\n[4/7] Installing prerequisites (this may take 10-15 minutes)...")
        execute_command(ssh,
            "powershell -ExecutionPolicy Bypass -File C:\\whatsappbridge\\deploy\\install-prerequisites.ps1")

        print("\n[5/7] Deploying WhatsApp Service...")
        execute_command(ssh,
            "powershell -ExecutionPolicy Bypass -File C:\\whatsappbridge\\deploy\\deploy-whatsapp-service.ps1")

        print("\n[6/7] Deploying Backend API...")
        execute_command(ssh,
            "powershell -ExecutionPolicy Bypass -File C:\\whatsappbridge\\deploy\\deploy-backend.ps1")

        print("\n[7/7] Deploying Frontend...")
        deploy_cmd = f"powershell -ExecutionPolicy Bypass -File C:\\whatsappbridge\\deploy\\deploy-frontend.ps1 -ApiUrl 'http://{DOMAIN}:5000'"
        execute_command(ssh, deploy_cmd)

        # Configure IIS bindings for domain
        print("\n[*] Configuring IIS for custom domain...")
        commands = [
            f"powershell -Command \"Import-Module WebAdministration; New-WebBinding -Name 'WhatsAppBridgeWeb' -Protocol http -Port 80 -HostHeader '{DOMAIN}' -Force\"",
            "powershell -Command \"iisreset /noforce\""
        ]
        for cmd in commands:
            execute_command(ssh, cmd)

        print("\n" + "="*70)
        print("Deployment Complete!")
        print("="*70)
        print("\nDNS Configuration Required:")
        print("="*70)
        print("\nAdd this A record to your wreckingball.ai DNS:")
        print()
        print(f"  Type:  A")
        print(f"  Name:  whatsapp")
        print(f"  Value: {SSH_HOST}")
        print(f"  TTL:   3600 (or 1 hour)")
        print()
        print("="*70)
        print("\nAfter DNS propagation (5-30 minutes):")
        print(f"  Application: http://{DOMAIN}")
        print(f"  API Docs:    http://{DOMAIN}:5000/swagger")
        print()
        print("Next Steps:")
        print("  1. Add the DNS record above")
        print("  2. Wait for DNS to propagate")
        print(f"  3. Visit http://{DOMAIN}")
        print("  4. Register your account")
        print("  5. Create API connection")
        print("  6. Connect WhatsApp via QR code")
        print()
        print("Optional (recommended):")
        print("  - Install SSL certificate for HTTPS")
        print("  - Enable encryption (run generate-encryption-keys.ps1)")
        print()

        ssh.close()

    except Exception as e:
        print(f"\nDeployment failed: {e}")
        import traceback
        traceback.print_exc()

if __name__ == "__main__":
    main()
