#Requires -RunAsAdministrator

Write-Host "Installing NSSM..." -ForegroundColor Cyan

# Try chocolatey first (if installed)
$chocoInstalled = Get-Command choco -ErrorAction SilentlyContinue
if ($chocoInstalled) {
    Write-Host "  Installing via Chocolatey..."
    choco install nssm -y 2>&1 | Out-Null
    if (Get-Command nssm -ErrorAction SilentlyContinue) {
        Write-Host "[OK] NSSM installed via Chocolatey" -ForegroundColor Green
        exit 0
    }
}

# Manual installation - download pre-compiled nssm.exe directly
Write-Host "  Downloading NSSM.exe directly..."
try {
    $nssmUrl = "https://nssm.cc/ci/nssm-2.24-103-gdee49fc.exe"
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $nssmUrl -OutFile "C:\Windows\System32\nssm.exe" -UseBasicParsing
    Write-Host "[OK] NSSM installed" -ForegroundColor Green
    exit 0
} catch {
    Write-Host "[WARN] Direct download failed: $_" -ForegroundColor Yellow
}

# Fallback: try alternative source
Write-Host "  Trying alternative download source..."
try {
    $altUrl = "https://github.com/martinpitt/nssm/releases/download/2.24-102-g897c7ad/nssm-2.24-102-g897c7ad.zip"
    $tempZip = "$env:TEMP\nssm.zip"
    Invoke-WebRequest -Uri $altUrl -OutFile $tempZip -UseBasicParsing
    Expand-Archive -Path $tempZip -DestinationPath "$env:TEMP\nssm" -Force
    Copy-Item "$env:TEMP\nssm\nssm*.exe" "C:\Windows\System32\nssm.exe" -Force
    Remove-Item $tempZip -Force
    Remove-Item "$env:TEMP\nssm" -Recurse -Force
    Write-Host "[OK] NSSM installed" -ForegroundColor Green
    exit 0
} catch {
    Write-Host "[FAIL] All download attempts failed: $_" -ForegroundColor Red
    exit 1
}
