#!/usr/bin/env python3
"""Check what routes exist in index.js"""

import paramiko

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"

ssh = paramiko.SSHClient()
ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD)

print("Downloading index.js to check routes...\n")

# Download index.js
sftp = ssh.open_sftp()
with sftp.file('C:\\Services\\WhatsAppBridge\\index.js', 'r') as f:
    content = f.read().decode('utf-8')
sftp.close()

# Extract route definitions
print("Routes defined in index.js:")
print("="*70)

lines = content.split('\n')
for i, line in enumerate(lines, 1):
    if 'app.get' in line or 'app.post' in line or 'app.delete' in line or 'app.put' in line:
        print(f"{i}: {line.strip()}")

print("="*70)

print("\nFull file content:\n")
print(content)

ssh.close()
