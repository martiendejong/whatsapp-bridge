#!/usr/bin/env python3
"""Capture startup error by running service in foreground"""

import paramiko
import threading
import time

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"

ssh = paramiko.SSHClient()
ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD)

print("="*70)
print("Running service in foreground to capture error")
print("="*70)
print()

# Kill any existing
ssh.exec_command('powershell -Command "Get-Process node -ErrorAction SilentlyContinue | Stop-Process -Force"')
time.sleep(1)

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

# Wait for output
print("Waiting for startup output...")
time.sleep(10)

print("\n\n" + "="*70)
print("If you see an error above, that's the cause")
print("="*70)

channel.close()
ssh.close()
