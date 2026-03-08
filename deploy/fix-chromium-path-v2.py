#!/usr/bin/env python3
"""Fix Chromium path with proper escaping"""

import paramiko
import re

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"

ssh = paramiko.SSHClient()
ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD)

print("="*70)
print("Fixing Chromium Path (v2 - Proper Escaping)")
print("="*70)

# Get Chromium path
print("\n1. Finding Chromium executable...")
stdin, stdout, stderr = ssh.exec_command(
    'powershell -Command "Get-ChildItem -Path C:\\Users\\Administrator\\.cache\\puppeteer -Filter chrome.exe -Recurse | Select-Object -First 1 -ExpandProperty FullName"'
)
chromium_path = stdout.read().decode('utf-8', errors='ignore').strip()
print(f"   Found: {chromium_path}")

# Convert Windows path to forward slashes for JavaScript
chromium_path_js = chromium_path.replace('\\', '/')
print(f"   JS path: {chromium_path_js}")

# Read current index.js
print("\n2. Reading index.js...")
sftp = ssh.open_sftp()
with sftp.file('/C:/Services/WhatsAppBridge/index.js', 'r') as f:
    content = f.read().decode('utf-8')

# Find and update the puppeteer config
print("\n3. Updating puppeteer config...")
pattern = r"(puppeteer: \{[^}]*args: \['--no-sandbox', '--disable-setuid-sandbox'\])"

# Check if executablePath already exists
if 'executablePath:' in content:
    # Replace existing path
    print("   Replacing existing executablePath...")
    pattern2 = r"executablePath: '[^']*'"
    updated_content = re.sub(pattern2, f"executablePath: '{chromium_path_js}'", content)
else:
    # Add new executablePath
    print("   Adding new executablePath...")
    replacement = f"\\1,\n                executablePath: '{chromium_path_js}'"
    updated_content = re.sub(pattern, replacement, content)

# Write back
print("\n4. Writing updated index.js...")
with sftp.file('/C:/Services/WhatsAppBridge/index.js', 'w') as f:
    f.write(updated_content)

sftp.close()

# Restart service
print("\n5. Restarting service...")
ssh.exec_command('powershell -Command "Get-Process node -ErrorAction SilentlyContinue | Stop-Process -Force"')
import time
time.sleep(2)

ssh.exec_command('powershell -Command "Start-ScheduledTask -TaskName WhatsAppBridgeService"')
time.sleep(5)

# Verify
print("\n6. Verifying service...")
stdin, stdout, stderr = ssh.exec_command('powershell -Command "netstat -ano | findstr :3000"')
port = stdout.read().decode('utf-8', errors='ignore').strip()

if port:
    print("   Service running on port 3000")

    # Check logs
    print("\n7. Checking for errors...")
    time.sleep(3)
    stdin, stdout, stderr = ssh.exec_command('type C:\\Services\\WhatsAppBridge\\logs\\service.log')
    logs = stdout.read().decode('utf-8', errors='ignore').strip()
    last_lines = logs.split('\n')[-15:]

    for line in last_lines:
        if 'Error' in line or 'error' in line:
            print(f"   {line}")
        elif 'WhatsApp Bridge Service running' in line:
            print(f"   {line}")
        elif 'executablePath' in line or 'chrome' in line.lower():
            print(f"   {line}")

ssh.close()

print("\n" + "="*70)
print("index.js updated with proper path!")
print(f"executablePath: '{chromium_path_js}'")
print("="*70)
