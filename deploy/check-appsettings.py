#!/usr/bin/env python3
"""Check appsettings configuration"""

import paramiko
import json

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"

def run_cmd(ssh, cmd):
    stdin, stdout, stderr = ssh.exec_command(cmd)
    return stdout.read().decode('utf-8', errors='ignore').strip()

ssh = paramiko.SSHClient()
ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD)

print("Checking appsettings files...\n")

# List all appsettings files
out = run_cmd(ssh, r'powershell -Command "Get-ChildItem C:\inetpub\whatsappbridge-api\appsettings*.json | Select-Object Name,Length"')
print("Available appsettings files:")
print(out if out else "  None found")

# Read appsettings.json
print("\n" + "="*70)
print("appsettings.json content:")
out = run_cmd(ssh, r'powershell -Command "Get-Content C:\inetpub\whatsappbridge-api\appsettings.json"')
print(out if out else "  File not found")

# Check ASPNETCORE_ENVIRONMENT
print("\n" + "="*70)
print("Checking environment variables...")
out = run_cmd(ssh, r'powershell -Command "$site = Get-Website -Name WhatsAppBridgeAPI; $app = Get-WebApplication -Name \"/\" -Site $site.Name; $config = Get-WebConfiguration -Filter \"system.webServer/aspNetCore\" -PSPath \"IIS:\Sites\$($site.Name)\"; $config.environmentVariables.Collection | Format-Table name,value"')
if out:
    print("Environment variables:")
    print(out)
else:
    print("  No environment variables set")

# Test /api/auth/register endpoint
print("\n" + "="*70)
print("Testing /api/auth/register endpoint...")
out = run_cmd(ssh, r'''powershell -Command "$body = @{email='test@example.com';password='Test123!'} | ConvertTo-Json; try { $response = Invoke-WebRequest -Uri 'http://localhost:5000/api/auth/register' -Method POST -Body $body -ContentType 'application/json' -UseBasicParsing -TimeoutSec 5; 'Status: ' + $response.StatusCode + ', Content: ' + $response.Content } catch { 'Error: ' + $_.Exception.Message }"''')
print(out if out else "  Request failed")

# Test root endpoint
print("\n" + "="*70)
print("Testing root endpoint (/)...")
out = run_cmd(ssh, r'powershell -Command "try { $response = Invoke-WebRequest -Uri \"http://localhost:5000/\" -UseBasicParsing -TimeoutSec 5; \"Status: $($response.StatusCode)\" } catch { \"Error: \" + $_.Exception.Message }"')
print(out)

ssh.close()
