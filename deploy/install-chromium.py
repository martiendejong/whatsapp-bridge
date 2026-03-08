#!/usr/bin/env python3
"""Install Chromium for Puppeteer"""

import paramiko

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"

SCRIPT = r"""
cd C:\Services\WhatsAppBridge

Write-Host "Installing Chromium for Puppeteer..."
Write-Host "(This will download ~150MB, may take 2-3 minutes)"
Write-Host ""

# Install Chrome via puppeteer
npx puppeteer browsers install chrome

Write-Host ""
Write-Host "Chromium installation complete!"
"""

ssh = paramiko.SSHClient()
ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD)

print("="*70)
print("Installing Chromium for WhatsApp Service")
print("="*70)
print("\nThis will take 2-3 minutes to download ~150MB...\n")

# Upload script
remote_script = "C:\\whatsappbridge\\install-chromium.ps1"
sftp = ssh.open_sftp()
with sftp.file(remote_script, 'w') as f:
    f.write(SCRIPT)
sftp.close()

# Execute and show progress
stdin, stdout, stderr = ssh.exec_command(f'powershell -ExecutionPolicy Bypass -File "{remote_script}"', timeout=300)

for line in stdout:
    line = line.strip()
    if line:
        print(line)

print("\n" + "="*70)
print("Chromium installed!")
print("\nRestarting WhatsApp Service...")
print("="*70)

# Restart service
ssh.exec_command(r'''powershell -Command "Get-Process node -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep -Seconds 2; Start-Process -FilePath 'C:\Services\WhatsAppBridge\start.bat' -WorkingDirectory 'C:\Services\WhatsAppBridge' -WindowStyle Hidden"''')

print("\nService restarted. Try connecting WhatsApp now!")
print("The QR code should appear!")

ssh.close()
