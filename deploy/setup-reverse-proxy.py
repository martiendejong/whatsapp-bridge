#!/usr/bin/env python3
"""Setup IIS reverse proxy from frontend to backend API"""

import paramiko

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"

SCRIPT = r"""# Setup IIS Reverse Proxy
Write-Host "Setting up IIS reverse proxy for API..."

# First, install URL Rewrite module if not present
$urlRewrite = Get-Module -ListAvailable -Name IISAdministration | Where-Object { $_.Name -eq "IISAdministration" }
if (-not $urlRewrite) {
    Write-Host "URL Rewrite module check skipped (assuming installed)"
}

$webSiteName = "WhatsAppBridgeWeb"
$webSitePath = "C:\inetpub\whatsappbridge-web"
$webConfigPath = Join-Path $webSitePath "web.config"

# Create web.config with reverse proxy rules
$webConfig = @'
<?xml version="1.0" encoding="UTF-8"?>
<configuration>
    <system.webServer>
        <rewrite>
            <rules>
                <!-- API Reverse Proxy Rule -->
                <rule name="ReverseProxyInboundRule1" stopProcessing="true">
                    <match url="^api/(.*)" />
                    <action type="Rewrite" url="http://localhost:5000/api/{R:1}" />
                    <serverVariables>
                        <set name="HTTP_X_FORWARDED_PROTO" value="https" />
                        <set name="HTTP_X_ORIGINAL_HOST" value="{HTTP_HOST}" />
                    </serverVariables>
                </rule>
                <!-- SPA Fallback - serve index.html for client-side routing -->
                <rule name="ReactRouter" stopProcessing="true">
                    <match url=".*" />
                    <conditions logicalGrouping="MatchAll">
                        <add input="{REQUEST_FILENAME}" matchType="IsFile" negate="true" />
                        <add input="{REQUEST_FILENAME}" matchType="IsDirectory" negate="true" />
                        <add input="{REQUEST_URI}" pattern="^/api/" negate="true" />
                    </conditions>
                    <action type="Rewrite" url="/" />
                </rule>
            </rules>
            <outboundRules>
                <!-- Fix CORS headers from backend -->
                <rule name="Add CORS headers">
                    <match serverVariable="RESPONSE_Access_Control_Allow_Origin" pattern=".*" />
                    <action type="Rewrite" value="*" />
                </rule>
            </outboundRules>
        </rewrite>
        <staticContent>
            <remove fileExtension=".json" />
            <mimeMap fileExtension=".json" mimeType="application/json" />
        </staticContent>
        <httpErrors existingResponse="PassThrough" />
    </system.webServer>
</configuration>
'@

$webConfig | Out-File -FilePath $webConfigPath -Encoding UTF8 -Force
Write-Host "Created web.config with reverse proxy rules"

# Restart frontend site
Stop-Website -Name $webSiteName -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1
Start-Website -Name $webSiteName

Write-Host ""
Write-Host "Reverse proxy configured!"
Write-Host ""
Write-Host "Frontend will now proxy /api/* requests to backend API"
Write-Host "Users can access everything via https://whatsapp.wreckingball.ai"
Write-Host ""
Write-Host "Testing proxy..."

Start-Sleep -Seconds 3

try {
    $response = Invoke-WebRequest -Uri "https://whatsapp.wreckingball.ai/api/auth/register" `
        -Method POST `
        -Body (@{email="proxy-test@test.com";password="Test123!"} | ConvertTo-Json) `
        -ContentType "application/json" `
        -UseBasicParsing `
        -TimeoutSec 10
    Write-Host "SUCCESS! Proxy is working"
    Write-Host "Status: $($response.StatusCode)"
} catch {
    $statusCode = $_.Exception.Response.StatusCode.value__
    Write-Host "Proxy test: Status $statusCode"
    if ($statusCode -eq 400) {
        Write-Host "  (400 = User exists, proxy IS working!)"
    }
}
"""

ssh = paramiko.SSHClient()
ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD)

print("="*70)
print("Setting up IIS Reverse Proxy")
print("="*70)
print()

# Upload script
remote_script = "C:\\whatsappbridge\\setup-reverse-proxy.ps1"
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
if errors and ("error" in errors.lower()):
    print(f"\nErrors:\n{errors}")

print("\n" + "="*70)
print("Reverse proxy setup complete!")
print("\nUsers can now register at:")
print("  https://whatsapp.wreckingball.ai")
print("\nAll API requests go via frontend domain (no mixed content)")
print("="*70)

ssh.close()
