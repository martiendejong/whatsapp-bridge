#!/usr/bin/env python3
"""Capture crash error in real-time"""

import paramiko
import threading
import time

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"

ssh = paramiko.SSHClient()
ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD)

print("Starting service and monitoring output...\n")

# Start service in foreground
channel = ssh.get_transport().open_session()
channel.exec_command('cd C:\\Services\\WhatsAppBridge && "C:\\Program Files\\nodejs\\node.exe" index.js')

# Function to read output
def read_output():
    while True:
        if channel.recv_ready():
            data = channel.recv(1024).decode('utf-8', errors='ignore')
            print(data, end='', flush=True)
        if channel.recv_stderr_ready():
            data = channel.recv_stderr(1024).decode('utf-8', errors='ignore')
            print(f"[ERROR] {data}", end='', flush=True)
        if channel.exit_status_ready():
            break
        time.sleep(0.05)

# Start output reader thread
output_thread = threading.Thread(target=read_output, daemon=True)
output_thread.start()

# Wait for service to start
print("Waiting for service to start...")
time.sleep(5)

# Send test request via another SSH connection
print("\nSending test request...\n")
ssh2 = paramiko.SSHClient()
ssh2.set_missing_host_key_policy(paramiko.AutoAddPolicy())
ssh2.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD)

test_id = f"test-{int(time.time())}"
cmd = f'''powershell -Command "$body = @{{sessionId='{test_id}'}} | ConvertTo-Json; Invoke-WebRequest -Uri 'http://localhost:3000/sessions/create' -Method POST -Body $body -ContentType 'application/json' -UseBasicParsing -TimeoutSec 10 2>&1 | Out-Null"'''

stdin2, stdout2, stderr2 = ssh2.exec_command(cmd)
stdout2.read()  # Wait for command to finish
ssh2.close()

# Wait for crash output
print("\nWaiting for error output...")
time.sleep(3)

print("\n\n" + "="*70)
print("If you see an error above, that's the crash reason")
print("="*70)

channel.close()
ssh.close()
