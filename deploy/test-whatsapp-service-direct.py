#!/usr/bin/env python3
"""Test WhatsApp Service directly"""

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

print("Testing WhatsApp Service endpoints...\n")

# Test health endpoint
print("1. Health check:")
out = run_cmd(ssh, r'powershell -Command "try { $r = Invoke-WebRequest -Uri \"http://localhost:3000/health\" -UseBasicParsing; \"Status: $($r.StatusCode), Body: $($r.Content)\" } catch { \"Error: \" + $_.Exception.Message }"')
print(f"  {out}")

# Test create session
print("\n2. Create session:")
out = run_cmd(ssh, r'''powershell -Command "try { $r = Invoke-WebRequest -Uri 'http://localhost:3000/sessions/create' -Method POST -ContentType 'application/json' -Body '{}' -UseBasicParsing; 'Status: ' + $r.StatusCode + ', Body: ' + $r.Content } catch { 'Error: ' + $_.Exception.Message }"''')
print(f"  {out}")

# Check service logs
print("\n3. Recent service logs:")
out = run_cmd(ssh, r'powershell -Command "Get-Content C:\Services\WhatsAppBridge\logs\service.log -Tail 20 -ErrorAction SilentlyContinue"')
if out:
    print(out)
else:
    print("  No logs found")

# Check if process is still running
print("\n4. Process status:")
out = run_cmd(ssh, r'powershell -Command "Get-Process node -ErrorAction SilentlyContinue | Select-Object Id,ProcessName,@{Name=\"WorkingSet\";Expression={[math]::Round($_.WorkingSet/1MB,2)}}"')
print(f"  {out if out else 'No node process running'}")

ssh.close()
