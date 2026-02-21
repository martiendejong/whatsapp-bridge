#!/usr/bin/env python3
import paramiko

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"

ssh = paramiko.SSHClient()
ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD)

# Check if Backend was published
print("Checking Backend deployment...")
stdin, stdout, stderr = ssh.exec_command('if exist C:\\inetpub\\whatsappbridge-api\\WhatsAppBridge.API.dll (echo EXISTS) else (echo MISSING)')
result = stdout.read().decode('utf-8', errors='ignore').strip()
print(f"Backend DLL: {result}")

if result == "MISSING":
    print("\n  Backend never published. Checking if source exists...")
    stdin, stdout, stderr = ssh.exec_command('if exist C:\\whatsappbridge\\Backend\\WhatsAppBridge.API\\WhatsAppBridge.API.csproj (echo EXISTS) else (echo MISSING)')
    result = stdout.read().decode('utf-8', errors='ignore').strip()
    print(f"  Backend .csproj: {result}")

# Check if Frontend was built
print("\nChecking Frontend deployment...")
stdin, stdout, stderr = ssh.exec_command('if exist C:\\inetpub\\whatsappbridge-web\\index.html (echo EXISTS) else (echo MISSING)')
result = stdout.read().decode('utf-8', errors='ignore').strip()
print(f"Frontend index.html: {result}")

if result == "MISSING":
    print("\n  Frontend dist not deployed. Checking if source exists...")
    stdin, stdout, stderr = ssh.exec_command('if exist C:\\whatsappbridge\\Frontend\\package.json (echo EXISTS) else (echo MISSING)')
    result = stdout.read().decode('utf-8', errors='ignore').strip()
    print(f"  Frontend package.json: {result}")

    stdin, stdout, stderr = ssh.exec_command('if exist C:\\whatsappbridge\\Frontend\\dist\\index.html (echo EXISTS) else (echo MISSING)')
    result = stdout.read().decode('utf-8', errors='ignore').strip()
    print(f"  Frontend dist/index.html (after build): {result}")

# Check WhatsApp Service files
print("\nChecking WhatsApp Service...")
stdin, stdout, stderr = ssh.exec_command('if exist C:\\Services\\WhatsAppBridge\\index.js (echo EXISTS) else (echo MISSING)')
result = stdout.read().decode('utf-8', errors='ignore').strip()
print(f"WhatsApp Service index.js: {result}")

if result == "MISSING":
    print("\n  Service files not copied. Checking source...")
    stdin, stdout, stderr = ssh.exec_command('if exist C:\\whatsappbridge\\WhatsAppService\\index.js (echo EXISTS) else (echo MISSING)')
    result = stdout.read().decode('utf-8', errors='ignore').strip()
    print(f"  WhatsAppService source index.js: {result}")

# Check task status
print("\nChecking WhatsApp Service task status...")
stdin, stdout, stderr = ssh.exec_command('schtasks /Query /TN "WhatsAppBridgeService" /FO CSV /NH')
output = stdout.read().decode('utf-8', errors='ignore')
print(output)

ssh.close()
