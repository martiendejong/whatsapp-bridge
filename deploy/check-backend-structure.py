#!/usr/bin/env python3
import paramiko

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"

ssh = paramiko.SSHClient()
ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD)

print("Checking C:\\whatsappbridge\\Backend structure...")
stdin, stdout, stderr = ssh.exec_command('dir /B C:\\whatsappbridge\\Backend')
output = stdout.read().decode('utf-8', errors='ignore')
print(output)

print("\nChecking if WhatsAppBridge.API exists...")
stdin, stdout, stderr = ssh.exec_command('if exist C:\\whatsappbridge\\Backend\\WhatsAppBridge.API (echo YES) else (echo NO)')
result = stdout.read().decode('utf-8', errors='ignore').strip()
print(f"WhatsAppBridge.API: {result}")

print("\nChecking Frontend structure...")
stdin, stdout, stderr = ssh.exec_command('dir /B C:\\whatsappbridge\\Frontend')
output = stdout.read().decode('utf-8', errors='ignore')
print(output)

ssh.close()
