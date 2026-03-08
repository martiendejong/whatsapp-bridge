#!/usr/bin/env python3
"""Fix routes to match API expectations"""

import paramiko

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"

ssh = paramiko.SSHClient()
ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD)

print("Fixing WhatsApp Service routes...\n")

# Download current index.js
sftp = ssh.open_sftp()
with sftp.file('C:\\Services\\WhatsAppBridge\\index.js', 'r') as f:
    content = f.read().decode('utf-8')

# Fix routes: singular → plural
fixes = [
    ("app.post('/session/create'", "app.post('/sessions/create'"),
    ("app.get('/session/:sessionId/status'", "app.get('/sessions/:sessionId/status'"),
    ("app.delete('/session/:sessionId'", "app.delete('/sessions/:sessionId'"),
    ("app.post('/message/send'", "app.post('/messages/send'"),
    ("app.post('/message/sendMedia'", "app.post('/messages/sendMedia'"),
]

for old, new in fixes:
    if old in content:
        content = content.replace(old, new)
        print(f"  Fixed: {old} -> {new}")

# Upload fixed version
with sftp.file('C:\\Services\\WhatsAppBridge\\index.js', 'w') as f:
    f.write(content.encode('utf-8'))

sftp.close()

print("\nRestarting service...")

# Restart service
stdin, stdout, stderr = ssh.exec_command(r'''powershell -Command "Get-Process node -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep -Seconds 2; Start-Process -FilePath 'C:\Services\WhatsAppBridge\start.bat' -WorkingDirectory 'C:\Services\WhatsAppBridge' -WindowStyle Hidden; Start-Sleep -Seconds 5; netstat -ano | findstr ':3000' | findstr 'LISTENING'"''')

out = stdout.read().decode('utf-8', errors='ignore').strip()
if out:
    print(f"  Service running: {out.split()[0]}")
else:
    print("  WARNING: Service might not be running")

ssh.close()

print("\n" + "="*70)
print("Routes fixed!")
print("\nNow try connecting WhatsApp again - it should work!")
print("="*70)
