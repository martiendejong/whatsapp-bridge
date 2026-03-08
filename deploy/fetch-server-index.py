#!/usr/bin/env python3
"""Fetch index.js from server to see what's deployed"""

import paramiko

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"

ssh = paramiko.SSHClient()
ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD)

# Read the deployed index.js
stdin, stdout, stderr = ssh.exec_command('type C:\\Services\\WhatsAppBridge\\index.js')
content = stdout.read().decode('utf-8', errors='ignore')

ssh.close()

# Save to temp file
with open('E:\\projects\\whatsappbridge\\deploy\\server-index.js', 'w', encoding='utf-8') as f:
    f.write(content)

print("Fetched server index.js and saved to:")
print("E:\\projects\\whatsappbridge\\deploy\\server-index.js")
print()
print("First 50 lines:")
print("="*70)
print('\n'.join(content.split('\n')[:50]))
