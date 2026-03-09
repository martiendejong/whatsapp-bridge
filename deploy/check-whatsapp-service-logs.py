#!/usr/bin/env python3
"""Check WhatsApp Service logs"""

import paramiko

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "SpaceElevator1tam!"

ssh = paramiko.SSHClient()
ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD)

print("="*70)
print("WhatsApp Service Logs")
print("="*70)

# Check if service is running
print("\n1. Service Status:")
stdin, stdout, stderr = ssh.exec_command('powershell -Command "Get-Process node -ErrorAction SilentlyContinue | Format-Table Id,ProcessName,StartTime"')
output = stdout.read().decode('utf-8', errors='ignore').strip()
if output:
    print(output)
else:
    print("   Node.js NOT running!")

# Check port
print("\n2. Port 3000:")
stdin, stdout, stderr = ssh.exec_command('powershell -Command "netstat -ano | findstr :3000"')
port = stdout.read().decode('utf-8', errors='ignore').strip()
if port:
    print("   LISTENING")
else:
    print("   NOT listening!")

# Check service log
print("\n3. Service Log (last 30 lines):")
stdin, stdout, stderr = ssh.exec_command('powershell -Command "Get-Content C:\\Services\\WhatsAppBridge\\logs\\service.log -Tail 30 -ErrorAction SilentlyContinue"')
logs = stdout.read().decode('utf-8', errors='ignore').strip()
if logs:
    print(logs)
else:
    print("   No service log")

# Check error log
print("\n4. Error Log (last 30 lines):")
stdin, stdout, stderr = ssh.exec_command('powershell -Command "Get-Content C:\\Services\\WhatsAppBridge\\logs\\service-error.log -Tail 30 -ErrorAction SilentlyContinue"')
errors = stdout.read().decode('utf-8', errors='ignore').strip()
if errors:
    print(errors)
else:
    print("   No errors logged")

ssh.close()

print("\n" + "="*70)
