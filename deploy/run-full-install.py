#!/usr/bin/env python3
"""Run complete installation on server"""

import paramiko
import sys

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"
DOMAIN = "whatsapp.wreckingball.ai"

print("="*70)
print("Running COMPLETE Installation")
print("="*70)
print("\nThis will install:")
print("  - WhatsApp Service (Node.js on port 3000)")
print("  - Backend API (ASP.NET Core on port 5000)")
print("  - Frontend (React on port 80)")
print("  - IIS configuration")
print("\nEstimated time: 15-20 minutes")
print("="*70)

try:
    print("\n[1/2] Connecting to server...")
    ssh = paramiko.SSHClient()
    ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD, timeout=30)
    print(f"  Connected to {SSH_HOST}")

    print("\n[2/2] Running installation script...")
    print("  (This will take 15-20 minutes - please wait)")
    print("")

    # Run installation with output streaming
    stdin, stdout, stderr = ssh.exec_command(
        f'powershell -ExecutionPolicy Bypass -File C:\\whatsappbridge\\deploy\\INSTALL_ON_SERVER.ps1 -Domain {DOMAIN}',
        get_pty=True
    )

    # Print output in real-time
    line_count = 0
    for line in stdout:
        line_str = line.rstrip()
        # Skip ANSI escape codes and empty lines
        if line_str and not any(x in line_str for x in ['\x1b[', '[?25', '\x1b]0']):
            # Clean line
            import re
            clean = re.sub(r'\x1b\[[0-9;]*[a-zA-Z]', '', line_str)
            clean = re.sub(r'\[.*?\]0;', '', clean)
            if clean.strip():
                print(clean)
                line_count += 1
                sys.stdout.flush()

    exit_code = stdout.channel.recv_exit_status()

    print("\n" + "="*70)
    if exit_code == 0:
        print("INSTALLATION COMPLETE!")
        print("="*70)
        print(f"\nYour application is now live at:")
        print(f"  http://{DOMAIN}")
        print(f"  http://{DOMAIN}:5000/swagger")
        print(f"\nNext step: Install SSL certificate")
        print(f"  cd E:\\projects\\whatsappbridge\\deploy")
        print(f"  setup-ssl.cmd")
    else:
        print(f"INSTALLATION COMPLETED WITH WARNINGS (exit code: {exit_code})")
        print("="*70)
        print("\nPlease check output above for any errors")

    ssh.close()

except Exception as e:
    print(f"\nERROR: {e}")
    import traceback
    traceback.print_exc()
    sys.exit(1)
