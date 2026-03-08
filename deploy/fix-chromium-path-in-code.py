#!/usr/bin/env python3
"""Fix Chromium path directly in index.js"""

import paramiko

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"

ssh = paramiko.SSHClient()
ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD)

print("="*70)
print("Fixing Chromium Path in index.js")
print("="*70)

# Read current index.js
print("\n1. Reading current index.js...")
sftp = ssh.open_sftp()
with sftp.file('/C:/Services/WhatsAppBridge/index.js', 'r') as f:
    content = f.read().decode('utf-8')

# Find the Chromium path
print("\n2. Finding Chromium executable...")
stdin, stdout, stderr = ssh.exec_command(
    'powershell -Command "Get-ChildItem -Path C:\\Users\\Administrator\\.cache\\puppeteer -Filter chrome.exe -Recurse | Select-Object -First 1 -ExpandProperty FullName"'
)
chromium_path = stdout.read().decode('utf-8', errors='ignore').strip()

if not chromium_path:
    print("   ERROR: Chrome executable not found!")
    ssh.close()
    exit(1)

print(f"   Found: {chromium_path}")

# Update index.js to use explicit path
print("\n3. Updating index.js with explicit Chromium path...")

# Find the puppeteer config section and add executablePath
old_puppeteer_config = """        const client = new Client({
            authStrategy: new LocalAuth({ clientId: sessionId }),
            puppeteer: {
                headless: true,
                args: ['--no-sandbox', '--disable-setuid-sandbox']
            }
        });"""

new_puppeteer_config = f"""        const client = new Client({{
            authStrategy: new LocalAuth({{ clientId: sessionId }}),
            puppeteer: {{
                headless: true,
                args: ['--no-sandbox', '--disable-setuid-sandbox'],
                executablePath: '{chromium_path.replace(chr(92), chr(92)*2)}'
            }}
        }});"""

updated_content = content.replace(old_puppeteer_config, new_puppeteer_config)

if updated_content == content:
    print("   WARNING: Could not find puppeteer config to replace")
    print("   Trying alternative approach...")

    # Alternative: just add executablePath line
    import re
    pattern = r"(puppeteer: \{[^}]*args: \['--no-sandbox', '--disable-setuid-sandbox'\])"
    replacement = f"\\1,\n                executablePath: '{chromium_path.replace(chr(92), chr(92)*2)}'"
    updated_content = re.sub(pattern, replacement, content)

# Write back
print("\n4. Writing updated index.js...")
with sftp.file('/C:/Services/WhatsAppBridge/index.js', 'w') as f:
    f.write(updated_content)

sftp.close()

# Restart service
print("\n5. Restarting service...")
ssh.exec_command('powershell -Command "Get-Process node -ErrorAction SilentlyContinue | Stop-Process -Force"')
import time
time.sleep(2)

ssh.exec_command('powershell -Command "Start-ScheduledTask -TaskName WhatsAppBridgeService"')
time.sleep(5)

# Verify
print("\n6. Verifying service...")
stdin, stdout, stderr = ssh.exec_command('powershell -Command "netstat -ano | findstr :3000"')
port = stdout.read().decode('utf-8', errors='ignore').strip()

if port:
    print("   Service running on port 3000")
else:
    print("   WARNING: Service not running")

ssh.close()

print("\n" + "="*70)
print("index.js updated with explicit Chromium path!")
print(f"executablePath: {chromium_path}")
print("="*70)
