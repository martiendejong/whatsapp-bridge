#Requires -RunAsAdministrator

Write-Host "Step 5: Final verification..." -ForegroundColor Cyan

try {
    # Restart IIS
    Write-Host "  Restarting IIS..."
    iisreset /noforce 2>&1 | Out-Null

    Start-Sleep -Seconds 5

    # Check WhatsApp Service
    Write-Host "`n  Checking WhatsApp Service..."
    $service = Get-Service -Name "WhatsAppBridgeService" -ErrorAction SilentlyContinue
    if ($service -and $service.Status -eq 'Running') {
        Write-Host "    [OK] WhatsApp Service: RUNNING" -ForegroundColor Green
    } else {
        Write-Host "    [FAIL] WhatsApp Service: NOT RUNNING" -ForegroundColor Red
    }

    # Check IIS sites
    Write-Host "`n  Checking IIS Sites..."
    Import-Module WebAdministration
    $apiSite = Get-Website -Name "WhatsAppBridgeAPI" -ErrorAction SilentlyContinue
    if ($apiSite -and $apiSite.State -eq 'Started') {
        Write-Host "    [OK] Backend API: RUNNING on port 5000" -ForegroundColor Green
    } else {
        Write-Host "    [FAIL] Backend API: NOT RUNNING" -ForegroundColor Red
    }

    $webSite = Get-Website -Name "WhatsAppBridgeWeb" -ErrorAction SilentlyContinue
    if ($webSite -and $webSite.State -eq 'Started') {
        Write-Host "    [OK] Frontend: RUNNING on port 80" -ForegroundColor Green
    } else {
        Write-Host "    [FAIL] Frontend: NOT RUNNING" -ForegroundColor Red
    }

    # Check listening ports
    Write-Host "`n  Checking Listening Ports..."
    $ports = netstat -an | findstr "LISTENING" | findstr ":80 :3000 :5000"
    if ($ports -match ":80 ") {
        Write-Host "    [OK] Port 80 listening" -ForegroundColor Green
    } else {
        Write-Host "    [FAIL] Port 80 not listening" -ForegroundColor Red
    }
    if ($ports -match ":3000 ") {
        Write-Host "    [OK] Port 3000 listening" -ForegroundColor Green
    } else {
        Write-Host "    [FAIL] Port 3000 not listening" -ForegroundColor Red
    }
    if ($ports -match ":5000 ") {
        Write-Host "    [OK] Port 5000 listening" -ForegroundColor Green
    } else {
        Write-Host "    [FAIL] Port 5000 not listening" -ForegroundColor Red
    }

    Write-Host "`n========================================" -ForegroundColor Cyan
    Write-Host "INSTALLATION COMPLETE!" -ForegroundColor Green
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host "`nSite: http://whatsapp.wreckingball.ai"
    Write-Host "API:  http://whatsapp.wreckingball.ai:5000/swagger"
    Write-Host "`nNext: Install SSL certificate with setup-ssl.cmd"

    exit 0
} catch {
    Write-Host "[FAIL] Verification failed: $_" -ForegroundColor Red
    exit 1
}
