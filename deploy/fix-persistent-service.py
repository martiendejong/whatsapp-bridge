#!/usr/bin/env python3
"""Fix the persistent service runner"""

import paramiko

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"

# Updated service runner that properly keeps service alive
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

    # Start process and redirect output
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $nodeExe
    $psi.Arguments = $appJs
    $psi.WorkingDirectory = $serviceDir
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true

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
    Add-Content -Path $logFile -Value "$timestamp - Service started (PID: $($process.Id))"

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
print("Fixing Persistent Service")
print("="*70)

# Upload new service runner
print("\n1. Uploading improved service-runner.ps1...")
sftp = ssh.open_sftp()
with sftp.file('/C:/Services/WhatsAppBridge/service-runner.ps1', 'w') as f:
    f.write(SERVICE_RUNNER)
sftp.close()
print("   Uploaded")

# Kill any existing processes
print("\n2. Stopping any running services...")
ssh.exec_command('powershell -Command "Get-Process node -ErrorAction SilentlyContinue | Stop-Process -Force"')
import time
time.sleep(2)

# Start the service via Task Scheduler task
print("\n3. Starting service via Task Scheduler...")
stdin, stdout, stderr = ssh.exec_command(
    'powershell -Command "Start-ScheduledTask -TaskName WhatsAppBridgeService"'
)
stdout.read()  # Wait for completion

time.sleep(5)

# Verify it's running
print("\n4. Verifying service is running...")
stdin, stdout, stderr = ssh.exec_command('powershell -Command "netstat -ano | findstr :3000"')
port_check = stdout.read().decode('utf-8', errors='ignore').strip()

if port_check:
    print("   [OK] SUCCESS - Port 3000 is listening!")

    # Test health endpoint
    print("\n5. Testing health endpoint...")
    stdin, stdout, stderr = ssh.exec_command(
        'powershell -Command "try { $r = Invoke-WebRequest -Uri http://localhost:3000/health -UseBasicParsing -TimeoutSec 5; $r.Content } catch { \\"ERROR: $($_.Exception.Message)\\" }"'
    )
    health = stdout.read().decode('utf-8', errors='ignore').strip()
    print(f"   {health}")
else:
    print("   [FAIL] FAILED - Port 3000 not listening")
    print("\n   Checking error log...")
    stdin, stdout, stderr = ssh.exec_command(
        'powershell -Command "Get-Content C:\\Services\\WhatsAppBridge\\logs\\service-error.log -Tail 20 -ErrorAction SilentlyContinue"'
    )
    errors = stdout.read().decode('utf-8', errors='ignore').strip()
    if errors:
        print(errors)

ssh.close()

print("\n" + "="*70)
print("Service should now be running persistently!")
print("Try connecting WhatsApp - QR code should appear")
print("="*70)
