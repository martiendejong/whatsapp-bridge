#!/usr/bin/env python3
"""Test WhatsApp Service endpoint directly"""

import paramiko
import time

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"

ssh = paramiko.SSHClient()
ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD)

print("Testing WhatsApp Service /sessions/create endpoint...\n")

# Create a session directly
test_id = f"test-{int(time.time())}"

cmd = f'''powershell -Command "$body = @{{sessionId='{test_id}'}} | ConvertTo-Json; try {{ $r = Invoke-WebRequest -Uri 'http://localhost:3000/sessions/create' -Method POST -Body $body -ContentType 'application/json' -UseBasicParsing -TimeoutSec 45; 'Status: ' + $r.StatusCode; ''; 'Response:'; $r.Content }} catch {{ 'Error: ' + $_.Exception.Message }}"'''

print(f"Creating session with ID: {test_id}")
print("(This may take 30-45 seconds while QR is generated)...\n")

stdin, stdout, stderr = ssh.exec_command(cmd, timeout=60)

output = stdout.read().decode('utf-8', errors='ignore')
print(output)

# Check if process crashed
stdin2, stdout2, stderr2 = ssh.exec_command('powershell -Command "Get-Process node -ErrorAction SilentlyContinue | Select-Object Id"')
process_out = stdout2.read().decode('utf-8', errors='ignore')

if process_out.strip():
    print("\nNode process still running")
else:
    print("\nWARNING: Node process crashed!")
    print("\nChecking logs...")
    stdin3, stdout3, stderr3 = ssh.exec_command('powershell -Command "Get-Content C:\\Services\\WhatsAppBridge\\logs\\service.log -Tail 30"')
    print(stdout3.read().decode('utf-8', errors='ignore'))

ssh.close()
