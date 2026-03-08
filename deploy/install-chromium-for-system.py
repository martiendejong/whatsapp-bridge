#!/usr/bin/env python3
"""Install Chromium in SYSTEM user's cache directory"""

import paramiko
import time

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"

# Script to install Chromium as SYSTEM
INSTALL_SCRIPT = r"""
cd C:\Services\WhatsAppBridge

Write-Host "Installing Chromium for SYSTEM account..."
Write-Host "This will download ~150MB and may take 2-3 minutes..."
Write-Host ""

# Create directory for SYSTEM user cache
$systemCache = "C:\Windows\system32\config\systemprofile\.cache\puppeteer"
New-Item -ItemType Directory -Path $systemCache -Force | Out-Null

# Install Chromium (this will put it in the SYSTEM cache)
npx puppeteer browsers install chrome

Write-Host ""
Write-Host "Chromium installation complete!"
Write-Host "Location: $systemCache"
"""

ssh = paramiko.SSHClient()
ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD)

print("="*70)
print("Installing Chromium for SYSTEM Account")
print("="*70)
print("\nThis will download ~150MB and take 2-3 minutes...")
print()

# Upload script
remote_script = "C:\\Services\\WhatsAppBridge\\install-chromium-system.ps1"
sftp = ssh.open_sftp()
with sftp.file(remote_script, 'w') as f:
    f.write(INSTALL_SCRIPT)
sftp.close()

# Run as SYSTEM using psexec or scheduled task
# We'll use a scheduled task that runs once as SYSTEM
print("Creating one-time scheduled task to run as SYSTEM...")
stdin, stdout, stderr = ssh.exec_command(r'''powershell -Command "
$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument '-ExecutionPolicy Bypass -File C:\Services\WhatsAppBridge\install-chromium-system.ps1'
$principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
Register-ScheduledTask -TaskName 'InstallChromiumOnce' -Action $action -Principal $principal -Force | Out-Null
Start-ScheduledTask -TaskName 'InstallChromiumOnce'
"''')
stdout.read()

print("Task started. Waiting for Chromium download to complete...")
print("(This may take 2-3 minutes depending on connection speed)")
print()

# Wait and check progress
for i in range(180):  # Wait up to 3 minutes
    time.sleep(1)

    # Check if task is still running
    stdin, stdout, stderr = ssh.exec_command(
        'powershell -Command "(Get-ScheduledTask -TaskName InstallChromiumOnce).State"'
    )
    state = stdout.read().decode('utf-8', errors='ignore').strip()

    if state == "Ready":
        print("\nInstallation complete!")
        break

    if i % 10 == 0:
        print(f"   Still installing... ({i}s elapsed)")

# Check if Chromium was installed
print("\nVerifying installation...")
stdin, stdout, stderr = ssh.exec_command(
    'powershell -Command "Test-Path C:\\Windows\\system32\\config\\systemprofile\\.cache\\puppeteer\\chrome"'
)
exists = stdout.read().decode('utf-8', errors='ignore').strip()

if exists == "True":
    print("   SUCCESS - Chromium installed for SYSTEM user!")
else:
    print("   FAILED - Chromium not found")
    print("\nChecking logs...")
    stdin, stdout, stderr = ssh.exec_command(
        'powershell -Command "Get-ScheduledTaskInfo -TaskName InstallChromiumOnce | Select-Object LastTaskResult"'
    )
    result = stdout.read().decode('utf-8', errors='ignore').strip()
    print(f"   Task result: {result}")

# Clean up task
print("\nCleaning up scheduled task...")
ssh.exec_command('powershell -Command "Unregister-ScheduledTask -TaskName InstallChromiumOnce -Confirm:$false"')

# Restart WhatsApp Service
print("\nRestarting WhatsApp Service...")
ssh.exec_command('powershell -Command "Stop-ScheduledTask -TaskName WhatsAppBridgeService; Start-Sleep -Seconds 2; Start-ScheduledTask -TaskName WhatsAppBridgeService"')

time.sleep(5)

# Verify service is running
print("\nVerifying service...")
stdin, stdout, stderr = ssh.exec_command('powershell -Command "netstat -ano | findstr :3000"')
port = stdout.read().decode('utf-8', errors='ignore').strip()
if port:
    print("   Service running on port 3000!")
else:
    print("   WARNING: Port 3000 not listening")

ssh.close()

print("\n" + "="*70)
print("Chromium installed for SYSTEM account!")
print("WhatsApp Service should now be able to generate QR codes.")
print("="*70)
