#!/usr/bin/env python3
"""Start service and send test request to trigger crash"""

import paramiko
import threading
import time
import json

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"

ssh = paramiko.SSHClient()
ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD)

print("="*70)
print("Testing service with actual request")
print("="*70)

# Kill any existing
ssh.exec_command('powershell -Command "Get-Process node -ErrorAction SilentlyContinue | Stop-Process -Force"')
time.sleep(2)

# Start service in foreground
channel = ssh.get_transport().open_session()
channel.exec_command('cd C:\\Services\\WhatsAppBridge && "C:\\Program Files\\nodejs\\node.exe" index.js')

# Function to read output
output_lines = []
def read_output():
    while True:
        if channel.recv_ready():
            data = channel.recv(1024).decode('utf-8', errors='ignore')
            output_lines.append(data)
            print(data, end='', flush=True)
        if channel.recv_stderr_ready():
            data = channel.recv_stderr(1024).decode('utf-8', errors='ignore')
            output_lines.append(f"[STDERR] {data}")
            print(f"[STDERR] {data}", end='', flush=True)
        if channel.exit_status_ready():
            break
        time.sleep(0.05)

# Start output reader thread
output_thread = threading.Thread(target=read_output, daemon=True)
output_thread.start()

# Wait for startup
print("\nWaiting for service to start...")
time.sleep(5)

# Send test request via second connection
print("\nSending test request to /sessions/create...")
ssh2 = paramiko.SSHClient()
ssh2.set_missing_host_key_policy(paramiko.AutoAddPolicy())
ssh2.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD)

test_session_id = f"test-{int(time.time())}"
stdin2, stdout2, stderr2 = ssh2.exec_command(
    f'''powershell -Command "$body = @{{sessionId='{test_session_id}'}} | ConvertTo-Json; try {{ $r = Invoke-WebRequest -Uri 'http://localhost:3000/sessions/create' -Method POST -Body $body -ContentType 'application/json' -UseBasicParsing -TimeoutSec 60; Write-Host \\"Success: $($r.StatusCode)\\"; Write-Host $r.Content }} catch {{ Write-Host \\"ERROR: $($_.Exception.Message)\\" }}"'''
)

response = stdout2.read().decode('utf-8', errors='ignore')
print(f"\nResponse: {response}")

ssh2.close()

# Wait for any error output
print("\nWaiting 5 seconds for error output...")
time.sleep(5)

print("\n" + "="*70)
print("All output:")
print("="*70)
print(''.join(output_lines))

channel.close()
ssh.close()
