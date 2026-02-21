#!/usr/bin/env python3
"""Quick manual fix - install NSSM then run simplified service installation"""

import paramiko
import time

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"

print("="*70)
print("QUICK FIX - Manual NSSM + Service Installation")
print("="*70)

try:
    ssh = paramiko.SSHClient()
    ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD, timeout=30)

    # Step 1: Install NSSM via PowerShell direct download
    print("\n[1/3] Installing NSSM...")
    cmd = '''powershell -Command "$ErrorActionPreference='Stop'; [Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12; Invoke-WebRequest -Uri 'https://nssm.cc/release/nssm-2.24.zip' -OutFile '$env:TEMP\\nssm.zip' -UseBasicParsing; Expand-Archive -Path '$env:TEMP\\nssm.zip' -DestinationPath '$env:TEMP\\nssm' -Force; Copy-Item '$env:TEMP\\nssm\\nssm-2.24\\win64\\nssm.exe' 'C:\\Windows\\System32\\nssm.exe' -Force; Write-Host 'NSSM installed'"'''

    stdin, stdout, stderr = ssh.exec_command(cmd)
    output = stdout.read().decode('utf-8', errors='ignore')
    error = stderr.read().decode('utf-8', errors='ignore')
    print(output)
    if error and 'NSSM installed' not in output:
        print(f"  Warning: {error}")
    else:
        print("  [OK] NSSM installed")

    time.sleep(2)

    # Step 2: Verify NSSM works
    print("\n[2/3] Verifying NSSM...")
    stdin, stdout, stderr = ssh.exec_command('nssm version')
    output = stdout.read().decode('utf-8', errors='ignore')
    if 'NSSM' in output:
        print(f"  [OK] {output.strip()}")
    else:
        print("  [FAIL] NSSM not working")
        print(stderr.read().decode('utf-8', errors='ignore'))

    # Step 3: Show what we have
    print("\n[3/3] Checking application structure...")
    for dir in ['Backend\\WhatsAppBridge.API', 'Frontend', 'WhatsAppService']:
        cmd = f'if exist C:\\whatsappbridge\\{dir} (echo EXISTS) else (echo MISSING)'
        stdin, stdout, stderr = ssh.exec_command(cmd)
        result = stdout.read().decode('utf-8', errors='ignore').strip()
        print(f"  {dir}: {result}")

    ssh.close()

    print("\n" + "="*70)
    print("NSSM READY - Now we need to upload missing application files")
    print("="*70)

except Exception as e:
    print(f"\n[ERROR] {e}")
    import traceback
    traceback.print_exc()
