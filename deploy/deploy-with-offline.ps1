#Requires -RunAsAdministrator

Write-Host "Deploying via app_offline.htm method..." -ForegroundColor Green
Write-Host ""

# Create app_offline.htm
Write-Host "[1/5] Creating app_offline.htm..." -ForegroundColor Yellow
@'
<!DOCTYPE html>
<html>
<head><title>Updating...</title></head>
<body style="font-family:Arial;text-align:center;padding:50px;">
    <h1>⚙️ Update in Progress</h1>
    <p>WhatsApp Bridge API is being updated. This takes ~10 seconds.</p>
</body>
</html>
'@ | Out-File -FilePath "C:\inetpub\whatsappbridge-api\app_offline.htm" -Encoding UTF8 -Force

Write-Host "   App is now offline (files unlocked)" -ForegroundColor Green

# Wait for IIS to release locks
Write-Host "`n[2/5] Waiting for IIS to release file locks..." -ForegroundColor Yellow
Start-Sleep -Seconds 5

# Copy files
Write-Host "`n[3/5] Copying new files..." -ForegroundColor Yellow
$sourceFiles = Get-ChildItem "E:\projects\whatsappbridge\Backend\WhatsAppBridge.API\bin\publish" -Recurse
$copied = 0
$failed = 0

foreach ($file in $sourceFiles) {
    $relativePath = $file.FullName.Replace("E:\projects\whatsappbridge\Backend\WhatsAppBridge.API\bin\publish\", "")
    $targetPath = "C:\inetpub\whatsappbridge-api\$relativePath"

    try {
        if ($file.PSIsContainer) {
            New-Item -ItemType Directory -Path $targetPath -Force -ErrorAction SilentlyContinue | Out-Null
        } else {
            Copy-Item -Path $file.FullName -Destination $targetPath -Force
            $copied++
        }
    } catch {
        $failed++
    }
}

Write-Host "   Copied: $copied files" -ForegroundColor Green
if ($failed -gt 0) {
    Write-Host "   Failed: $failed files (probably still locked)" -ForegroundColor Yellow
}

# Remove app_offline.htm
Write-Host "`n[4/5] Removing app_offline.htm..." -ForegroundColor Yellow
Start-Sleep -Seconds 2
Remove-Item "C:\inetpub\whatsappbridge-api\app_offline.htm" -Force

Write-Host "   App is starting up..." -ForegroundColor Green

# Wait for app to start
Write-Host "`n[5/5] Waiting for app to start..." -ForegroundColor Yellow
Start-Sleep -Seconds 5

# Test
Write-Host "`nTesting API..." -ForegroundColor Yellow
try {
    $response = Invoke-WebRequest -Uri "http://localhost:5000/swagger" -UseBasicParsing -TimeoutSec 10
    if ($response.StatusCode -eq 200) {
        Write-Host ""
        Write-Host "================================" -ForegroundColor Green
        Write-Host "   DEPLOYMENT SUCCESS!" -ForegroundColor Green
        Write-Host "================================" -ForegroundColor Green
        Write-Host ""
        Write-Host "Backend deployed with External API support" -ForegroundColor Cyan
        Write-Host "Production URL: https://whatsapp.wreckingball.ai" -ForegroundColor Cyan
    }
} catch {
    Write-Host ""
    Write-Host "WARNING: API not responding yet. Give it 10-15 more seconds." -ForegroundColor Yellow
    Write-Host "Error: $($_.Exception.Message)" -ForegroundColor Yellow
}

Write-Host ""
