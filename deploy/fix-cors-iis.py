#!/usr/bin/env python3
"""Add CORS headers via IIS for API site"""

import paramiko

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"

SCRIPT = r"""# Add CORS headers via IIS web.config
$apiPath = "C:\inetpub\whatsappbridge-api"
$webConfigPath = Join-Path $apiPath "web.config"

# Read current web.config
[xml]$webConfig = Get-Content $webConfigPath

# Add customHeaders section if not exists
$httpProtocol = $webConfig.SelectSingleNode("//configuration/location/system.webServer/httpProtocol")
if (-not $httpProtocol) {
    $httpProtocol = $webConfig.CreateElement("httpProtocol")
    $systemWebServer = $webConfig.SelectSingleNode("//configuration/location/system.webServer")
    $systemWebServer.AppendChild($httpProtocol) | Out-Null
}

$customHeaders = $httpProtocol.SelectSingleNode("customHeaders")
if (-not $customHeaders) {
    $customHeaders = $webConfig.CreateElement("customHeaders")
    $httpProtocol.AppendChild($customHeaders) | Out-Null
}

# Add CORS headers
$headers = @(
    @{name="Access-Control-Allow-Origin"; value="https://whatsapp.wreckingball.ai"},
    @{name="Access-Control-Allow-Methods"; value="GET, POST, PUT, DELETE, OPTIONS"},
    @{name="Access-Control-Allow-Headers"; value="Content-Type, Authorization"},
    @{name="Access-Control-Allow-Credentials"; value="true"}
)

foreach ($header in $headers) {
    # Remove if exists
    $existing = $customHeaders.SelectSingleNode("add[@name='$($header.name)']")
    if ($existing) {
        $customHeaders.RemoveChild($existing) | Out-Null
    }

    # Add new
    $add = $webConfig.CreateElement("add")
    $add.SetAttribute("name", $header.name)
    $add.SetAttribute("value", $header.value)
    $customHeaders.AppendChild($add) | Out-Null

    Write-Host "Added header: $($header.name)"
}

# Save web.config
$webConfig.Save($webConfigPath)
Write-Host ""
Write-Host "CORS headers added to web.config"

# Restart API site
Stop-Website -Name "WhatsAppBridgeAPI"
Start-Sleep -Seconds 2
Start-Website -Name "WhatsAppBridgeAPI"

Write-Host "API site restarted"
"""

ssh = paramiko.SSHClient()
ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD)

print("Adding CORS headers via IIS...\n")

# Upload script
remote_script = "C:\\whatsappbridge\\fix-cors-iis.ps1"
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
print("CORS headers added via IIS!")
print("\nTry signup again - CORS should now work")
print("="*70)

ssh.close()
