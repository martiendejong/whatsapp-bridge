#!/usr/bin/env python3
"""Setup WhatsApp Service as persistent background process"""

import paramiko

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"

# Create a PowerShell script that runs the service and restarts it if it crashes
PERSISTENT_SERVICE_SCRIPT = r"""
# WhatsApp Service Persistent Runner
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
    Write-Host "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') - Starting WhatsApp Service..."

    $process = Start-Process -FilePath $nodeExe `
        -ArgumentList $appJs `
        -WorkingDirectory $serviceDir `
        -RedirectStandardOutput $logFile `
        -RedirectStandardError $errorLog `
        -WindowStyle Hidden `
        -PassThru

    # Save PID
    $process.Id | Out-File -FilePath $pidFile

    Write-Host "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') - Service started (PID: $($process.Id))"

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
    $running = Get-Process -Id $process.Id -ErrorAction SilentlyContinue

    if (-not $running) {
        Write-Host "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') - Service crashed! Restarting..."

        # Log crash info
        "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') - CRASH DETECTED" | Out-File -FilePath $errorLog -Append

        $restartCount++

        if ($restartCount -gt $maxRestarts) {
            Write-Host "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') - Max restarts reached. Stopping."
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

STARTUP_SCRIPT = r"""
# Startup script for WhatsApp Service
param([switch]$Background)

$serviceDir = "C:\Services\WhatsAppBridge"
$runnerScript = "$serviceDir\service-runner.ps1"

if ($Background) {
    # Start in background
    Start-Process powershell -ArgumentList "-ExecutionPolicy Bypass -File `"$runnerScript`"" -WindowStyle Hidden
    Write-Host "WhatsApp Service started in background"
} else {
    # Run in foreground (for testing)
    & $runnerScript
}
"""

INSTALL_SCRIPT = r"""
# Install WhatsApp Service components
$serviceDir = "C:\Services\WhatsAppBridge"

Write-Host "Setting up WhatsApp Service..."

# Install npm dependencies if needed
Push-Location $serviceDir

if (-not (Test-Path "node_modules\whatsapp-web.js")) {
    Write-Host "Installing npm packages..."
    npm install --production
}

# Install Chromium
Write-Host "Installing Chromium..."
npx puppeteer browsers install chrome

Pop-Location

# Create Task Scheduler task for auto-start
Write-Host "Creating startup task..."

$taskName = "WhatsAppBridgeService"
$existingTask = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue

if ($existingTask) {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
}

$action = New-ScheduledTaskAction -Execute "powershell.exe" `
    -Argument "-ExecutionPolicy Bypass -File `"$serviceDir\start-service.ps1`" -Background"

$trigger = New-ScheduledTaskTrigger -AtStartup

$principal = New-ScheduledTaskPrincipal -UserId "SYSTEM" -LogonType ServiceAccount -RunLevel Highest

$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable

Register-ScheduledTask -TaskName $taskName `
    -Action $action `
    -Trigger $trigger `
    -Principal $principal `
    -Settings $settings `
    -Description "WhatsApp Bridge Service - Auto-start and monitor"

Write-Host "Startup task created"

# Start service now
Write-Host "Starting service..."
& "$serviceDir\start-service.ps1" -Background

Start-Sleep -Seconds 5

# Verify
$listening = netstat -ano | findstr ":3000" | findstr "LISTENING"
if ($listening) {
    Write-Host ""
    Write-Host "SUCCESS! WhatsApp Service is running" -ForegroundColor Green
    Write-Host "  Port 3000: LISTENING"
} else {
    Write-Host ""
    Write-Host "WARNING: Service may not be running correctly" -ForegroundColor Yellow
    Write-Host "Check logs at: $serviceDir\logs\"
}
"""

ssh = paramiko.SSHClient()
ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD)

print("="*70)
print("Setting up Persistent WhatsApp Service")
print("="*70)
print()

sftp = ssh.open_sftp()

print("1. Uploading service scripts...")
service_dir = "/C:/Services/WhatsAppBridge"

# Upload runner script
with sftp.file(f"{service_dir}/service-runner.ps1", 'w') as f:
    f.write(PERSISTENT_SERVICE_SCRIPT)
print("   - service-runner.ps1")

# Upload startup script
with sftp.file(f"{service_dir}/start-service.ps1", 'w') as f:
    f.write(STARTUP_SCRIPT)
print("   - start-service.ps1")

sftp.close()

print("\n2. Running installation...")
stdin, stdout, stderr = ssh.exec_command(
    f'powershell -ExecutionPolicy Bypass -Command "{INSTALL_SCRIPT}"'
)

for line in stdout:
    line = line.strip()
    if line:
        print(f"   {line}")

print("\n3. Verifying service...")
stdin, stdout, stderr = ssh.exec_command(
    'powershell -Command "netstat -ano | findstr \\":3000\" | findstr \"LISTENING\""'
)

output = stdout.read().decode('utf-8', errors='ignore').strip()

ssh.close()

print()
print("="*70)
if output:
    print("SUCCESS! WhatsApp Service is running")
    print("="*70)
    print()
    print("Features:")
    print("  - Auto-start on server reboot (Task Scheduler)")
    print("  - Auto-restart if crashed")
    print("  - Logs: C:\\Services\\WhatsAppBridge\\logs\\")
    print()
    print("Try connecting WhatsApp now - QR code should appear!")
else:
    print("Service setup complete (check logs if not working)")
    print("="*70)

print()
