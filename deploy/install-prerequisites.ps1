#Requires -RunAsAdministrator

Write-Host "Installing WhatsApp Bridge Prerequisites..." -ForegroundColor Green

# Check if .NET 9 is installed
$dotnetVersion = dotnet --version 2>$null
if (-not $dotnetVersion -or $dotnetVersion -notmatch "^9\.") {
    Write-Host "Installing .NET 9 SDK..." -ForegroundColor Yellow
    $dotnetInstallerUrl = "https://download.visualstudio.microsoft.com/download/pr/latest/dotnet-sdk-win-x64.exe"
    $dotnetInstaller = "$env:TEMP\dotnet-sdk-installer.exe"
    Invoke-WebRequest -Uri $dotnetInstallerUrl -OutFile $dotnetInstaller
    Start-Process -FilePath $dotnetInstaller -ArgumentList "/quiet", "/norestart" -Wait
    Remove-Item $dotnetInstaller
}

# Check if Node.js is installed
$nodeVersion = node --version 2>$null
if (-not $nodeVersion) {
    Write-Host "Installing Node.js LTS..." -ForegroundColor Yellow
    $nodeInstallerUrl = "https://nodejs.org/dist/v20.11.0/node-v20.11.0-x64.msi"
    $nodeInstaller = "$env:TEMP\node-installer.msi"
    Invoke-WebRequest -Uri $nodeInstallerUrl -OutFile $nodeInstaller
    Start-Process -FilePath "msiexec.exe" -ArgumentList "/i", $nodeInstaller, "/quiet", "/norestart" -Wait
    Remove-Item $nodeInstaller
}

# Install IIS
$iisFeatures = @(
    "IIS-WebServerRole",
    "IIS-WebServer",
    "IIS-CommonHttpFeatures",
    "IIS-HttpErrors",
    "IIS-ApplicationDevelopment",
    "IIS-NetFxExtensibility45",
    "IIS-HealthAndDiagnostics",
    "IIS-HttpLogging",
    "IIS-Security",
    "IIS-RequestFiltering",
    "IIS-Performance",
    "IIS-WebServerManagementTools",
    "IIS-ManagementConsole",
    "IIS-ASPNET45"
)

Write-Host "Installing IIS features..." -ForegroundColor Yellow
foreach ($feature in $iisFeatures) {
    Enable-WindowsOptionalFeature -Online -FeatureName $feature -All -NoRestart -ErrorAction SilentlyContinue
}

# Install ASP.NET Core Hosting Bundle
Write-Host "Installing ASP.NET Core Hosting Bundle..." -ForegroundColor Yellow
$hostingBundleUrl = "https://download.visualstudio.microsoft.com/download/pr/latest/dotnet-hosting-win.exe"
$hostingBundle = "$env:TEMP\dotnet-hosting-bundle.exe"
Invoke-WebRequest -Uri $hostingBundleUrl -OutFile $hostingBundle
Start-Process -FilePath $hostingBundle -ArgumentList "/quiet", "/norestart" -Wait
Remove-Item $hostingBundle

# Install NSSM (Non-Sucking Service Manager)
Write-Host "Installing NSSM..." -ForegroundColor Yellow
$nssmUrl = "https://nssm.cc/release/nssm-2.24.zip"
$nssmZip = "$env:TEMP\nssm.zip"
$nssmExtract = "$env:TEMP\nssm"
Invoke-WebRequest -Uri $nssmUrl -OutFile $nssmZip
Expand-Archive -Path $nssmZip -DestinationPath $nssmExtract -Force
Copy-Item "$nssmExtract\nssm-2.24\win64\nssm.exe" "C:\Windows\System32\" -Force
Remove-Item $nssmZip, $nssmExtract -Recurse -Force

# Restart IIS
Write-Host "Restarting IIS..." -ForegroundColor Yellow
iisreset

Write-Host "`nPrerequisites installation complete!" -ForegroundColor Green
Write-Host "NOTE: You may need to restart the server for all changes to take effect." -ForegroundColor Yellow
