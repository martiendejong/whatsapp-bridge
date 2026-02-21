#!/usr/bin/env python3
import paramiko
import time

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"

print("="*70)
print("INSTALLATION WITHOUT NSSM (using Task Scheduler)")
print("="*70)

try:
    ssh = paramiko.SSHClient()
    ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD)
    sftp = ssh.open_sftp()

    # Upload script
    print("\n[1/2] Uploading installer...")
    sftp.put("E:/projects/whatsappbridge/deploy/install-without-nssm.ps1", "C:/Temp/install.ps1")
    print("  Uploaded")
    sftp.close()

    # Execute
    print("\n[2/2] Running installation...")
    stdin, stdout, stderr = ssh.exec_command('powershell -ExecutionPolicy Bypass -File C:\\Temp\\install.ps1', get_pty=True)

    for line in stdout:
        line_str = line.rstrip()
        # Filter ANSI codes and show only important lines
        if any(x in line_str for x in ['[', 'WhatsApp', 'Site:', 'API:', 'INSTALLATION', '====']):
            import re
            clean = re.sub(r'\x1b\[[0-9;]*[a-zA-Z]', '', line_str)
            clean = re.sub(r'\[.*?\]0;', '', clean)
            if clean.strip():
                print(clean)

    time.sleep(2)

    # Verify
    print("\n" + "="*70)
    print("Verifying services...")
    stdin, stdout, stderr = ssh.exec_command('netstat -an | findstr "LISTENING" | findstr ":80 :3000 :5000"')
    output = stdout.read().decode('utf-8', errors='ignore')

    ports_found = []
    if ':80 ' in output: ports_found.append('80 (Frontend)')
    if ':3000 ' in output: ports_found.append('3000 (WhatsApp Service)')
    if ':5000 ' in output: ports_found.append('5000 (Backend API)')

    if ports_found:
        print("[OK] Listening on ports: " + ', '.join(ports_found))
    else:
        print("[WARN] No services listening yet")

    ssh.close()

    print("\n" + "="*70)
    print("DONE!")
    print("=" *70)
    print("\nCheck: http://whatsapp.wreckingball.ai")

except Exception as e:
    print(f"\n[ERROR] {e}")
    import traceback
    traceback.print_exc()
