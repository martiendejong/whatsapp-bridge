#!/usr/bin/env python3
import paramiko
import time

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"

print("="*70)
print("INSTALLING NSSM VIA CHOCOLATEY")
print("="*70)

ssh = paramiko.SSHClient()
ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD)

print("\nInstalling NSSM...")
stdin, stdout, stderr = ssh.exec_command('choco install nssm -y')

for line in stdout:
    line_str = line.rstrip()
    if line_str:
        print(line_str)

time.sleep(3)

print("\n" + "="*70)
print("Verifying installation...")
stdin, stdout, stderr = ssh.exec_command('nssm version')
output = stdout.read().decode('utf-8', errors='ignore')

if 'NSSM' in output or '2.24' in output:
    print("[OK] NSSM installed successfully!")
    print(output)
else:
    print("[FAIL] NSSM verification failed")
    print(stderr.read().decode('utf-8', errors='ignore'))

ssh.close()
print("="*70)
