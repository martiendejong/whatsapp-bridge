#!/usr/bin/env python3
"""Restart WhatsApp Service and monitor startup"""

import paramiko
import time

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"

ssh = paramiko.SSHClient()
ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD)

print("="*70)
print("Restarting WhatsApp Service")
print("="*70)

# Check Task Scheduler task
print("\n1. Checking Task Scheduler task...")
stdin, stdout, stderr = ssh.exec_command('powershell -Command "Get-ScheduledTask -TaskName WhatsAppBridgeService -ErrorAction SilentlyContinue | Select-Object TaskName,State"')
task = stdout.read().decode('utf-8', errors='ignore').strip()
if task:
    print(task)
else:
    print("   Task not found!")

# Kill any existing processes
print("\n2. Killing any existing processes...")
ssh.exec_command('powershell -Command "Get-Process node -ErrorAction SilentlyContinue | Stop-Process -Force"')
time.sleep(2)

# Start the service manually
print("\n3. Starting service...")
ssh.exec_command('powershell -Command "Start-Process powershell -ArgumentList \\"-ExecutionPolicy Bypass -File C:\\\\Services\\\\WhatsAppBridge\\\\start-service.ps1 -Background\\" -WindowStyle Hidden"')

print("   Waiting 5 seconds for startup...")
time.sleep(5)

# Check if it's running
print("\n4. Checking if running...")
stdin, stdout, stderr = ssh.exec_command('powershell -Command "netstat -ano | findstr :3000"')
port = stdout.read().decode('utf-8', errors='ignore').strip()

if port:
    print("   SUCCESS - Port 3000 is listening!")

    # Try health check
    print("\n5. Testing health endpoint...")
    stdin, stdout, stderr = ssh.exec_command('powershell -Command "try { $r = Invoke-WebRequest -Uri http://localhost:3000/health -UseBasicParsing -TimeoutSec 5; Write-Host $r.Content } catch { Write-Host \\"ERROR: $($_.Exception.Message)\\" }"')
    health = stdout.read().decode('utf-8', errors='ignore').strip()
    print(f"   {health}")

    # Check error log
    print("\n6. Checking for errors...")
    time.sleep(2)
    stdin, stdout, stderr = ssh.exec_command('powershell -Command "Get-Content C:\\\\Services\\\\WhatsAppBridge\\\\logs\\\\service-error.log -Tail 20 -ErrorAction SilentlyContinue"')
    errors = stdout.read().decode('utf-8', errors='ignore').strip()
    if errors:
        print("   Errors found:")
        print(errors)
    else:
        print("   No errors")
else:
    print("   FAILED - Port 3000 not listening")

    # Check what went wrong
    print("\n5. Checking error log...")
    stdin, stdout, stderr = ssh.exec_command('powershell -Command "Get-Content C:\\\\Services\\\\WhatsAppBridge\\\\logs\\\\service-error.log -Tail 30 -ErrorAction SilentlyContinue"')
    errors = stdout.read().decode('utf-8', errors='ignore').strip()
    if errors:
        print(errors)
    else:
        print("   No error log found")

ssh.close()

print("\n" + "="*70)
