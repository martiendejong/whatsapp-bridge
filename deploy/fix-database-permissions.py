#!/usr/bin/env python3
"""Fix database permissions for IIS app pool"""

import paramiko

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"

SCRIPT = r"""# Fix Database Permissions
Write-Host "Fixing database permissions..."

$apiPath = "C:\inetpub\whatsappbridge-api"
$appPool = "WhatsAppBridgeAPIPool"

# Create Data directory if it doesn't exist
$dataPath = Join-Path $apiPath "Data"
if (-not (Test-Path $dataPath)) {
    New-Item -ItemType Directory -Path $dataPath -Force
    Write-Host "Created Data directory: $dataPath"
}

# Grant full permissions to IIS AppPool identity
$acl = Get-Acl $dataPath
$identity = "IIS AppPool\$appPool"
$permissions = "FullControl"
$inheritanceFlags = "ContainerInherit,ObjectInherit"
$propagationFlags = "None"
$type = "Allow"

$accessRule = New-Object System.Security.AccessControl.FileSystemAccessRule(
    $identity, $permissions, $inheritanceFlags, $propagationFlags, $type
)

$acl.SetAccessRule($accessRule)
Set-Acl -Path $dataPath -AclObject $acl

Write-Host "Granted $permissions to $identity on $dataPath"

# Also grant to parent directory
$acl = Get-Acl $apiPath
$acl.SetAccessRule($accessRule)
Set-Acl -Path $apiPath -AclObject $acl

Write-Host "Granted $permissions to $identity on $apiPath"

# Update appsettings.Production.json with correct database path
$appsettingsPath = Join-Path $apiPath "appsettings.Production.json"
if (Test-Path $appsettingsPath) {
    $settings = Get-Content $appsettingsPath | ConvertFrom-Json
    $dbPath = Join-Path $dataPath "whatsappbridge.db"
    $settings.ConnectionStrings.DefaultConnection = "Data Source=$dbPath"

    $settings | ConvertTo-Json -Depth 10 | Set-Content $appsettingsPath
    Write-Host "Updated database path to: $dbPath"
} else {
    Write-Host "WARNING: appsettings.Production.json not found"
}

# Restart IIS site to apply changes
Stop-Website -Name "WhatsAppBridgeAPI" -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2
Start-Website -Name "WhatsAppBridgeAPI"

Write-Host ""
Write-Host "Database permissions fixed!"
Write-Host "Testing API..."

Start-Sleep -Seconds 3

try {
    $response = Invoke-WebRequest -Uri "http://localhost:5000/swagger/index.html" -UseBasicParsing -TimeoutSec 5
    Write-Host "SUCCESS! API is now responding on port 5000"
    Write-Host "Status Code: $($response.StatusCode)"
} catch {
    Write-Host "ERROR: API still not responding"
    Write-Host $_.Exception.Message
}
"""

ssh = paramiko.SSHClient()
ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD)

print("Fixing database permissions...\n")

# Upload script
remote_script = "C:\\whatsappbridge\\fix-database-permissions.ps1"
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
if errors and "error" in errors.lower():
    print(f"\nErrors:\n{errors}")

print("\n" + "="*70)
print("Done! Check if API is responding now.")
print("="*70)

ssh.close()
