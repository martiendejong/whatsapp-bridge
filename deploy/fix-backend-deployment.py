#!/usr/bin/env python3
"""Fix backend API deployment - create IIS site with reverse proxy"""

import paramiko
import sys

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"

# PowerShell script to create IIS API site
SETUP_SCRIPT = r"""
# Create IIS site for WhatsApp API
$siteName = "WhatsAppBridgeAPI"
$appPool = "WhatsAppBridgeAPIPool"
$physicalPath = "C:\inetpub\whatsappbridge-api"
$port = 5000

Write-Host "Creating IIS site for WhatsApp Bridge API..."

# Create site if not exists
$site = Get-Website -Name $siteName -ErrorAction SilentlyContinue
if (-not $site) {
    New-Website -Name $siteName `
        -ApplicationPool $appPool `
        -PhysicalPath $physicalPath `
        -Port $port `
        -Force
    Write-Host "  Created IIS site: $siteName"
} else {
    Write-Host "  Site already exists: $siteName"
}

# Ensure app pool is configured correctly
$pool = Get-IISAppPool -Name $appPool
$pool.ManagedRuntimeVersion = ""  # No Managed Code for .NET Core
$pool.StartMode = "AlwaysRunning"
$pool | Set-IISAppPool

# Start the site
Start-Website -Name $siteName
Write-Host "  Started website: $siteName"

# Add HTTPS binding for API (port 5001)
$httpsBinding = Get-WebBinding -Name $siteName -Protocol "https" -ErrorAction SilentlyContinue
if (-not $httpsBinding) {
    # Get SSL certificate
    $cert = Get-ChildItem -Path Cert:\LocalMachine\WebHosting | Where-Object { $_.Subject -like "*whatsapp.wreckingball.ai*" } | Select-Object -First 1

    if ($cert) {
        New-WebBinding -Name $siteName -Protocol "https" -Port 5001 -SslFlags 1

        # Bind certificate
        $binding = Get-WebBinding -Name $siteName -Protocol "https"
        $binding.AddSslCertificate($cert.Thumbprint, "WebHosting")

        Write-Host "  Added HTTPS binding on port 5001"
    } else {
        Write-Host "  WARNING: No SSL certificate found for whatsapp.wreckingball.ai"
    }
}

# Configure CORS in web.config if needed
$webConfigPath = Join-Path $physicalPath "web.config"
if (-not (Test-Path $webConfigPath)) {
    $webConfig = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <location path="." inheritInChildApplications="false">
    <system.webServer>
      <handlers>
        <add name="aspNetCore" path="*" verb="*" modules="AspNetCoreModuleV2" resourceType="Unspecified" />
      </handlers>
      <aspNetCore processPath="dotnet" arguments=".\WhatsAppBridge.API.dll" stdoutLogEnabled="false" stdoutLogFile=".\logs\stdout" hostingModel="inprocess" />
      <httpProtocol>
        <customHeaders>
          <add name="Access-Control-Allow-Origin" value="https://whatsapp.wreckingball.ai" />
          <add name="Access-Control-Allow-Methods" value="GET, POST, PUT, DELETE, OPTIONS" />
          <add name="Access-Control-Allow-Headers" value="Content-Type, Authorization" />
        </customHeaders>
      </httpProtocol>
    </system.webServer>
  </location>
</configuration>
"@
    $webConfig | Out-File -FilePath $webConfigPath -Encoding UTF8
    Write-Host "  Created web.config with CORS headers"
}

# Test if API is now running
Start-Sleep -Seconds 3
$response = $null
try {
    $response = Invoke-WebRequest -Uri "http://localhost:5000/swagger/index.html" -UseBasicParsing -TimeoutSec 5
    Write-Host "  API is responding on port 5000!"
} catch {
    Write-Host "  WARNING: API not responding yet on port 5000"
}

Write-Host "`nBackend API deployment completed!"
Write-Host "  HTTP:  http://whatsapp.wreckingball.ai:5000"
Write-Host "  HTTPS: https://whatsapp.wreckingball.ai:5001"
"""

def main():
    print("="*70)
    print("Fixing WhatsApp Bridge Backend Deployment")
    print("="*70)

    try:
        ssh = paramiko.SSHClient()
        ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
        ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD, timeout=10)
        print(f"\nConnected to {SSH_HOST}\n")

        # Execute setup script
        print("Executing IIS configuration script...")
        stdin, stdout, stderr = ssh.exec_command(f'powershell -Command "{SETUP_SCRIPT}"', get_pty=True)

        # Read output
        output = stdout.read().decode('utf-8', errors='ignore')
        error = stderr.read().decode('utf-8', errors='ignore')

        # Print output
        for line in output.split('\n'):
            if line.strip() and not line.startswith('['):
                print(line)

        if error and "error" in error.lower():
            print(f"\nErrors:\n{error}")

        # Verify ports are listening
        print("\n" + "-"*70)
        print("Verifying ports...")
        stdin, stdout, stderr = ssh.exec_command('netstat -ano | findstr "LISTENING" | findstr ":5000 :5001"')
        output = stdout.read().decode('utf-8', errors='ignore')

        if output.strip():
            print("Ports listening:")
            for line in output.split('\n')[:5]:
                if line.strip():
                    print(f"  {line.strip()}")
        else:
            print("  WARNING: Ports 5000/5001 not listening yet")

        print("\n" + "="*70)
        print("Backend deployment fix complete!")
        print("\nNext step: Rebuild frontend with HTTPS API URL")
        print("  Run: python rebuild-frontend.py")
        print("="*70)

        ssh.close()

    except Exception as e:
        print(f"\nError: {e}")
        import traceback
        traceback.print_exc()
        sys.exit(1)

if __name__ == "__main__":
    main()
