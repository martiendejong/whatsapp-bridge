#Requires -RunAsAdministrator

Write-Host "Step 2: Installing WhatsApp Service..." -ForegroundColor Cyan

$servicePath = "C:\Services\WhatsAppBridge"

try {
    # Create service directory
    Write-Host "  Creating service directory..."
    New-Item -ItemType Directory -Path $servicePath -Force | Out-Null

    # Copy service files
    Write-Host "  Copying service files..."
    Copy-Item "C:\whatsappbridge\WhatsAppService\*" -Destination $servicePath -Recurse -Force

    # Install Node.js dependencies
    Write-Host "  Installing npm packages..."
    Set-Location $servicePath
    npm install --production 2>&1 | Out-Null

    # Remove existing service if present
    $existingService = Get-Service -Name "WhatsAppBridgeService" -ErrorAction SilentlyContinue
    if ($existingService) {
        Write-Host "  Removing existing service..."
        nssm stop WhatsAppBridgeService 2>&1 | Out-Null
        nssm remove WhatsAppBridgeService confirm 2>&1 | Out-Null
    }

    # Install new service
    Write-Host "  Installing Windows service..."
    $nodePath = (Get-Command node).Source
    nssm install WhatsAppBridgeService $nodePath "$servicePath\index.js" | Out-Null
    nssm set WhatsAppBridgeService AppDirectory $servicePath | Out-Null
    nssm set WhatsAppBridgeService AppEnvironmentExtra "PORT=3000" | Out-Null

    # Start service
    Write-Host "  Starting service..."
    nssm start WhatsAppBridgeService | Out-Null

    Start-Sleep -Seconds 3

    # Verify
    $service = Get-Service -Name "WhatsAppBridgeService" -ErrorAction SilentlyContinue
    if ($service -and $service.Status -eq 'Running') {
        Write-Host "[OK] WhatsApp Service running on port 3000" -ForegroundColor Green
        exit 0
    } else {
        Write-Host "[FAIL] Service not running" -ForegroundColor Red
        exit 1
    }
} catch {
    Write-Host "[FAIL] Service installation failed: $_" -ForegroundColor Red
    exit 1
}
