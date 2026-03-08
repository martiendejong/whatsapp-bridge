#!/usr/bin/env python3
"""Fix backend API deployment - upload and execute script"""

import paramiko
import sys

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"

# PowerShell script to create IIS API site
SETUP_SCRIPT = r"""# WhatsApp Bridge - Fix Backend API Deployment
Write-Host "Creating IIS site for WhatsApp Bridge API..."

$siteName = "WhatsAppBridgeAPI"
$appPool = "WhatsAppBridgeAPIPool"
$physicalPath = "C:\inetpub\whatsappbridge-api"
$port = 5000

# Create site if not exists
$site = Get-Website -Name $siteName -ErrorAction SilentlyContinue
if (-not $site) {
    New-Website -Name $siteName -ApplicationPool $appPool -PhysicalPath $physicalPath -Port $port -Force
    Write-Host "Created IIS site: $siteName on port $port"
} else {
    Write-Host "Site already exists: $siteName"
}

# Ensure app pool is configured correctly
$pool = Get-IISAppPool -Name $appPool
$pool.ManagedRuntimeVersion = ""
$pool.StartMode = "AlwaysRunning"
$pool | Set-IISAppPool
Write-Host "Configured app pool: $appPool"

# Start the site
Start-Website -Name $siteName -ErrorAction SilentlyContinue
Write-Host "Started website: $siteName"

# Add HTTPS binding (port 5001)
$httpsBinding = Get-WebBinding -Name $siteName -Protocol "https" -Port 5001 -ErrorAction SilentlyContinue
if (-not $httpsBinding) {
    $cert = Get-ChildItem -Path Cert:\LocalMachine\WebHosting | Where-Object { $_.Subject -like "*whatsapp.wreckingball.ai*" } | Select-Object -First 1
    if ($cert) {
        New-WebBinding -Name $siteName -Protocol "https" -Port 5001 -HostHeader "" -SslFlags 0
        $binding = Get-WebBinding -Name $siteName -Protocol "https" -Port 5001
        $binding.AddSslCertificate($cert.Thumbprint, "WebHosting")
        Write-Host "Added HTTPS binding on port 5001 with SSL certificate"
    } else {
        Write-Host "WARNING: No SSL certificate found"
    }
} else {
    Write-Host "HTTPS binding already exists on port 5001"
}

# Create web.config if missing
$webConfigPath = Join-Path $physicalPath "web.config"
if (-not (Test-Path $webConfigPath)) {
    @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <location path="." inheritInChildApplications="false">
    <system.webServer>
      <handlers>
        <add name="aspNetCore" path="*" verb="*" modules="AspNetCoreModuleV2" resourceType="Unspecified" />
      </handlers>
      <aspNetCore processPath="dotnet" arguments=".\WhatsAppBridge.API.dll" stdoutLogEnabled="true" stdoutLogFile=".\logs\stdout" hostingModel="inprocess" />
    </system.webServer>
  </location>
</configuration>
"@ | Out-File -FilePath $webConfigPath -Encoding UTF8
    Write-Host "Created web.config"
}

Write-Host ""
Write-Host "Backend API deployment completed!"
Write-Host "Testing API endpoint..."

Start-Sleep -Seconds 3

try {
    $response = Invoke-WebRequest -Uri "http://localhost:5000/swagger/index.html" -UseBasicParsing -TimeoutSec 5 -ErrorAction Stop
    Write-Host "SUCCESS: API is responding on port 5000!"
} catch {
    Write-Host "WARNING: API not responding yet. Check IIS logs."
}
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

        # Upload script file
        remote_script = "C:\\whatsappbridge\\fix-backend.ps1"
        print(f"Uploading PowerShell script to {remote_script}...")

        sftp = ssh.open_sftp()
        with sftp.file(remote_script, 'w') as f:
            f.write(SETUP_SCRIPT)
        sftp.close()

        print("Script uploaded successfully\n")

        # Execute script
        print("Executing IIS configuration script...")
        cmd = f'powershell -ExecutionPolicy Bypass -File "{remote_script}"'
        stdin, stdout, stderr = ssh.exec_command(cmd)

        # Read output in real-time
        for line in stdout:
            line = line.strip()
            if line:
                print(f"  {line}")

        errors = stderr.read().decode('utf-8', errors='ignore')
        if errors and "error" in errors.lower():
            print(f"\nErrors detected:\n{errors}")

        # Verify ports
        print("\n" + "-"*70)
        print("Verifying API is listening...")
        stdin, stdout, stderr = ssh.exec_command('netstat -ano | findstr "LISTENING" | findstr ":5000"')
        output = stdout.read().decode('utf-8', errors='ignore').strip()

        if output:
            print("Port 5000 is LISTENING:")
            for line in output.split('\n')[:3]:
                if line.strip():
                    print(f"  {line.strip()}")
            print("\n✓ Backend API is running!")
        else:
            print("  WARNING: Port 5000 not listening")
            print("  Check: Get-Website | Where-Object { $_.Name -eq 'WhatsAppBridgeAPI' }")

        print("\n" + "="*70)
        print("Backend deployment completed!")
        print("\nAPI should now be accessible at:")
        print("  HTTP:  http://whatsapp.wreckingball.ai:5000/swagger")
        print("  HTTPS: https://whatsapp.wreckingball.ai:5001/swagger")
        print("\nNext: Rebuild frontend with correct API URL")
        print("="*70)

        ssh.close()

    except Exception as e:
        print(f"\nError: {e}")
        import traceback
        traceback.print_exc()
        sys.exit(1)

if __name__ == "__main__":
    main()
