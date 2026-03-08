#!/usr/bin/env python3
"""Configure Puppeteer to use Administrator's cache directory"""

import paramiko

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"

# Updated service runner with PUPPETEER_CACHE_DIR set
SERVICE_RUNNER = r"""# WhatsApp Service Persistent Runner
$serviceDir = "C:\Services\WhatsAppBridge"
$nodeExe = "C:\Program Files\nodejs\node.exe"
$appJs = "index.js"
$logFile = "$serviceDir\logs\service.log"
$errorLog = "$serviceDir\logs\service-error.log"
$pidFile = "$serviceDir\service.pid"

# Create logs directory
New-Item -ItemType Directory -Path "$serviceDir\logs" -Force | Out-Null

# Function to start service
function Start-WhatsAppService {
    $timestamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    Add-Content -Path $logFile -Value "$timestamp - Starting WhatsApp Service..."

    # Start process with custom Puppeteer cache directory
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $nodeExe
    $psi.Arguments = $appJs
    $psi.WorkingDirectory = $serviceDir
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true

    # IMPORTANT: Set Puppeteer cache to Administrator's directory
    $psi.EnvironmentVariables["PUPPETEER_CACHE_DIR"] = "C:\Users\Administrator\.cache\puppeteer"

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $psi

    # Event handlers for output
    $outputHandler = {
        if ($EventArgs.Data) {
            Add-Content -Path $using:logFile -Value $EventArgs.Data
        }
    }

    $errorHandler = {
        if ($EventArgs.Data) {
            Add-Content -Path $using:errorLog -Value $EventArgs.Data
        }
    }

    Register-ObjectEvent -InputObject $process -EventName OutputDataReceived -Action $outputHandler | Out-Null
    Register-ObjectEvent -InputObject $process -EventName ErrorDataReceived -Action $errorHandler | Out-Null

    $process.Start() | Out-Null
    $process.BeginOutputReadLine()
    $process.BeginErrorReadLine()

    # Save PID
    $process.Id | Out-File -FilePath $pidFile

    $timestamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    Add-Content -Path $logFile -Value "$timestamp - Service started (PID: $($process.Id)) with PUPPETEER_CACHE_DIR=C:\Users\Administrator\.cache\puppeteer"

    return $process
}

# Kill any existing processes
Get-Process node -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "*nodejs*" } | Stop-Process -Force

# Start service
$process = Start-WhatsAppService

# Monitor and restart if crashed
$restartCount = 0
$maxRestarts = 10

while ($true) {
    Start-Sleep -Seconds 10

    # Check if process is still running
    if ($process.HasExited) {
        $timestamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
        Add-Content -Path $errorLog -Value "$timestamp - Service crashed with exit code $($process.ExitCode)!"
        Add-Content -Path $logFile -Value "$timestamp - Service crashed! Restarting..."

        $restartCount++

        if ($restartCount -gt $maxRestarts) {
            $timestamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
            Add-Content -Path $errorLog -Value "$timestamp - Max restarts reached. Stopping."
            break
        }

        # Wait before restart
        Start-Sleep -Seconds 5

        # Restart
        $process = Start-WhatsAppService
        $restartCount = 0  # Reset on successful start
    }
}
"""

ssh = paramiko.SSHClient()
ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD)

print("="*70)
print("Fixing Puppeteer Cache Path")
print("="*70)

# Upload updated service runner
print("\n1. Uploading updated service-runner.ps1...")
sftp = ssh.open_sftp()
with sftp.file('/C:/Services/WhatsAppBridge/service-runner.ps1', 'w') as f:
    f.write(SERVICE_RUNNER)
sftp.close()
print("   Uploaded")

# Stop current service
print("\n2. Stopping current service...")
ssh.exec_command('powershell -Command "Get-Process node -ErrorAction SilentlyContinue | Stop-Process -Force"')
import time
time.sleep(2)

# Restart via scheduled task
print("\n3. Starting service with new configuration...")
ssh.exec_command('powershell -Command "Start-ScheduledTask -TaskName WhatsAppBridgeService"')
time.sleep(5)

# Verify
print("\n4. Verifying service...")
stdin, stdout, stderr = ssh.exec_command('powershell -Command "netstat -ano | findstr :3000"')
port = stdout.read().decode('utf-8', errors='ignore').strip()

if port:
    print("   Service running on port 3000")

    # Check logs
    print("\n5. Checking service log...")
    stdin, stdout, stderr = ssh.exec_command(
        'powershell -Command "Get-Content C:\\Services\\WhatsAppBridge\\logs\\service.log -Tail 10 -ErrorAction SilentlyContinue"'
    )
    logs = stdout.read().decode('utf-8', errors='ignore').strip()
    print(logs)
else:
    print("   WARNING: Service not running")

ssh.close()

print("\n" + "="*70)
print("Service updated to use Administrator's Chromium cache!")
print("PUPPETEER_CACHE_DIR = C:\\Users\\Administrator\\.cache\\puppeteer")
print("="*70)
