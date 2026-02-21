#!/usr/bin/env python3
import paramiko
import time

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"

print("="*70)
print("FINAL STATUS CHECK")
print("="*70)

ssh = paramiko.SSHClient()
ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD)

# Run the installation (silent except errors)
print("\nRunning installation (this will take 3-5 minutes)...")
sftp = ssh.open_sftp()
sftp.put("E:/projects/whatsappbridge/deploy/install-with-errors-shown.ps1", "C:/Temp/install-final.ps1")
sftp.close()

stdin, stdout, stderr = ssh.exec_command('powershell -ExecutionPolicy Bypass -File C:\\Temp\\install-final.ps1 2>&1')
stdout.read()  # Wait for completion
time.sleep(5)

# Check status
print("\nChecking services...")
stdin, stdout, stderr = ssh.exec_command('netstat -an | findstr "LISTENING" | findstr ":80 :3000 :5000"')
output = stdout.read().decode('utf-8', errors='ignore')

ports = []
if ':80 ' in output: ports.append('[OK] Port 80 (Frontend)')
if ':3000 ' in output: ports.append('[OK] Port 3000 (WhatsApp Service)')
if ':5000 ' in output: ports.append('[OK] Port 5000 (Backend API)')

if ports:
    for p in ports:
        print(p)
else:
    print("[WARN] No ports listening")

# Check if Frontend IIS site exists
stdin, stdout, stderr = ssh.exec_command('powershell Get-Website -Name WhatsAppBridgeWeb')
output = stdout.read().decode('utf-8', errors='ignore')
if 'WhatsAppBridgeWeb' in output:
    print("[OK] Frontend IIS site configured")

# Check if Backend IIS site exists
stdin, stdout, stderr = ssh.exec_command('powershell Get-Website -Name WhatsAppBridgeAPI')
output = stdout.read().decode('utf-8', errors='ignore')
if 'WhatsAppBridgeAPI' in output:
    print("[OK] Backend IIS site configured")

# Check Task Scheduler
stdin, stdout, stderr = ssh.exec_command('schtasks /Query /TN "WhatsAppBridgeService" /FO LIST')
output = stdout.read().decode('utf-8', errors='ignore')
if 'Ready' in output or 'Running' in output:
    print("[OK] WhatsApp Service scheduled task configured")

ssh.close()

print("\n" + "="*70)
print("Check: http://whatsapp.wreckingball.ai")
print("API:   http://whatsapp.wreckingball.ai:5000/swagger")
print("="*70)
