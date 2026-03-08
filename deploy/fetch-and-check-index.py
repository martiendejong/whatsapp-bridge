#!/usr/bin/env python3
"""Fetch index.js and check if webhook code is present"""

import paramiko

ssh = paramiko.SSHClient()
ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
ssh.connect('85.215.217.154', username='administrator', password='3WsXcFr$7YhNmKi*')

stdin, stdout, stderr = ssh.exec_command('type C:\\Services\\WhatsAppBridge\\index.js')
content = stdout.read().decode('utf-8', errors='ignore')

ssh.close()

# Check for key webhook components
print("="*70)
print("Checking index.js for webhook code")
print("="*70)

checks = [
    ("axios import", "import axios from 'axios'"),
    ("notifyBackendStatusChange function", "function notifyBackendStatusChange"),
    ("Webhook in ready handler", "await notifyBackendStatusChange"),
    ("async ready handler", "client.on('ready', async"),
]

all_good = True
for check_name, search_term in checks:
    if search_term in content:
        print(f"[OK] {check_name}")
    else:
        print(f"[MISSING] {check_name}")
        all_good = False

print("\n" + "="*70)

if not all_good:
    print("Webhook code is NOT properly installed!")
    print("\nShowing 'ready' event handler:")
    print("="*70)

    # Find and show the ready handler
    lines = content.split('\n')
    for i, line in enumerate(lines):
        if "client.on('ready'" in line:
            # Show 10 lines around it
            start = max(0, i-2)
            end = min(len(lines), i+10)
            for j in range(start, end):
                print(f"{j+1:4d}: {lines[j]}")
            break
else:
    print("All webhook code is present!")
    print("\nLet's check error log for webhook failures...")
