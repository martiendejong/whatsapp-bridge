#!/usr/bin/env python3
"""Check backend API deployment"""

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

print("Checking backend API deployment...\n")

# Check if backend files exist
print("Backend API Files:")
out = run_cmd(ssh, r'powershell -Command "Test-Path C:\inetpub\whatsappbridge-api"')
print(f"  Directory exists: {out}")

if "True" in out:
    out = run_cmd(ssh, r'powershell -Command "(Get-ChildItem C:\inetpub\whatsappbridge-api -File).Count"')
    print(f"  File count: {out}")

    out = run_cmd(ssh, r'powershell -Command "Get-ChildItem C:\inetpub\whatsappbridge-api\*.dll | Select-Object -First 5 Name"')
    print(f"  DLL files:\n{out}")

# Check if IIS site exists for API
print("\nIIS API Site:")
out = run_cmd(ssh, r'powershell -Command "Get-Website | Where-Object { $_.Name -like \"*API*\" -and $_.Name -like \"*WhatsApp*\" }"')
if out:
    print(f"  {out}")
else:
    print("  No WhatsApp API site found in IIS")

# Check app pools
print("\nApp Pools:")
out = run_cmd(ssh, r'powershell -Command "Get-IISAppPool | Where-Object { $_.Name -like \"*WhatsApp*\" } | Format-Table Name,State"')
print(f"  {out if out else 'No WhatsApp app pools found'}")

# Check if backend service/process running
print("\nRunning Processes:")
out = run_cmd(ssh, r'powershell -Command "Get-Process | Where-Object { $_.ProcessName -match \"WhatsApp|dotnet\" -and $_.Path -like \"*whatsapp*\" } | Select-Object Id,ProcessName,Path"')
print(f"  {out if out else 'No WhatsApp backend processes'}")

ssh.close()
