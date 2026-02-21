#!/usr/bin/env python3
import paramiko

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"

print("="*70)
print("FINAL INSTALLATION (with error output)")
print("="*70)

ssh = paramiko.SSHClient()
ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD)
sftp = ssh.open_sftp()

print("\nUploading installer...")
sftp.put("E:/projects/whatsappbridge/deploy/install-with-errors-shown.ps1", "C:/Temp/install-final.ps1")
sftp.close()

print("Running installation...")
print("This will take 3-5 minutes (npm install + dotnet publish)")
print("="*70 + "\n")

stdin, stdout, stderr = ssh.exec_command('powershell -ExecutionPolicy Bypass -File C:\\Temp\\install-final.ps1', get_pty=True)

for line in stdout:
    print(line.rstrip())

ssh.close()
