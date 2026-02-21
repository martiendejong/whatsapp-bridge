#Requires -RunAsAdministrator

Write-Host "Step 3: Deploying Backend API..." -ForegroundColor Cyan

$apiPath = "C:\inetpub\whatsappbridge-api"

try {
    # Publish backend
    Write-Host "  Publishing ASP.NET Core API..."
    Set-Location "C:\whatsappbridge\Backend\WhatsAppBridge.API"
    dotnet publish -c Release -o $apiPath 2>&1 | Out-Null

    # Create appsettings.Production.json
    Write-Host "  Creating production config..."
    $config = @{
        ConnectionStrings = @{
            DefaultConnection = "Data Source=$apiPath\whatsappbridge.db"
        }
        Jwt = @{
            Key = "wreckingball-prod-$(New-Guid)"
            Issuer = "WhatsAppBridge"
            Audience = "WhatsAppBridgeUsers"
        }
        WhatsAppService = @{
            Url = "http://localhost:3000"
        }
        AllowedOrigins = @(
            "https://whatsapp.wreckingball.ai",
            "http://whatsapp.wreckingball.ai"
        )
        Encryption = @{
            Enabled = $false
            Key = ""
            IV = ""
        }
    }
    $config | ConvertTo-Json -Depth 10 | Out-File -FilePath "$apiPath\appsettings.Production.json" -Encoding UTF8

    # Configure IIS
    Write-Host "  Configuring IIS..."
    Import-Module WebAdministration

    $appPoolName = "WhatsAppBridgeAPIPool"
    if (Test-Path "IIS:\AppPools\$appPoolName") {
        Remove-WebAppPool -Name $appPoolName
    }
    New-WebAppPool -Name $appPoolName | Out-Null
    Set-ItemProperty "IIS:\AppPools\$appPoolName" -Name "managedRuntimeVersion" -Value ""

    $siteName = "WhatsAppBridgeAPI"
    if (Test-Path "IIS:\Sites\$siteName") {
        Remove-WebSite -Name $siteName
    }
    New-WebSite -Name $siteName -PhysicalPath $apiPath -Port 5000 -ApplicationPool $appPoolName | Out-Null

    # Set permissions
    Write-Host "  Setting permissions..."
    $acl = Get-Acl $apiPath
    $rule = New-Object System.Security.AccessControl.FileSystemAccessRule("IIS_IUSRS", "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow")
    $acl.SetAccessRule($rule)
    Set-Acl $apiPath $acl

    Write-Host "[OK] Backend API deployed on port 5000" -ForegroundColor Green
    exit 0
} catch {
    Write-Host "[FAIL] Backend deployment failed: $_" -ForegroundColor Red
    exit 1
}
