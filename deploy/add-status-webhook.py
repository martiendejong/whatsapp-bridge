#!/usr/bin/env python3
"""Add webhook to notify backend when WhatsApp connects"""

import paramiko

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"

# Read current index.js from server
ssh = paramiko.SSHClient()
ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD)

print("="*70)
print("Adding Status Webhook to WhatsApp Service")
print("="*70)

print("\n1. Reading current index.js from server...")
sftp = ssh.open_sftp()
with sftp.file('/C:/Services/WhatsAppBridge/index.js', 'r') as f:
    content = f.read().decode('utf-8')

# Add import for axios at the top
print("\n2. Adding axios import...")
if 'import axios from' not in content:
    # Add after the other imports
    content = content.replace(
        "import cors from 'cors';",
        "import cors from 'cors';\nimport axios from 'axios';"
    )

# Add webhook function
print("\n3. Adding webhook notification function...")
webhook_function = """
// Webhook to notify backend when status changes
async function notifyBackendStatusChange(sessionId, status, phoneNumber = null) {
    try {
        const backendUrl = 'http://localhost:5000/api/whatsapp/webhook/status';
        await axios.post(backendUrl, {
            sessionId,
            status,
            phoneNumber
        }, {
            timeout: 5000
        });
        console.log(`Notified backend: session ${sessionId} -> ${status}`);
    } catch (error) {
        console.error(`Failed to notify backend for session ${sessionId}:`, error.message);
    }
}

"""

# Insert webhook function before the create session endpoint
if 'function notifyBackendStatusChange' not in content:
    content = content.replace(
        '// Create new WhatsApp session and get QR code',
        webhook_function + '// Create new WhatsApp session and get QR code'
    )

# Update the 'ready' event handler to call webhook
print("\n4. Updating 'ready' event handler...")
old_ready_handler = """        client.on('ready', () => {
            console.log(`WhatsApp client ready for session ${sessionId}`);
        });"""

new_ready_handler = """        client.on('ready', async () => {
            console.log(`WhatsApp client ready for session ${sessionId}`);

            // Get phone number
            const info = await client.info;
            const phoneNumber = info.wid.user;

            // Notify backend
            await notifyBackendStatusChange(sessionId, 'connected', phoneNumber);
        });"""

content = content.replace(old_ready_handler, new_ready_handler)

# Update the 'authenticated' event handler
print("\n5. Updating 'authenticated' event handler...")
old_auth_handler = """        client.on('authenticated', () => {
            console.log(`Session ${sessionId} authenticated`);
        });"""

new_auth_handler = """        client.on('authenticated', async () => {
            console.log(`Session ${sessionId} authenticated`);
            await notifyBackendStatusChange(sessionId, 'authenticated');
        });"""

content = content.replace(old_auth_handler, new_auth_handler)

# Update 'disconnected' event handler
print("\n6. Updating 'disconnected' event handler...")
old_disc_handler = """        client.on('disconnected', (reason) => {
            console.log(`Session ${sessionId} disconnected:`, reason);
            clients.delete(sessionId);
        });"""

new_disc_handler = """        client.on('disconnected', async (reason) => {
            console.log(`Session ${sessionId} disconnected:`, reason);
            await notifyBackendStatusChange(sessionId, 'disconnected');
            clients.delete(sessionId);
        });"""

content = content.replace(old_disc_handler, new_disc_handler)

# Write updated file
print("\n7. Writing updated index.js...")
with sftp.file('/C:/Services/WhatsAppBridge/index.js', 'w') as f:
    f.write(content)

sftp.close()

# Install axios if not already installed
print("\n8. Installing axios...")
stdin, stdout, stderr = ssh.exec_command('cd C:\\Services\\WhatsAppBridge && npm install axios')
output = stdout.read().decode('utf-8', errors='ignore')
print("   " + (output.strip().split('\n')[-1] if output else "Done"))

# Restart service
print("\n9. Restarting WhatsApp Service...")
ssh.exec_command('powershell -Command "Get-Process node -ErrorAction SilentlyContinue | Stop-Process -Force"')
import time
time.sleep(2)

ssh.exec_command('powershell -Command "Start-ScheduledTask -TaskName WhatsAppBridgeService"')
time.sleep(5)

# Verify
stdin, stdout, stderr = ssh.exec_command('powershell -Command "netstat -ano | findstr :3000"')
port = stdout.read().decode('utf-8', errors='ignore').strip()

if port:
    print("   Service running on port 3000")
else:
    print("   WARNING: Service not running")

ssh.close()

print("\n" + "="*70)
print("Webhook added successfully!")
print("WhatsApp Service will now notify backend when status changes:")
print("  - authenticated → backend updated")
print("  - ready (connected) → backend updated with phone number")
print("  - disconnected → backend updated")
print("="*70)
