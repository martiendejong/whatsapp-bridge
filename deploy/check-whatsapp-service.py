#!/usr/bin/env python3
"""Check if WhatsApp Service is running"""

import paramiko

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "SpaceElevator1tam!"

def run_cmd(ssh, cmd):
    stdin, stdout, stderr = ssh.exec_command(cmd)
    return stdout.read().decode('utf-8', errors='ignore').strip()

ssh = paramiko.SSHClient()
ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD)

print("Checking WhatsApp Service...\n")

# Check if service exists
print("Windows Service Status:")
out = run_cmd(ssh, 'sc query WhatsAppBridgeService')
print(out if out else "  Service not found")

# Check if Node.js process running
print("\n" + "="*70)
print("Node.js Processes:")
out = run_cmd(ssh, r'powershell -Command "Get-Process node -ErrorAction SilentlyContinue | Select-Object Id,ProcessName,Path"')
print(out if out else "  No Node.js processes running")

# Check if port 3000 is listening
print("\n" + "="*70)
print("Port 3000 Status:")
out = run_cmd(ssh, 'netstat -ano | findstr ":3000" | findstr "LISTENING"')
print(out if out else "  Port 3000 NOT listening")

# Check WhatsApp service directory
print("\n" + "="*70)
print("WhatsApp Service Files:")
out = run_cmd(ssh, r'powershell -Command "Test-Path C:\Services\WhatsAppBridge"')
if "True" in out:
    print("  Directory exists: C:\\Services\\WhatsAppBridge")
    out = run_cmd(ssh, r'powershell -Command "(Get-ChildItem C:\Services\WhatsAppBridge -File).Count"')
    print(f"  File count: {out}")
else:
    print("  Directory NOT found")

# Check NSSM installation
print("\n" + "="*70)
print("NSSM (Service Wrapper):")
out = run_cmd(ssh, 'where nssm')
print(f"  NSSM location: {out}" if out else "  NSSM not installed")

ssh.close()

print("\n" + "="*70)
print("Summary:")
print("  If service is not running, we need to install and start it")
print("="*70)
