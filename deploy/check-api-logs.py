#!/usr/bin/env python3
"""Check API logs to see why it's not responding"""

import paramiko

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"

def run_cmd(ssh, cmd):
    stdin, stdout, stderr = ssh.exec_command(cmd)
    return stdout.read().decode('utf-8', errors='ignore').strip()

ssh = paramiko.SSHClient()
ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD)

print("Checking WhatsApp Bridge API logs...\n")

# Check if log directory exists
print("Log Directory:")
out = run_cmd(ssh, r'powershell -Command "Test-Path C:\inetpub\whatsappbridge-api\logs"')
if "True" in out:
    print("  Directory exists")

    # Get latest log file
    out = run_cmd(ssh, r'powershell -Command "Get-ChildItem C:\inetpub\whatsappbridge-api\logs\*.log -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1 | Select-Object Name,LastWriteTime,Length | Format-List"')
    print(out if out else "  No log files found")

    # Read last 50 lines of latest log
    out = run_cmd(ssh, r'powershell -Command "Get-ChildItem C:\inetpub\whatsappbridge-api\logs\*.log -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1 | Get-Content -Tail 50"')
    if out:
        print("\nLast 50 lines of latest log:")
        print(out)
else:
    print("  Logs directory doesn't exist yet")

# Check IIS application event log for ASP.NET Core errors
print("\n" + "="*70)
print("Recent ASP.NET Core Errors:")
out = run_cmd(ssh, r'powershell -Command "Get-EventLog -LogName Application -Source \"IIS AspNetCore Module V2\" -Newest 10 -ErrorAction SilentlyContinue | Format-List TimeGenerated,Message"')
if out:
    print(out)
else:
    print("  No recent errors in event log")

# Check if DLL exists
print("\n" + "="*70)
print("Checking API files:")
out = run_cmd(ssh, r'powershell -Command "Test-Path C:\inetpub\whatsappbridge-api\WhatsAppBridge.API.dll"')
print(f"  WhatsAppBridge.API.dll exists: {out}")

# Check web.config
print("\nweb.config:")
out = run_cmd(ssh, r'powershell -Command "Get-Content C:\inetpub\whatsappbridge-api\web.config -ErrorAction SilentlyContinue"')
if out:
    print(out)
else:
    print("  web.config not found!")

# Try to manually test the DLL
print("\n" + "="*70)
print("Testing if DLL can run...")
out = run_cmd(ssh, r'powershell -Command "cd C:\inetpub\whatsappbridge-api; dotnet WhatsAppBridge.API.dll --urls http://localhost:5002 2>&1 | Select-Object -First 20"')
print(out if out else "  Could not test DLL directly")

ssh.close()
