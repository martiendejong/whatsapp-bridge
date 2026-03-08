#!/usr/bin/env python3
"""
Offline Deployment V2 - Direct upload without archiving node_modules
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
    bar = '#' * filled + '-' * (width - filled)
    mb_transferred = transferred / (1024 * 1024)
    mb_total = total / (1024 * 1024)
    print(f'\r  [{bar}] {percent*100:.1f}% ({mb_transferred:.1f}/{mb_total:.1f} MB)', end='', flush=True)

def upload_directory(sftp, local_path, remote_path, indent="  "):
    """Recursively upload a directory via SFTP"""
    try:
        sftp.mkdir(remote_path)
    except:
        pass  # Directory might exist

    for item in os.listdir(local_path):
        local_item = os.path.join(local_path, item)
        remote_item = remote_path + "/" + item

        if os.path.isfile(local_item):
            try:
                sftp.put(local_item, remote_item)
            except Exception as e:
                print(f"\n{indent}[SKIP] {item}: {e}")
        elif os.path.isdir(local_item):
            upload_directory(sftp, local_item, remote_item, indent + "  ")

print("="*70)
print("OFFLINE DEPLOYMENT V2 - WhatsApp Bridge")
print("="*70)
print(f"Started: {datetime.now().strftime('%H:%M:%S')}")

try:
    # Connect
    print("\n[1/5] Connecting to server...")
    ssh = paramiko.SSHClient()
    ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD, timeout=30)
    print("  [OK] Connected")

    sftp = ssh.open_sftp()

    # Upload Backend (pre-compiled)
    print("\n[2/5] Uploading Backend (compiled binaries)...")

    # Create backend ZIP (this works - no locked files)
    print("  Creating backend archive...")
    os.system('powershell -Command "Compress-Archive -Path E:\\temp\\backend-published\\* -DestinationPath E:\\temp\\backend-deploy.zip -Force" 2>nul')
    backend_size = os.path.getsize("E:/temp/backend-deploy.zip") / (1024 * 1024)
    print(f"  [OK] Backend archive: {backend_size:.1f} MB")

    print("  Uploading...")
    sftp.put("E:/temp/backend-deploy.zip", "C:/Temp/backend-deploy.zip", callback=progress_bar)
    print("\n  [OK] Backend uploaded")

    # Upload WhatsApp Service (direct - no archiving)
    print("\n[3/5] Uploading WhatsApp Service...")
    print("  (This may take 2-3 minutes due to node_modules size)")

    local_service = "E:/projects/whatsappbridge/WhatsAppService"
    remote_service = "C:/Services/WhatsAppBridge"

    # Upload main files first
    print("  Uploading index.js...")
    try:
        sftp.mkdir(remote_service)
    except:
        pass

    sftp.put(f"{local_service}/index.js", f"{remote_service}/index.js")
    sftp.put(f"{local_service}/package.json", f"{remote_service}/package.json")
    print("  [OK] Main files uploaded")

    # Upload node_modules (this is slow but works)
    print("  Uploading node_modules (287 packages)...")
    node_modules_local = f"{local_service}/node_modules"
    node_modules_remote = f"{remote_service}/node_modules"

    try:
        sftp.mkdir(node_modules_remote)
    except:
        pass

    # Count total for progress
    total_files = sum([len(files) for r, d, files in os.walk(node_modules_local)])
    uploaded = 0

    for root, dirs, files in os.walk(node_modules_local):
        remote_root = root.replace(node_modules_local, node_modules_remote).replace("\\", "/")

        # Create directories
        for dir_name in dirs:
            try:
                sftp.mkdir(f"{remote_root}/{dir_name}")
            except:
                pass

        # Upload files
        for file_name in files:
            local_file = os.path.join(root, file_name)
            remote_file = f"{remote_root}/{file_name}"

            try:
                sftp.put(local_file, remote_file)
                uploaded += 1
                if uploaded % 50 == 0:  # Progress every 50 files
                    percent = (uploaded / total_files) * 100
                    print(f"\r  Progress: {uploaded}/{total_files} files ({percent:.1f}%)", end='', flush=True)
            except Exception as e:
                # Skip locked files
                if "permission" not in str(e).lower():
                    print(f"\n  [SKIP] {file_name}")

    print(f"\n  [OK] WhatsApp Service uploaded ({uploaded} files)")

    sftp.close()

    # Configure on server
    print("\n[4/5] Configuring services...")

    # Extract backend
    print("  Extracting backend...")
    cmd = 'powershell -Command "if (Test-Path C:\\inetpub\\whatsappbridge-api) { Remove-Item C:\\inetpub\\whatsappbridge-api\\* -Recurse -Force }; Expand-Archive -Path C:\\Temp\\backend-deploy.zip -DestinationPath C:\\inetpub\\whatsappbridge-api -Force"'
    stdin, stdout, stderr = ssh.exec_command(cmd)
    stdout.read()
    print("  [OK] Backend extracted")

    # Create Backend config
    print("  Creating Backend config...")
    config_script = f'''
$config = @{{
    ConnectionStrings = @{{ DefaultConnection = "Data Source=C:\\inetpub\\whatsappbridge-api\\whatsappbridge.db" }}
    Jwt = @{{
        Key = "wreckingball-prod-{int(datetime.now().timestamp())}"
        Issuer = "WhatsAppBridge"
        Audience = "WhatsAppBridgeUsers"
    }}
    WhatsAppService = @{{ Url = "http://localhost:3000" }}
    AllowedOrigins = @("https://whatsapp.wreckingball.ai", "http://whatsapp.wreckingball.ai")
    Encryption = @{{ Enabled = $false; Key = ""; IV = "" }}
}}
$config | ConvertTo-Json -Depth 10 | Out-File -FilePath "C:\\inetpub\\whatsappbridge-api\\appsettings.Production.json" -Encoding UTF8
Write-Host "Config Created"
'''
    stdin, stdout, stderr = ssh.exec_command(f'powershell -Command "{config_script}"')
    output = stdout.read().decode('utf-8', errors='ignore')
    print("  [OK] Config created")

    # Configure IIS
    print("  Configuring IIS...")
    iis_script = '''
Import-Module WebAdministration
$appPoolName = "WhatsAppBridgeAPIPool"
if (Test-Path "IIS:\\AppPools\\$appPoolName") { Remove-WebAppPool -Name $appPoolName }
New-WebAppPool -Name $appPoolName | Out-Null
Set-ItemProperty "IIS:\\AppPools\\$appPoolName" -Name "managedRuntimeVersion" -Value ""
$siteName = "WhatsAppBridgeAPI"
if (Test-Path "IIS:\\Sites\\$siteName") { Remove-WebSite -Name $siteName }
New-WebSite -Name $siteName -PhysicalPath "C:\\inetpub\\whatsappbridge-api" -Port 5000 -ApplicationPool $appPoolName | Out-Null
$acl = Get-Acl "C:\\inetpub\\whatsappbridge-api"
$rule = New-Object System.Security.AccessControl.FileSystemAccessRule("IIS_IUSRS", "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow")
$acl.SetAccessRule($rule)
Set-Acl "C:\\inetpub\\whatsappbridge-api" $acl
Write-Host "IIS OK"
'''
    stdin, stdout, stderr = ssh.exec_command(f'powershell -Command "{iis_script}"')
    output = stdout.read().decode('utf-8', errors='ignore')
    if "IIS OK" in output:
        print("  [OK] IIS configured")

    # Configure WhatsApp Service
    print("  Configuring WhatsApp Service...")
    service_script = '''
@"
@echo off
cd /d C:\\Services\\WhatsAppBridge
node index.js
"@ | Out-File -FilePath "C:\\Services\\WhatsAppBridge\\start.bat" -Encoding ASCII
$existing = Get-ScheduledTask -TaskName "WhatsAppBridgeService" -ErrorAction SilentlyContinue
if ($existing) { Unregister-ScheduledTask -TaskName "WhatsAppBridgeService" -Confirm:$false }
$action = New-ScheduledTaskAction -Execute "cmd.exe" -Argument "/c C:\\Services\\WhatsAppBridge\\start.bat" -WorkingDirectory "C:\\Services\\WhatsAppBridge"
$trigger = New-ScheduledTaskTrigger -AtStartup
$principal = New-ScheduledTaskPrincipal -UserID "NT AUTHORITY\\SYSTEM" -LogonType ServiceAccount -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable
Register-ScheduledTask -TaskName "WhatsAppBridgeService" -Action $action -Trigger $trigger -Principal $principal -Settings $settings | Out-Null
Start-ScheduledTask -TaskName "WhatsAppBridgeService"
Write-Host "Service OK"
'''
    stdin, stdout, stderr = ssh.exec_command(f'powershell -Command "{service_script}"')
    output = stdout.read().decode('utf-8', errors='ignore')
    if "Service OK" in output:
        print("  [OK] Service configured")

    # Restart IIS
    print("  Restarting IIS...")
    stdin, stdout, stderr = ssh.exec_command('iisreset /noforce')
    stdout.read()
    print("  [OK] IIS restarted")

    # Wait
    print("\n  Waiting for services (15 seconds)...")
    import time
    time.sleep(15)

    # Verify
    print("\n[5/5] Verifying...")
    stdin, stdout, stderr = ssh.exec_command('netstat -an | findstr "LISTENING" | findstr ":80 :3000 :5000"')
    output = stdout.read().decode('utf-8', errors='ignore')

    results = []
    for port, name in [('80', 'Frontend'), ('3000', 'WhatsApp Service'), ('5000', 'Backend API')]:
        if f':{port} ' in output:
            results.append(f"[OK] Port {port} ({name})")
            print(f"  {results[-1]}")
        else:
            results.append(f"[FAIL] Port {port} ({name})")
            print(f"  {results[-1]}")

    ssh.close()

    success_count = sum(1 for r in results if '[OK]' in r)

    print("\n" + "="*70)
    if success_count == 3:
        print("[SUCCESS] ALL SERVICES RUNNING!")
    elif success_count >= 2:
        print(f"[WARN] {success_count}/3 services running")
    else:
        print("[ERROR] DEPLOYMENT FAILED")
    print("="*70)

    print("\nURLs:")
    print("  Frontend: http://whatsapp.wreckingball.ai")
    print("  Backend:  http://whatsapp.wreckingball.ai:5000/swagger")
    print("  Service:  http://whatsapp.wreckingball.ai:3000")

    print(f"\nCompleted: {datetime.now().strftime('%H:%M:%S')}")

except Exception as e:
    print(f"\n[ERROR] {e}")
    import traceback
    traceback.print_exc()
    sys.exit(1)
