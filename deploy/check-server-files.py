#!/usr/bin/env python3
import paramiko

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"

ssh = paramiko.SSHClient()
ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD)

print("Checking C:\\whatsappbridge structure...")
stdin, stdout, stderr = ssh.exec_command('dir C:\\whatsappbridge')
output = stdout.read().decode('utf-8', errors='ignore')
print(output)

print("\n" + "="*70 + "\n")
print("Checking if subdirectories exist...")
for subdir in ['Backend', 'Frontend', 'WhatsAppService']:
    stdin, stdout, stderr = ssh.exec_command(f'if exist C:\\whatsappbridge\\{subdir} (echo YES) else (echo NO)')
    result = stdout.read().decode('utf-8', errors='ignore').strip()
    print(f"{subdir}: {result}")

ssh.close()
