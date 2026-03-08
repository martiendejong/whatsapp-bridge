#!/usr/bin/env python3
"""Start IIS website"""

import paramiko
import time

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"

ssh = paramiko.SSHClient()
ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD)

print("="*70)
print("Starting IIS Website")
print("="*70)

print("\n1. Starting WhatsAppBridgeAPI website...")
stdin, stdout, stderr = ssh.exec_command('powershell -Command "Start-WebSite -Name WhatsAppBridgeAPI"')
stdout.read()

time.sleep(5)

print("\n2. Verifying status...")
stdin, stdout, stderr = ssh.exec_command('powershell -Command "Get-WebsiteState -Name WhatsAppBridgeAPI"')
state = stdout.read().decode('utf-8', errors='ignore').strip()
print(f"   Website state: {state}")

print("\n3. Checking ports...")
stdin, stdout, stderr = ssh.exec_command('powershell -Command "netstat -ano | findstr :5001"')
port_check = stdout.read().decode('utf-8', errors='ignore').strip()
if port_check:
    print(f"   Port 5001: LISTENING")
else:
    print(f"   Port 5001: NOT listening yet")

print("\n4. Testing health endpoint...")
time.sleep(2)
stdin, stdout, stderr = ssh.exec_command(
    'powershell -Command "try { $r = Invoke-WebRequest -Uri https://localhost:5001/api/health -UseBasicParsing -TimeoutSec 10; Write-Host \\"API Health: $($r.StatusCode)\\" } catch { Write-Host \\"ERROR: $($_.Exception.Message)\\" }"'
)
health = stdout.read().decode('utf-8', errors='ignore').strip()
print(f"   {health}")

ssh.close()

print("\n" + "="*70)
print("IIS website started!")
print("="*70)
