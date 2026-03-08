#!/usr/bin/env python3
"""Diagnose service crash"""

import paramiko

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"

def run_cmd(ssh, cmd):
    stdin, stdout, stderr = ssh.exec_command(cmd)
    return stdout.read().decode('utf-8', errors='ignore').strip()

ssh = paramiko.SSHClient()
ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD)

print("Checking full service logs...\n")

# Get ALL log content
out = run_cmd(ssh, r'powershell -Command "Get-Content C:\Services\WhatsAppBridge\logs\service.log -ErrorAction SilentlyContinue"')
print("Full service log:")
print("="*70)
print(out if out else "Empty or not found")
print("="*70)

# Try to start service with verbose logging
print("\nRestarting service with console output...\n")

SCRIPT = r"""
cd C:\Services\WhatsAppBridge

# Kill any existing
Get-Process node -ErrorAction SilentlyContinue | Stop-Process -Force

# Start with output to console
& "C:\Program Files\nodejs\node.exe" index.js 2>&1
"""

# Execute and capture output for 10 seconds
stdin, stdout, stderr = ssh.exec_command(f'powershell -Command "{SCRIPT}"', timeout=10)

try:
    output = stdout.read(size=4096).decode('utf-8', errors='ignore')
    print("Service startup output:")
    print(output[:2000])  # First 2000 chars
except:
    print("Timeout waiting for output (service might be running in background)")

ssh.close()

print("\n" + "="*70)
print("Check if we see any errors in the startup output above")
print("="*70)
