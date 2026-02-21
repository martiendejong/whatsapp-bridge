#!/usr/bin/env python3
"""Check installation status on server"""

import paramiko

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"

def check_cmd(ssh, cmd, description):
    """Execute command and return output"""
    print(f"\n{description}")
    stdin, stdout, stderr = ssh.exec_command(cmd, get_pty=True)
    output = stdout.read().decode('utf-8', errors='ignore')
    # Clean up ANSI codes
    lines = [line.strip() for line in output.split('\n')
             if line.strip() and '?25' not in line and '\x1b[' not in line[:10]]
    for line in lines[:20]:  # Limit output
        print(f"  {line}")
    return output

def main():
    print("="*70)
    print("WhatsApp Bridge - Installation Status Check")
    print("="*70)

    try:
        ssh = paramiko.SSHClient()
        ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
        ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD)
        print(f"\nConnected to {SSH_HOST}")

        # Check if WhatsApp service is installed
        check_cmd(ssh, 'sc query WhatsAppBridgeService',
                  "WhatsApp Service Status:")

        # Check IIS sites
        check_cmd(ssh, 'powershell -Command "Get-Website | Where-Object { $_.Name -like \'*WhatsApp*\' } | Format-Table Name,State,PhysicalPath"',
                  "IIS Sites:")

        # Check if ports are listening
        check_cmd(ssh, 'netstat -an | findstr "3000 5000 80"',
                  "Listening Ports:")

        print("\n" + "="*70)
        print("Status check complete")
        print("="*70)

        ssh.close()

    except Exception as e:
        print(f"\nError: {e}")

if __name__ == "__main__":
    main()
