#!/usr/bin/env python3
import paramiko

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"

ssh = paramiko.SSHClient()
ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD)

print("Searching for NSSM...")

# Check common locations
locations = [
    'C:\\Windows\\System32\\nssm.exe',
    'C:\\ProgramData\\chocolatey\\bin\\nssm.exe',
    'C:\\ProgramData\\chocolatey\\lib\\NSSM\\tools\\nssm.exe',
    'C:\\Program Files\\NSSM\\nssm.exe'
]

for loc in locations:
    cmd = f'if exist "{loc}" (echo EXISTS) else (echo MISSING)'
    stdin, stdout, stderr = ssh.exec_command(cmd)
    result = stdout.read().decode('utf-8', errors='ignore').strip()
    print(f"{loc}: {result}")

# Search in ProgramData\chocolatey
print("\nChecking chocolatey directory...")
stdin, stdout, stderr = ssh.exec_command('dir /S /B C:\\ProgramData\\chocolatey\\*nssm.exe 2>nul')
output = stdout.read().decode('utf-8', errors='ignore')
if output.strip():
    print("Found:")
    print(output)
else:
    print("No nssm.exe found in chocolatey directory")

ssh.close()
