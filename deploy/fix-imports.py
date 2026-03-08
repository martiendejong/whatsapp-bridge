#!/usr/bin/env python3
"""Fix imports in index.js"""

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

print("Downloading index.js...\n")

# Download current index.js
sftp = ssh.open_sftp()
with sftp.file('C:\\Services\\WhatsAppBridge\\index.js', 'r') as f:
    content = f.read().decode('utf-8')

print("Current first 10 lines:")
lines = content.split('\n')
for i, line in enumerate(lines[:10], 1):
    print(f"  {i}: {line}")

# Fix import statement
print("\nFixing import...")
new_content = content.replace(
    "import { Client, LocalAuth } from 'whatsapp-web.js';",
    "import pkg from 'whatsapp-web.js';\nconst { Client, LocalAuth } = pkg;"
)

# Upload fixed version
with sftp.file('C:\\Services\\WhatsAppBridge\\index.js', 'w') as f:
    f.write(new_content.encode('utf-8'))

sftp.close()

print("Fixed import statement")

# Restart service
print("\nRestarting service...")
out = run_cmd(ssh, r'''powershell -Command "Get-Process node -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '*nodejs*' } | Stop-Process -Force; Start-Sleep -Seconds 2; Start-Process -FilePath 'C:\Services\WhatsAppBridge\start.bat' -WorkingDirectory 'C:\Services\WhatsAppBridge' -WindowStyle Hidden; Start-Sleep -Seconds 5; netstat -ano | findstr ':3000' | findstr 'LISTENING'"''')

if out:
    print("SUCCESS! Port 3000 is listening:")
    print(f"  {out}")
else:
    print("Port not listening. Checking logs...")
    out = run_cmd(ssh, r'powershell -Command "Get-Content C:\Services\WhatsAppBridge\logs\service.log -Tail 10"')
    print(out)

ssh.close()

print("\n" + "="*70)
print("Import fix applied!")
print("\nTry connecting WhatsApp now - QR code should appear")
print("="*70)
