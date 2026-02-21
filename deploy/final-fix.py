#!/usr/bin/env python3
"""Final fix: Upload script, execute, done"""

import paramiko
import time

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"

print("="*70)
print("FINAL FIX")
print("="*70)

try:
    ssh = paramiko.SSHClient()
    ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD, timeout=30)
    sftp = ssh.open_sftp()

    # Upload fixed NSSM installer
    print("\n[1/2] Uploading NSSM installer...")
    sftp.put("E:/projects/whatsappbridge/deploy/install-nssm-fixed.ps1", "C:/Temp/install-nssm.ps1")
    print("  Uploaded")

    sftp.close()

    # Execute
    print("\n[2/2] Installing NSSM...")
    stdin, stdout, stderr = ssh.exec_command('powershell -ExecutionPolicy Bypass -File C:\\Temp\\install-nssm.ps1', get_pty=True)

    for line in stdout:
        line_clean = line.rstrip()
        if line_clean and '[' in line_clean:
            print(line_clean)

    # Check if it worked
    print("\nVerifying...")
    stdin, stdout, stderr = ssh.exec_command('nssm version')
    output = stdout.read().decode('utf-8', errors='ignore')
    if 'NSSM' in output or '2.24' in output:
        print("[OK] NSSM installed and working")
    else:
        print("[FAIL] NSSM still not working")
        print(stderr.read().decode('utf-8', errors='ignore'))

    ssh.close()

    print("\n" + "="*70)

except Exception as e:
    print(f"\n[ERROR] {e}")
    import traceback
    traceback.print_exc()
