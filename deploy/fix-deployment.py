#!/usr/bin/env python3
"""Direct fix via Python SSH - no loops, just execution"""

import paramiko
import sys
import time

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"

SCRIPTS = [
    "fix-step1-nssm.ps1",
    "fix-step2-service.ps1",
    "fix-step3-backend.ps1",
    "fix-step4-frontend.ps1",
    "fix-step5-verify.ps1"
]

print("=" * 70)
print("DIRECT FIX VIA PYTHON SSH")
print("=" * 70)

try:
    # Connect
    print("\n[1/3] Connecting...")
    ssh = paramiko.SSHClient()
    ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD, timeout=30)
    print("  Connected to server")

    # Upload scripts
    print("\n[2/3] Uploading fix scripts...")
    sftp = ssh.open_sftp()
    for script in SCRIPTS:
        local_path = f"E:/projects/whatsappbridge/deploy/{script}"
        remote_path = f"C:/Temp/{script}"
        print(f"  Uploading {script}...")
        sftp.put(local_path, remote_path)
    sftp.close()
    print("  All scripts uploaded")

    # Execute step by step
    print("\n[3/3] Executing installation...")
    for i, script in enumerate(SCRIPTS, 1):
        print(f"\n--- Step {i}/5: {script} ---")

        cmd = f'powershell -ExecutionPolicy Bypass -File C:\\Temp\\{script}'
        stdin, stdout, stderr = ssh.exec_command(cmd, get_pty=True)

        # Stream output
        for line in stdout:
            line_clean = line.rstrip()
            if line_clean:
                print(line_clean)

        exit_code = stdout.channel.recv_exit_status()

        if exit_code != 0:
            print(f"\n[WARN] Step {i} exited with code {exit_code}")
            print("Continuing to next step...")
        else:
            print(f"[OK] Step {i} completed")

        time.sleep(2)

    ssh.close()

    print("\n" + "=" * 70)
    print("DEPLOYMENT FIXED!")
    print("=" * 70)
    print("\nCheck: http://whatsapp.wreckingball.ai")
    print("API:   http://whatsapp.wreckingball.ai:5000/swagger")
    print("\nNext: Install SSL with setup-ssl.cmd")

except Exception as e:
    print(f"\n[ERROR] {e}")
    import traceback
    traceback.print_exc()
    sys.exit(1)
