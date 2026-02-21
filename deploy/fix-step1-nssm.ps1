#Requires -RunAsAdministrator

Write-Host "Step 1: Installing NSSM..." -ForegroundColor Cyan

# Download NSSM from GitHub (alternative to nssm.cc)
$nssmUrl = "https://github.com/kirillkovalenko/nssm/releases/download/2.24-101-g897c7ad/nssm-2.24-101-g897c7ad.zip"
$tempZip = "$env:TEMP\nssm.zip"
$tempDir = "$env:TEMP\nssm"

try {
    Write-Host "  Downloading NSSM from GitHub..."
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $nssmUrl -OutFile $tempZip -UseBasicParsing

    Write-Host "  Extracting..."
    Expand-Archive -Path $tempZip -DestinationPath $tempDir -Force

    Write-Host "  Installing to System32..."
    Copy-Item "$tempDir\nssm-2.24-101-g897c7ad\win64\nssm.exe" "C:\Windows\System32\" -Force

    Write-Host "  Cleanup..."
    Remove-Item $tempZip -Force
    Remove-Item $tempDir -Recurse -Force

    Write-Host "[OK] NSSM installed successfully" -ForegroundColor Green
    exit 0
} catch {
    Write-Host "[FAIL] NSSM installation failed: $_" -ForegroundColor Red
    exit 1
}
