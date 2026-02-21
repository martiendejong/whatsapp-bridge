#!/usr/bin/env python3
"""Simple deployment - copy files directly via SSH commands"""

import paramiko
import os
from pathlib import Path

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"
DOMAIN = "whatsapp.wreckingball.ai"

def exec_cmd(ssh, cmd, description=""):
    """Execute command and return output"""
    if description:
        print(f"\n{description}")
    stdin, stdout, stderr = ssh.exec_command(cmd, get_pty=True)
    output = stdout.read().decode('utf-8', errors='ignore')
    print(output)
    return output

def main():
    print("="*70)
    print("WhatsApp Bridge - Simple Deployment")
    print("="*70)

    project_root = Path(__file__).parent.parent

    try:
        print("\n[1/7] Connecting to server...")
        ssh = paramiko.SSHClient()
        ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
        ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD)
        print(f"    Connected to {SSH_HOST}")

        print("\n[2/7] Checking if project already exists locally on server...")
        # Check if files exist in current directory structure
        result = exec_cmd(ssh, 'dir "E:\\projects\\whatsappbridge" 2>&1 | find "File(s)"')

        if "File(s)" in result and "0 File(s)" not in result:
            print("    Project files found on E: drive")

            print("\n[3/7] Copying from E: to C: drive...")
            exec_cmd(ssh, 'powershell -Command "Copy-Item -Path E:\\projects\\whatsappbridge -Destination C:\\whatsappbridge -Recurse -Force"',
                    "Copying project files")
        else:
            print("    E: drive not accessible or files not found")
            print("\n[3/7] Creating minimal project structure...")

            # Create basic structure
            exec_cmd(ssh, 'mkdir C:\\whatsappbridge\\Backend 2>nul')
            exec_cmd(ssh, 'mkdir C:\\whatsappbridge\\Frontend 2>nul')
            exec_cmd(ssh, 'mkdir C:\\whatsappbridge\\WhatsAppService 2>nul')
            exec_cmd(ssh, 'mkdir C:\\whatsappbridge\\deploy 2>nul')

            print("    WARNING: Project files need to be uploaded manually")
            print("    You can copy from E:\\projects\\whatsappbridge if available on server")

        print("\n[4/7] Installing prerequisites...")

        # Install .NET 9
        print("  Checking .NET...")
        exec_cmd(ssh, 'dotnet --version 2>nul || echo Not installed')

        # Install Node.js
        print("  Checking Node.js...")
        exec_cmd(ssh, 'node --version 2>nul || echo Not installed')

        # Install IIS
        print("  Installing IIS...")
        exec_cmd(ssh, 'powershell -Command "Install-WindowsFeature -Name Web-Server -IncludeManagementTools"')

        # Install NSSM if needed
        print("  Checking NSSM...")
        exec_cmd(ssh, 'where nssm 2>nul || echo Not installed')

        print("\n[5/7] Deployment would continue with:")
        print("  - WhatsApp Service deployment")
        print("  - Backend API deployment")
        print("  - Frontend deployment")
        print("  - IIS configuration")

        print("\n[6/7] Getting server IP...")
        result = exec_cmd(ssh, 'powershell -Command "(Test-Connection -ComputerName (hostname) -Count 1).IPV4Address.IPAddressToString"')
        server_ip = result.strip().split('\n')[-1].strip() if result else SSH_HOST

        print("\n[7/7] DNS Configuration:")
        print("="*70)
        print("\nAdd this A record to wreckingball.ai DNS:")
        print()
        print("  Type:  A")
        print("  Name:  whatsapp")
        print(f"  Value: {server_ip}")
        print("  TTL:   3600")
        print()
        print("="*70)
        print(f"\nAfter DNS propagation:")
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
