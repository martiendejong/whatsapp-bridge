#!/usr/bin/env python3
"""Check WhatsApp service logs"""

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

print("Checking WhatsApp Service logs...\n")

# Check if log file exists
out = run_cmd(ssh, r'powershell -Command "Test-Path C:\Services\WhatsAppBridge\logs\service.log"')
if "True" in out:
    # Read log
    out = run_cmd(ssh, r'powershell -Command "Get-Content C:\Services\WhatsAppBridge\logs\service.log -ErrorAction SilentlyContinue"')
    print("Service Log:")
    print(out if out else "  Empty log file")
else:
    print("Log file not found")

print("\n" + "="*70)

# Check if index.js exists
out = run_cmd(ssh, r'powershell -Command "Get-ChildItem C:\Services\WhatsAppBridge\*.js | Select-Object Name"')
print("JavaScript files in service directory:")
print(out if out else "  No JS files found")

print("\n" + "="*70)

# List all files
out = run_cmd(ssh, r'powershell -Command "Get-ChildItem C:\Services\WhatsAppBridge | Select-Object Name,Length"')
print("All files in C:\\Services\\WhatsAppBridge:")
print(out)

print("\n" + "="*70)

# Check if node_modules exists
out = run_cmd(ssh, r'powershell -Command "Test-Path C:\Services\WhatsAppBridge\node_modules"')
print(f"node_modules exists: {out}")

ssh.close()
