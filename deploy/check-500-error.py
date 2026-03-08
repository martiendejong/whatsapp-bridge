#!/usr/bin/env python3
"""Check latest 500 error in backend"""

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

print("Checking recent errors...\n")

# Get latest event log errors
out = run_cmd(ssh, r'powershell -Command "Get-EventLog -LogName Application -Source \"IIS AspNetCore Module V2\" -Newest 3 -ErrorAction SilentlyContinue | Format-List TimeGenerated,EntryType,Message"')
print("Latest Event Log Entries:")
print(out if out else "No errors found")

print("\n" + "="*70)
print("Checking IIS logs...")

# Get latest IIS log file
out = run_cmd(ssh, r'powershell -Command "$site = Get-Website -Name WhatsAppBridgeWeb; $logPath = \"$($site.logFile.directory)\W3SVC$($site.id)\"; Get-ChildItem $logPath -Filter *.log -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName"')
if out:
    log_file = out
    print(f"Latest IIS log: {log_file}")

    # Get last 20 lines
    out = run_cmd(ssh, f'powershell -Command "Get-Content \"{log_file}\" -Tail 20 | Where-Object {{ $_ -like \"*500*\" }}"')
    if out:
        print("\n500 Errors in IIS log:")
        print(out)
    else:
        print("  No 500 errors in recent logs")
else:
    print("  Could not find IIS log")

print("\n" + "="*70)
print("Testing backend directly...")

# Test backend without reverse proxy
out = run_cmd(ssh, r'''powershell -Command "try { $response = Invoke-WebRequest -Uri 'http://localhost:5000/api/auth/register' -Method POST -Body (@{email='direct-test@test.com';password='Test123!'} | ConvertTo-Json) -ContentType 'application/json' -UseBasicParsing; 'Status: ' + $response.StatusCode + ', Body: ' + $response.Content } catch { 'Error: ' + $_.Exception.Message; if ($_.Exception.Response) { 'Status Code: ' + $_.Exception.Response.StatusCode.value__ } }"''')
print("Direct backend test:")
print(out)

ssh.close()
