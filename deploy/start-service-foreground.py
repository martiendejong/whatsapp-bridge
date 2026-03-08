#!/usr/bin/env python3
"""Start service in foreground to see errors"""

import paramiko
import select
import time

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"

ssh = paramiko.SSHClient()
ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD)

print("Starting WhatsApp Service in foreground...\n")

# Start service and capture output
channel = ssh.get_transport().open_session()
channel.exec_command('cd C:\\Services\\WhatsAppBridge && "C:\\Program Files\\nodejs\\node.exe" index.js')

# Read output for 10 seconds
start_time = time.time()
output_lines = []

while time.time() - start_time < 10:
    if channel.recv_ready():
        data = channel.recv(1024).decode('utf-8', errors='ignore')
        output_lines.append(data)
        print(data, end='')

    if channel.recv_stderr_ready():
        data = channel.recv_stderr(1024).decode('utf-8', errors='ignore')
        output_lines.append(f"[STDERR] {data}")
        print(f"[STDERR] {data}", end='')

    if channel.exit_status_ready():
        break

    time.sleep(0.1)

print("\n\n" + "="*70)

# Check if port is listening
stdin2, stdout2, stderr2 = ssh.exec_command('netstat -ano | findstr ":3000" | findstr "LISTENING"')
port_status = stdout2.read().decode('utf-8', errors='ignore').strip()

if port_status:
    print("SUCCESS! Service is running on port 3000")
    print(port_status)
else:
    print("Service NOT listening on port 3000")
    print("\nService output above should show the error")

ssh.close()
