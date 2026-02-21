#!/usr/bin/env python3
import paramiko

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"

print("="*70)
print("UPLOADING MISSING FILES")
print("="*70)

ssh = paramiko.SSHClient()
ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD)
sftp = ssh.open_sftp()

# Upload WhatsAppService
print("\n[1/3] Uploading WhatsApp Service...")
sftp.put("E:/temp/whatsapp-service.zip", "C:/Temp/whatsapp-service.zip")
print("  Uploaded")

sftp.close()

# Extract WhatsAppService
print("\n[2/3] Extracting WhatsApp Service...")
cmd = 'powershell -Command "Expand-Archive -Path C:\\Temp\\whatsapp-service.zip -DestinationPath C:\\whatsappbridge\\WhatsAppService -Force"'
stdin, stdout, stderr = ssh.exec_command(cmd)
stdout.read()
print("  Extracted")

# Try Backend publish with verbose output
print("\n[3/3] Publishing Backend (verbose)...")
cmd = '''powershell -Command "cd C:\\whatsappbridge\\Backend\\WhatsAppBridge.API; dotnet publish -c Release -o C:\\inetpub\\whatsappbridge-api"'''
stdin, stdout, stderr = ssh.exec_command(cmd)

for line in stdout:
    line_str = line.rstrip()
    if 'error' in line_str.lower() or 'failed' in line_str.lower() or 'success' in line_str.lower():
        print(line_str)

ssh.close()

print("\n" + "="*70)
print("DONE")
print("="*70)
