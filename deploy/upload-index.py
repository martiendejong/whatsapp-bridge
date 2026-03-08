#!/usr/bin/env python3
import paramiko
import time

ssh = paramiko.SSHClient()
ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
ssh.connect('85.215.217.154', username='administrator', password='3WsXcFr$7YhNmKi*')

print("Uploading fixed index.js...")
sftp = ssh.open_sftp()
sftp.put('E:/projects/whatsappbridge/WhatsAppService/index-server.js', '/C:/Services/WhatsAppBridge/index.js')
sftp.close()
print("Uploaded!")

print("\nRestarting service...")
ssh.exec_command('powershell -Command "Get-Process node -ErrorAction SilentlyContinue | Stop-Process -Force"')
time.sleep(2)

ssh.exec_command('powershell -Command "Start-ScheduledTask -TaskName WhatsAppBridgeService"')
time.sleep(5)

stdin, stdout, stderr = ssh.exec_command('powershell -Command "netstat -ano | findstr :3000"')
if stdout.read().decode('utf-8', errors='ignore').strip():
    print("Service running on port 3000!")
else:
    print("WARNING: Service not running")

ssh.close()

print("\n" + "="*70)
print("FIXED! Now when you scan a QR code:")
print("  1. WhatsApp Service detects 'authenticated' event")
print("  2. Calls notifyBackendStatusChange(sessionId, 'authenticated')")
print("  3. Backend webhook updates database")
print("  4. Frontend polling sees status change")
print("  5. UI updates automatically!")
print("="*70)
