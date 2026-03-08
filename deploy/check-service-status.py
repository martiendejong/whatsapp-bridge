#!/usr/bin/env python3
"""Check WhatsApp Service status and logs"""

import paramiko

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"

ssh = paramiko.SSHClient()
ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD)

print("="*70)
print("WhatsApp Service Status Check")
print("="*70)

# Check if process is running
print("\n1. Checking Node.js process...")
stdin, stdout, stderr = ssh.exec_command('powershell -Command "Get-Process node -ErrorAction SilentlyContinue | Format-Table Id,ProcessName,StartTime"')
output = stdout.read().decode('utf-8', errors='ignore').strip()
if output:
    print(output)
else:
    print("   No Node.js process found!")

# Check port
print("\n2. Checking port 3000...")
stdin, stdout, stderr = ssh.exec_command('powershell -Command "netstat -ano | findstr :3000"')
output = stdout.read().decode('utf-8', errors='ignore').strip()
if output:
    print("   Port 3000 is LISTENING")
else:
    print("   Port 3000 NOT listening!")

# Check error log
print("\n3. Recent error log (last 50 lines)...")
stdin, stdout, stderr = ssh.exec_command('powershell -Command "Get-Content C:\\Services\\WhatsAppBridge\\logs\\service-error.log -Tail 50 -ErrorAction SilentlyContinue"')
errors = stdout.read().decode('utf-8', errors='ignore').strip()
if errors:
    print(errors)
else:
    print("   No errors logged")

# Check service log
print("\n4. Recent service log (last 30 lines)...")
stdin, stdout, stderr = ssh.exec_command('powershell -Command "Get-Content C:\\Services\\WhatsAppBridge\\logs\\service.log -Tail 30 -ErrorAction SilentlyContinue"')
logs = stdout.read().decode('utf-8', errors='ignore').strip()
if logs:
    print(logs)
else:
    print("   No service logs")

# Test health endpoint
print("\n5. Testing service health...")
stdin, stdout, stderr = ssh.exec_command('powershell -Command "try { $r = Invoke-WebRequest -Uri http://localhost:3000/health -UseBasicParsing -TimeoutSec 3; Write-Host \\"Status: $($r.StatusCode)\\"; Write-Host \\"Body: $($r.Content)\\" } catch { Write-Host \\"ERROR: $($_.Exception.Message)\\" }"')
health = stdout.read().decode('utf-8', errors='ignore').strip()
print(health)

# Test sessions endpoint
print("\n6. Testing sessions endpoint...")
stdin, stdout, stderr = ssh.exec_command('powershell -Command "try { $r = Invoke-WebRequest -Uri http://localhost:3000/sessions -UseBasicParsing -TimeoutSec 3; Write-Host \\"Status: $($r.StatusCode)\\" } catch { Write-Host \\"ERROR: $($_.Exception.Message)\\" }"')
sessions = stdout.read().decode('utf-8', errors='ignore').strip()
print(sessions)

ssh.close()

print("\n" + "="*70)
