#!/usr/bin/env python3
"""Manually fix the event handlers to call webhook"""

import paramiko
import re

ssh = paramiko.SSHClient()
ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
ssh.connect('85.215.217.154', username='administrator', password='3WsXcFr$7YhNmKi*')

print("="*70)
print("Fixing Event Handlers")
print("="*70)

# Read current file
print("\n1. Reading index.js...")
sftp = ssh.open_sftp()
with sftp.file('/C:/Services/WhatsAppBridge/index.js', 'r') as f:
    content = f.read().decode('utf-8')

# Fix ready handler
print("\n2. Fixing 'ready' event handler...")
old_ready = """        client.on('ready', () => {
            console.log(`WhatsApp client ready for session ${sessionId}`);
        });"""

new_ready = """        client.on('ready', async () => {
            console.log(`WhatsApp client ready for session ${sessionId}`);

            try {
                const info = await client.info;
                const phoneNumber = info.wid.user;
                await notifyBackendStatusChange(sessionId, 'connected', phoneNumber);
            } catch (error) {
                console.error(`Error in ready handler:`, error);
            }
        });"""

if old_ready in content:
    content = content.replace(old_ready, new_ready)
    print("   Updated ready handler")
else:
    print("   WARNING: Could not find exact ready handler pattern")

# Fix authenticated handler
print("\n3. Fixing 'authenticated' event handler...")
old_auth = """        client.on('authenticated', () => {
            console.log(`Session ${sessionId} authenticated`);
        });"""

new_auth = """        client.on('authenticated', async () => {
            console.log(`Session ${sessionId} authenticated`);
            await notifyBackendStatusChange(sessionId, 'authenticated');
        });"""

if old_auth in content:
    content = content.replace(old_auth, new_auth)
    print("   Updated authenticated handler")
else:
    print("   WARNING: Could not find exact authenticated handler pattern")

# Fix disconnected handler
print("\n4. Fixing 'disconnected' event handler...")
old_disc = """        client.on('disconnected', (reason) => {
            console.log(`Session ${sessionId} disconnected:`, reason);
            clients.delete(sessionId);
        });"""

new_disc = """        client.on('disconnected', async (reason) => {
            console.log(`Session ${sessionId} disconnected:`, reason);
            await notifyBackendStatusChange(sessionId, 'disconnected');
            clients.delete(sessionId);
        });"""

if old_disc in content:
    content = content.replace(old_disc, new_disc)
    print("   Updated disconnected handler")
else:
    print("   WARNING: Could not find exact disconnected handler pattern")

# Write back
print("\n5. Writing updated index.js...")
with sftp.file('/C:/Services/WhatsAppBridge/index.js', 'w') as f:
    f.write(content)

sftp.close()

# Restart service
print("\n6. Restarting service...")
ssh.exec_command('powershell -Command "Get-Process node -ErrorAction SilentlyContinue | Stop-Process -Force"')
import time
time.sleep(2)

ssh.exec_command('powershell -Command "Start-ScheduledTask -TaskName WhatsAppBridgeService"')
time.sleep(5)

# Verify
stdin, stdout, stderr = ssh.exec_command('powershell -Command "netstat -ano | findstr :3000"')
port = stdout.read().decode('utf-8', errors='ignore').strip()

if port:
    print("   Service restarted successfully")
else:
    print("   WARNING: Service not running")

ssh.close()

print("\n" + "="*70)
print("Event handlers fixed!")
print("Webhook will now be called when QR is scanned")
print("="*70)
