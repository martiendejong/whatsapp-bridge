#!/usr/bin/env python3
"""
Complete Offline Deployment
Uploads pre-compiled backend and complete WhatsApp Service to server
"""

import paramiko
import os
import sys
from datetime import datetime

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"

def progress_bar(transferred, total, width=50):
    """Display upload progress"""
    percent = transferred / total
    filled = int(width * percent)
    bar = '█' * filled + '-' * (width - filled)
    mb_transferred = transferred / (1024 * 1024)
    mb_total = total / (1024 * 1024)
    print(f'\r  [{bar}] {percent*100:.1f}% ({mb_transferred:.1f}/{mb_total:.1f} MB)', end='', flush=True)

print("="*70)
print("OFFLINE DEPLOYMENT - WhatsApp Bridge")
print("="*70)
print(f"Started: {datetime.now().strftime('%H:%M:%S')}")

try:
    # Connect
    print("\n[1/6] Connecting to server...")
    ssh = paramiko.SSHClient()
    ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD, timeout=30)
    print("  [OK] Connected")

    # Create archives
    print("\n[2/6] Creating deployment packages...")

    # Backend archive
    print("  Creating backend archive...")
    os.system('powershell -Command "Compress-Archive -Path E:\\temp\\backend-published\\* -DestinationPath E:\\temp\\backend-deploy.zip -Force"')
    backend_size = os.path.getsize("E:/temp/backend-deploy.zip") / (1024 * 1024)
    print(f"  [OK] Backend: {backend_size:.1f} MB")

    # WhatsApp Service archive
    print("  Creating WhatsApp Service archive...")
    os.system('powershell -Command "Compress-Archive -Path E:\\projects\\whatsappbridge\\WhatsAppService\\* -DestinationPath E:\\temp\\service-deploy.zip -Force"')
    service_size = os.path.getsize("E:/temp/service-deploy.zip") / (1024 * 1024)
    print(f"  [OK] WhatsApp Service: {service_size:.1f} MB")

    # Upload
    print("\n[3/6] Uploading to server...")
    sftp = ssh.open_sftp()

    print("  Backend...")
    sftp.put("E:/temp/backend-deploy.zip", "C:/Temp/backend-deploy.zip", callback=progress_bar)
    print("\n  [OK] Backend uploaded")

    print("  WhatsApp Service...")
    sftp.put("E:/temp/service-deploy.zip", "C:/Temp/service-deploy.zip", callback=progress_bar)
    print("\n  [OK] Service uploaded")

    sftp.close()

    # Extract on server
    print("\n[4/6] Extracting files on server...")

    # Create directories
    print("  Creating directories...")
    commands = [
        'if not exist "C:\\inetpub\\whatsappbridge-api" mkdir "C:\\inetpub\\whatsappbridge-api"',
        'if not exist "C:\\Services\\WhatsAppBridge" mkdir "C:\\Services\\WhatsAppBridge"'
    ]
    for cmd in commands:
        ssh.exec_command(cmd)
    print("  [OK] Directories created")

    # Extract backend
    print("  Extracting backend...")
    cmd = 'powershell -Command "Expand-Archive -Path C:\\Temp\\backend-deploy.zip -DestinationPath C:\\inetpub\\whatsappbridge-api -Force"'
    stdin, stdout, stderr = ssh.exec_command(cmd)
    stdout.read()
    print("  [OK] Backend extracted")

    # Extract service
    print("  Extracting WhatsApp Service...")
    cmd = 'powershell -Command "Expand-Archive -Path C:\\Temp\\service-deploy.zip -DestinationPath C:\\Services\\WhatsAppBridge -Force"'
    stdin, stdout, stderr = ssh.exec_command(cmd)
    stdout.read()
    print("  [OK] Service extracted")

    # Configure
    print("\n[5/6] Configuring services...")

    # Create appsettings.Production.json
    print("  Creating Backend config...")
    config_script = '''
$config = @{
    ConnectionStrings = @{
        DefaultConnection = "Data Source=C:\\inetpub\\whatsappbridge-api\\whatsappbridge.db"
    }
    Jwt = @{
        Key = "wreckingball-prod-''' + str(datetime.now().timestamp()).replace('.', '') + '''"
        Issuer = "WhatsAppBridge"
        Audience = "WhatsAppBridgeUsers"
    }
    WhatsAppService = @{ Url = "http://localhost:3000" }
    AllowedOrigins = @(
        "https://whatsapp.wreckingball.ai",
        "http://whatsapp.wreckingball.ai"
    )
    Encryption = @{ Enabled = $false; Key = ""; IV = "" }
}
$config | ConvertTo-Json -Depth 10 | Out-File -FilePath "C:\\inetpub\\whatsappbridge-api\\appsettings.Production.json" -Encoding UTF8
'''
    stdin, stdout, stderr = ssh.exec_command(f'powershell -Command "{config_script}"')
    stdout.read()
    print("  [OK] Backend config created")

    # Configure IIS for Backend
    print("  Configuring IIS...")
    iis_script = '''
Import-Module WebAdministration

$appPoolName = "WhatsAppBridgeAPIPool"
if (Test-Path "IIS:\\AppPools\\$appPoolName") {
    Remove-WebAppPool -Name $appPoolName
}
New-WebAppPool -Name $appPoolName | Out-Null
Set-ItemProperty "IIS:\\AppPools\\$appPoolName" -Name "managedRuntimeVersion" -Value ""

$siteName = "WhatsAppBridgeAPI"
if (Test-Path "IIS:\\Sites\\$siteName") {
    Remove-WebSite -Name $siteName
}
New-WebSite -Name $siteName -PhysicalPath "C:\\inetpub\\whatsappbridge-api" -Port 5000 -ApplicationPool $appPoolName | Out-Null

$acl = Get-Acl "C:\\inetpub\\whatsappbridge-api"
$rule = New-Object System.Security.AccessControl.FileSystemAccessRule("IIS_IUSRS", "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow")
$acl.SetAccessRule($rule)
Set-Acl "C:\\inetpub\\whatsappbridge-api" $acl

Write-Host "IIS Configured"
'''
    stdin, stdout, stderr = ssh.exec_command(f'powershell -Command "{iis_script}"')
    output = stdout.read().decode('utf-8', errors='ignore')
    if "IIS Configured" in output:
        print("  [OK] IIS configured")
    else:
        print("  ⚠ IIS configuration (check manually)")

    # Configure WhatsApp Service
    print("  Configuring WhatsApp Service...")
    service_script = '''
# Create startup script
@"
@echo off
cd /d C:\\Services\\WhatsAppBridge
node index.js
"@ | Out-File -FilePath "C:\\Services\\WhatsAppBridge\\start.bat" -Encoding ASCII

# Remove existing task
$existing = Get-ScheduledTask -TaskName "WhatsAppBridgeService" -ErrorAction SilentlyContinue
if ($existing) {
    Unregister-ScheduledTask -TaskName "WhatsAppBridgeService" -Confirm:$false
}

# Create scheduled task
$action = New-ScheduledTaskAction -Execute "cmd.exe" -Argument "/c C:\\Services\\WhatsAppBridge\\start.bat" -WorkingDirectory "C:\\Services\\WhatsAppBridge"
$trigger = New-ScheduledTaskTrigger -AtStartup
$principal = New-ScheduledTaskPrincipal -UserID "NT AUTHORITY\\SYSTEM" -LogonType ServiceAccount -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable

Register-ScheduledTask -TaskName "WhatsAppBridgeService" -Action $action -Trigger $trigger -Principal $principal -Settings $settings | Out-Null

# Start task
Start-ScheduledTask -TaskName "WhatsAppBridgeService"

Write-Host "Service Configured"
'''
    stdin, stdout, stderr = ssh.exec_command(f'powershell -Command "{service_script}"')
    output = stdout.read().decode('utf-8', errors='ignore')
    if "Service Configured" in output:
        print("  [OK] WhatsApp Service configured")
    else:
        print("  ⚠ Service configuration (check manually)")

    # Restart IIS
    print("  Restarting IIS...")
    stdin, stdout, stderr = ssh.exec_command('iisreset /noforce')
    stdout.read()
    print("  [OK] IIS restarted")

    # Wait for services to start
    print("\n  Waiting for services to start (10 seconds)...")
    import time
    time.sleep(10)

    # Verify
    print("\n[6/6] Verifying deployment...")
    stdin, stdout, stderr = ssh.exec_command('netstat -an | findstr "LISTENING" | findstr ":80 :3000 :5000"')
    output = stdout.read().decode('utf-8', errors='ignore')

    ports_ok = []
    ports_fail = []

    if ':80 ' in output:
        ports_ok.append('80 (Frontend)')
    else:
        ports_fail.append('80 (Frontend)')

    if ':3000 ' in output:
        ports_ok.append('3000 (WhatsApp Service)')
    else:
        ports_fail.append('3000 (WhatsApp Service)')

    if ':5000 ' in output:
        ports_ok.append('5000 (Backend API)')
    else:
        ports_fail.append('5000 (Backend API)')

    if ports_ok:
        print("  [OK] Listening on:", ", ".join(ports_ok))
    if ports_fail:
        print("  [FAIL] NOT listening:", ", ".join(ports_fail))

    ssh.close()

    print("\n" + "="*70)
    if len(ports_ok) == 3:
        print("[SUCCESS] DEPLOYMENT SUCCESSFUL!")
    elif len(ports_ok) >= 2:
        print("[WARN]  DEPLOYMENT PARTIALLY SUCCESSFUL")
    else:
        print("[ERROR] DEPLOYMENT FAILED")
    print("="*70)

    print("\nURLs:")
    print("  Frontend: http://whatsapp.wreckingball.ai")
    print("  Backend:  http://whatsapp.wreckingball.ai:5000/swagger")
    print("  Service:  http://whatsapp.wreckingball.ai:3000")

    print(f"\nCompleted: {datetime.now().strftime('%H:%M:%S')}")

    if len(ports_fail) > 0:
        print("\n[WARN]  Some services not running. Check logs:")
        print("  Backend: C:\\inetpub\\whatsappbridge-api\\logs")
        print("  Service: Task Scheduler > WhatsAppBridgeService")

except Exception as e:
    print(f"\n[ERROR] ERROR: {e}")
    import traceback
    traceback.print_exc()
    sys.exit(1)
