#!/usr/bin/env python3
import paramiko

ssh = paramiko.SSHClient()
ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
ssh.connect('85.215.217.154', username='administrator', password='3WsXcFr$7YhNmKi*')

print("="*70)
print("QUICK STATUS CHECK")
print("="*70)

# Check services
print("\nWhatsApp Service:")
stdin, stdout, stderr = ssh.exec_command('sc query WhatsAppBridgeService')
output = stdout.read().decode('utf-8', errors='ignore')
if 'RUNNING' in output:
    print("  [OK] RUNNING")
elif 'STOPPED' in output:
    print("  [WARN] STOPPED")
else:
    print("  [FAIL] NOT INSTALLED")

# Check IIS sites
print("\nIIS Sites:")
stdin, stdout, stderr = ssh.exec_command('powershell Get-Website ^| Where-Object { $_.Name -like "*WhatsApp*" } ^| Select-Object Name,State')
output = stdout.read().decode('utf-8', errors='ignore')
if 'WhatsAppBridge' in output:
    print("  [OK] Sites exist")
    if 'Started' in output:
        print("  [OK] Sites RUNNING")
    else:
        print("  [WARN] Sites STOPPED")
else:
    print("  [FAIL] Sites NOT CONFIGURED")

# Check ports
print("\nListening Ports:")
stdin, stdout, stderr = ssh.exec_command('netstat -an ^| findstr "LISTENING" ^| findstr ":80 :3000 :5000"')
output = stdout.read().decode('utf-8', errors='ignore')
for port in ['80', '3000', '5000']:
    if f':{port}' in output:
        print(f"  [OK] Port {port}")
    else:
        print(f"  [FAIL] Port {port} not listening")

ssh.close()

print("\n" + "="*70)
