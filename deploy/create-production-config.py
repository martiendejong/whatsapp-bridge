#!/usr/bin/env python3
"""Create production appsettings"""

import paramiko
import json
import secrets

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"

# Generate random JWT key
jwt_key = secrets.token_urlsafe(48)

PRODUCTION_SETTINGS = {
    "ConnectionStrings": {
        "DefaultConnection": "Data Source=C:\\inetpub\\whatsappbridge-api\\Data\\whatsappbridge.db"
    },
    "Jwt": {
        "Key": jwt_key,
        "Issuer": "WhatsAppBridge",
        "Audience": "WhatsAppBridgeUsers"
    },
    "WhatsAppService": {
        "Url": "http://localhost:3000"
    },
    "AllowedOrigins": [
        "https://whatsapp.wreckingball.ai",
        "http://whatsapp.wreckingball.ai",
        "http://localhost:5173"
    ],
    "Encryption": {
        "Enabled": False,
        "Key": "",
        "IV": ""
    }
}

SCRIPT = f"""# Create production configuration
$apiPath = "C:\\inetpub\\whatsappbridge-api"
$settingsPath = Join-Path $apiPath "appsettings.Production.json"

# Create settings file
$settings = @'
{json.dumps(PRODUCTION_SETTINGS, indent=2)}
'@

$settings | Out-File -FilePath $settingsPath -Encoding UTF8
Write-Host "Created appsettings.Production.json"

# Set ASPNETCORE_ENVIRONMENT to Production
Import-Module WebAdministration
$siteName = "WhatsAppBridgeAPI"
Set-WebConfigurationProperty -Filter "system.webServer/aspNetCore" -PSPath "IIS:\\Sites\\$siteName" -Name "environmentVariables" -Value @{{Collection=@(
    @{{name="ASPNETCORE_ENVIRONMENT";value="Production"}}
)}}
Write-Host "Set ASPNETCORE_ENVIRONMENT=Production"

# Restart site
Stop-Website -Name $siteName
Start-Sleep -Seconds 2
Start-Website -Name $siteName

Write-Host ""
Write-Host "Configuration applied! Waiting for app to start..."
Start-Sleep -Seconds 5

# Test API
try {{
    $response = Invoke-WebRequest -Uri "http://localhost:5000/api/auth/register" -Method POST `
        -Body (@{{email="test@test.com";password="Test123!"}} | ConvertTo-Json) `
        -ContentType "application/json" -UseBasicParsing -TimeoutSec 10
    Write-Host "SUCCESS! API is responding"
    Write-Host "Status: $($response.StatusCode)"
}} catch {{
    Write-Host "Testing endpoint... Status: $($_.Exception.Response.StatusCode.value__)"
    Write-Host "Message: $($_.Exception.Message)"
}}
"""

ssh = paramiko.SSHClient()
ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD)

print("Creating production configuration...\n")
print(f"Generated JWT Key: {jwt_key[:20]}...\n")

# Upload script
remote_script = "C:\\whatsappbridge\\create-production-config.ps1"
sftp = ssh.open_sftp()
with sftp.file(remote_script, 'w') as f:
    f.write(SCRIPT)
sftp.close()

# Execute
stdin, stdout, stderr = ssh.exec_command(f'powershell -ExecutionPolicy Bypass -File "{remote_script}"')

for line in stdout:
    line = line.strip()
    if line:
        print(f"  {line}")

errors = stderr.read().decode('utf-8', errors='ignore')
if errors and ("error" in errors.lower() or "exception" in errors.lower()):
    print(f"\nErrors/Warnings:\n{errors}")

print("\n" + "="*70)
print("Production configuration complete!")
print("\nAPI should now be accessible at:")
print("  http://whatsapp.wreckingball.ai:5000/api/auth/register")
print("  https://whatsapp.wreckingball.ai:5001/api/auth/register")
print("\nNext: Test signup from frontend")
print("="*70)

ssh.close()
