#!/usr/bin/env python3
"""Check IIS status"""

import paramiko

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"

ssh = paramiko.SSHClient()
ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD)

print("="*70)
print("IIS Status Check")
print("="*70)

# Check app pool
print("\n1. App Pool Status:")
stdin, stdout, stderr = ssh.exec_command('powershell -Command "Get-WebAppPoolState -Name WhatsAppBridgeAPIPool"')
print(stdout.read().decode('utf-8', errors='ignore').strip())

# Check website
print("\n2. Website Status:")
stdin, stdout, stderr = ssh.exec_command('powershell -Command "Get-WebsiteState -Name WhatsAppBridgeAPI"')
print(stdout.read().decode('utf-8', errors='ignore').strip())

# Check port bindings
print("\n3. Port Bindings:")
stdin, stdout, stderr = ssh.exec_command('powershell -Command "Get-WebBinding -Name WhatsAppBridgeAPI | Format-List"')
print(stdout.read().decode('utf-8', errors='ignore').strip())

# Check if ports are listening
print("\n4. Listening Ports:")
stdin, stdout, stderr = ssh.exec_command('powershell -Command "netstat -ano | findstr :5000"')
port_5000 = stdout.read().decode('utf-8', errors='ignore').strip()
if port_5000:
    print(f"   Port 5000: LISTENING")
else:
    print(f"   Port 5000: NOT listening")

stdin, stdout, stderr = ssh.exec_command('powershell -Command "netstat -ano | findstr :5001"')
port_5001 = stdout.read().decode('utf-8', errors='ignore').strip()
if port_5001:
    print(f"   Port 5001: LISTENING")
else:
    print(f"   Port 5001: NOT listening")

# Check event log for errors
print("\n5. Recent Application Errors (last 5):")
stdin, stdout, stderr = ssh.exec_command(
    'powershell -Command "Get-EventLog -LogName Application -Source \\".NET Runtime\\" -Newest 5 -ErrorAction SilentlyContinue | Format-List TimeGenerated,Message"'
)
errors = stdout.read().decode('utf-8', errors='ignore').strip()
if errors:
    print(errors)
else:
    print("   No recent .NET errors")

ssh.close()

print("\n" + "="*70)
