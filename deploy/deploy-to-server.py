#!/usr/bin/env python3
"""Deploy WhatsApp Bridge to Windows server"""

import paramiko
import sys
import os
from pathlib import Path
from scp import SCPClient

# SSH Configuration
SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"
DOMAIN = "whatsapp.wreckingball.ai"

def create_ssh_client():
    """Create and return SSH client"""
    ssh = paramiko.SSHClient()
    ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD)
    return ssh

def execute_command(ssh, command, description=""):
    """Execute command and print output"""
    if description:
        print(f"\n[*] {description}")
    print(f"    Command: {command[:80]}...")

    stdin, stdout, stderr = ssh.exec_command(command, get_pty=True)

    # Read output in real-time
    for line in stdout:
        print(f"    {line.rstrip()}")

    error = stderr.read().decode('utf-8')
    if error:
        print(f"    Error: {error}")

    return stdout.channel.recv_exit_status()

def upload_files(ssh, local_path, remote_path):
    """Upload directory to server"""
    print(f"\n[*] Uploading {local_path} to {remote_path}")

    with SCPClient(ssh.get_transport()) as scp:
        scp.put(local_path, remote_path, recursive=True)

    print(f"    Upload complete!")

def main():
    print("="*60)
    print("WhatsApp Bridge - Deployment to wreckingball.ai")
    print("="*60)

    project_root = Path(__file__).parent.parent

    try:
        # Connect to server
        print("\n[1/6] Connecting to server...")
        ssh = create_ssh_client()
        print(f"    Connected to {SSH_HOST}")

        # Create remote directory
        print("\n[2/6] Creating remote directory...")
        execute_command(ssh, "mkdir C:\\whatsappbridge 2>nul || echo Directory exists")

        # Upload project files
        print("\n[3/6] Uploading project files...")
        # Upload Backend
        upload_files(ssh, str(project_root / "Backend"), "C:\\whatsappbridge\\Backend")
        # Upload WhatsApp Service
        upload_files(ssh, str(project_root / "WhatsAppService"), "C:\\whatsappbridge\\WhatsAppService")
        # Upload Frontend
        upload_files(ssh, str(project_root / "Frontend"), "C:\\whatsappbridge\\Frontend")
        # Upload deployment scripts
        upload_files(ssh, str(project_root / "deploy"), "C:\\whatsappbridge\\deploy")
        # Upload tools
        upload_files(ssh, str(project_root / "tools"), "C:\\whatsappbridge\\tools")

        # Install prerequisites
        print("\n[4/6] Installing prerequisites...")
        execute_command(ssh,
            "powershell -ExecutionPolicy Bypass -File C:\\whatsappbridge\\deploy\\install-prerequisites.ps1",
            "Installing .NET, Node.js, IIS, etc.")

        # Deploy WhatsApp Bridge
        print("\n[5/6] Deploying WhatsApp Bridge...")
        deploy_cmd = f"powershell -ExecutionPolicy Bypass -Command \"cd C:\\whatsappbridge\\deploy; .\\deploy-wreckingball.ps1 -Domain '{DOMAIN}'\""
        execute_command(ssh, deploy_cmd, "Running deployment script")

        # Get server IP for DNS record
        print("\n[6/6] Getting server IP for DNS configuration...")
        stdin, stdout, stderr = ssh.exec_command("powershell -Command \"(Test-Connection -ComputerName $env:COMPUTERNAME -Count 1).IPV4Address.IPAddressToString\"")
        server_ip = stdout.read().decode('utf-8').strip()

        print("\n" + "="*60)
        print("Deployment Complete!")
        print("="*60)
        print("\nDNS Configuration Required:")
        print("===========================")
        print("\nAdd this A record to wreckingball.ai DNS:")
        print(f"\nType:  A")
        print(f"Name:  whatsapp")
        print(f"Value: {server_ip or SSH_HOST}")
        print(f"TTL:   3600")
        print("\nAfter DNS propagation:")
        print(f"  Web:     http://{DOMAIN}")
        print(f"  API:     http://{DOMAIN}/api")
        print(f"  Swagger: http://{DOMAIN}/api/swagger")
        print("\nNext Steps:")
        print("1. Add DNS record (see above)")
        print("2. Wait 5-30 minutes for DNS propagation")
        print(f"3. Test: http://{DOMAIN}")
        print("4. Install SSL certificate (recommended)")
        print("5. Enable encryption in server config")
        print()

        ssh.close()

    except Exception as e:
        print(f"\nError: {e}")
        sys.exit(1)

if __name__ == "__main__":
    main()
