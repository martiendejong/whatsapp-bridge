#Requires -RunAsAdministrator

Write-Host "Setting up WhatsApp Bridge as single site with /api application..." -ForegroundColor Green
Write-Host ""

Import-Module WebAdministration

$siteName = "WhatsAppBridge"
$frontendPath = "C:\inetpub\whatsappbridge-web"
$apiPath = "C:\inetpub\whatsappbridge-api"

# Check if main site exists
if (-not (Test-Path "IIS:\Sites\$siteName")) {
    Write-Host "ERROR: Site '$siteName' does not exist!" -ForegroundColor Red
    Write-Host "Creating main site..." -ForegroundColor Yellow

    # Create app pool for frontend
    $frontendPoolName = "${siteName}FrontendPool"
    if (-not (Test-Path "IIS:\AppPools\$frontendPoolName")) {
        New-WebAppPool -Name $frontendPoolName
        Set-ItemProperty "IIS:\AppPools\$frontendPoolName" -Name "managedRuntimeVersion" -Value ""
    }

    # Create main site
    New-WebSite -Name $siteName -PhysicalPath $frontendPath -ApplicationPool $frontendPoolName

    Write-Host "Main site created" -ForegroundColor Green
} else {
    Write-Host "[1/4] Main site exists: $siteName" -ForegroundColor Green
}

# Create API app pool if it doesn't exist
$apiPoolName = "${siteName}APIPool"
Write-Host "`n[2/4] Checking API app pool..." -ForegroundColor Yellow

if (Test-Path "IIS:\AppPools\$apiPoolName") {
    Write-Host "   API app pool exists, recycling..." -ForegroundColor Yellow
    Restart-WebAppPool -Name $apiPoolName
} else {
    Write-Host "   Creating API app pool..." -ForegroundColor Yellow
    New-WebAppPool -Name $apiPoolName
    Set-ItemProperty "IIS:\AppPools\$apiPoolName" -Name "managedRuntimeVersion" -Value ""
    Set-ItemProperty "IIS:\AppPools\$apiPoolName" -Name "enable32BitAppOnWin64" -Value $false
}

Write-Host "   API app pool ready" -ForegroundColor Green

# Remove /api application if it exists (to recreate)
Write-Host "`n[3/4] Setting up /api application..." -ForegroundColor Yellow

if (Test-Path "IIS:\Sites\$siteName\api") {
    Write-Host "   Removing existing /api application..." -ForegroundColor Yellow
    Remove-WebApplication -Site $siteName -Name "api"
}

# Create /api application
Write-Host "   Creating /api application..." -ForegroundColor Yellow
New-WebApplication -Site $siteName -Name "api" -PhysicalPath $apiPath -ApplicationPool $apiPoolName

Write-Host "   /api application created" -ForegroundColor Green

# Update CORS in production settings
Write-Host "`n[4/4] Updating CORS configuration..." -ForegroundColor Yellow

$appSettingsPath = "$apiPath\appsettings.Production.json"
if (Test-Path $appSettingsPath) {
    $appSettings = Get-Content $appSettingsPath -Raw | ConvertFrom-Json

    # Update AllowedOrigins to include the main domain
    $appSettings.AllowedOrigins = @(
        "https://whatsapp.wreckingball.ai",
        "http://localhost:9237"
    )

    $appSettings | ConvertTo-Json -Depth 10 | Out-File -FilePath $appSettingsPath -Encoding UTF8
    Write-Host "   CORS updated for single-site setup" -ForegroundColor Green
}

# Recycle API pool to pick up config changes
Write-Host "`n   Recycling API app pool..." -ForegroundColor Yellow
Restart-WebAppPool -Name $apiPoolName

Write-Host "`n================================" -ForegroundColor Green
Write-Host "   SETUP COMPLETE!" -ForegroundColor Green
Write-Host "================================" -ForegroundColor Green
Write-Host ""
Write-Host "Architecture:" -ForegroundColor Cyan
Write-Host "  Frontend: https://whatsapp.wreckingball.ai/" -ForegroundColor White
Write-Host "  API:      https://whatsapp.wreckingball.ai/api/*" -ForegroundColor White
Write-Host ""
Write-Host "Test the External API:" -ForegroundColor Cyan
Write-Host '  curl -k "https://whatsapp.wreckingball.ai/api/external/whatsapp/sessions" \' -ForegroundColor White
Write-Host '    -H "X-Api-Token: YOUR_TOKEN" \' -ForegroundColor White
Write-Host '    -H "X-Email: YOUR_EMAIL"' -ForegroundColor White
Write-Host ""
