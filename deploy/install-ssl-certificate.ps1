#Requires -RunAsAdministrator

<#
.SYNOPSIS
    Install Let's Encrypt SSL certificate for whatsapp.wreckingball.ai
.DESCRIPTION
    Uses win-acme to automatically obtain and install SSL certificate from Let's Encrypt
    Configures IIS bindings for HTTPS on port 443
.PARAMETER Domain
    The domain name (default: whatsapp.wreckingball.ai)
.PARAMETER Email
    Email address for Let's Encrypt notifications
#>

param(
    [string]$Domain = "whatsapp.wreckingball.ai",
    [string]$Email = "martien@wreckingball.ai"
)

$ErrorActionPreference = "Stop"

Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "SSL Certificate Installation - Let's Encrypt" -ForegroundColor Cyan
Write-Host "Domain: $Domain" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host ""

# Step 1: Download and install win-acme
Write-Host "[1/5] Downloading win-acme (Let's Encrypt client)..." -ForegroundColor Yellow

$winAcmeUrl = "https://github.com/win-acme/win-acme/releases/download/v2.2.9.1701/win-acme.v2.2.9.1701.x64.pluggable.zip"
$winAcmePath = "C:\Tools\win-acme"
$winAcmeZip = "$env:TEMP\win-acme.zip"

if (-not (Test-Path $winAcmePath)) {
    New-Item -ItemType Directory -Path $winAcmePath -Force | Out-Null
}

Write-Host "  Downloading from GitHub..."
Invoke-WebRequest -Uri $winAcmeUrl -OutFile $winAcmeZip

Write-Host "  Extracting..."
Expand-Archive -Path $winAcmeZip -DestinationPath $winAcmePath -Force
Remove-Item $winAcmeZip -Force

Write-Host "  win-acme installed at $winAcmePath" -ForegroundColor Green

# Step 2: Verify DNS is pointing to this server
Write-Host "`n[2/5] Verifying DNS configuration..." -ForegroundColor Yellow

$serverIP = (Test-Connection -ComputerName (hostname) -Count 1).IPV4Address.IPAddressToString
Write-Host "  Server IP: $serverIP"

try {
    $dnsResult = Resolve-DnsName -Name $Domain -Type A -ErrorAction Stop
    $dnsIP = $dnsResult.IPAddress
    Write-Host "  DNS resolves to: $dnsIP"

    if ($dnsIP -eq $serverIP) {
        Write-Host "  DNS verification: PASSED" -ForegroundColor Green
    } else {
        Write-Host "  WARNING: DNS points to $dnsIP but server is $serverIP" -ForegroundColor Yellow
        Write-Host "  SSL installation may fail. Wait for DNS propagation." -ForegroundColor Yellow
        $continue = Read-Host "Continue anyway? (y/n)"
        if ($continue -ne "y") {
            Write-Host "Aborted. Run this script again after DNS propagates." -ForegroundColor Red
            exit 1
        }
    }
} catch {
    Write-Host "  ERROR: Domain $Domain does not resolve" -ForegroundColor Red
    Write-Host "  Add the DNS A record first and wait for propagation" -ForegroundColor Yellow
    exit 1
}

# Step 3: Ensure port 80 is accessible for ACME challenge
Write-Host "`n[3/5] Configuring IIS for ACME challenge..." -ForegroundColor Yellow

Import-Module WebAdministration

# Ensure default site or WhatsAppBridgeWeb is running and accessible on port 80
$site = Get-Website | Where-Object { $_.Bindings.Collection.bindingInformation -like "*:80:$Domain" }
if (-not $site) {
    Write-Host "  Creating HTTP binding for ACME challenge..."
    $webSite = Get-Website -Name "WhatsAppBridgeWeb"
    if ($webSite) {
        New-WebBinding -Name "WhatsAppBridgeWeb" -Protocol http -Port 80 -HostHeader $Domain -Force
        Write-Host "  HTTP binding created" -ForegroundColor Green
    }
}

# Step 4: Run win-acme to obtain certificate
Write-Host "`n[4/5] Requesting SSL certificate from Let's Encrypt..." -ForegroundColor Yellow
Write-Host "  This will:" -ForegroundColor White
Write-Host "    - Validate domain ownership via HTTP challenge" -ForegroundColor White
Write-Host "    - Request certificate from Let's Encrypt" -ForegroundColor White
Write-Host "    - Install certificate in Windows certificate store" -ForegroundColor White
Write-Host "    - Create HTTPS binding in IIS" -ForegroundColor White
Write-Host "    - Set up automatic renewal (every 60 days)" -ForegroundColor White
Write-Host ""

Set-Location $winAcmePath

# Run win-acme in unattended mode
$arguments = @(
    "--target", "manual",
    "--host", $Domain,
    "--webroot", "C:\inetpub\whatsappbridge-web",
    "--installation", "iis",
    "--siteid", "1",
    "--emailaddress", $Email,
    "--accepttos",
    "--store", "certificatestore"
)

Write-Host "  Executing: wacs.exe $($arguments -join ' ')" -ForegroundColor Gray
& ".\wacs.exe" $arguments

if ($LASTEXITCODE -ne 0) {
    Write-Host "`n  ERROR: Certificate request failed" -ForegroundColor Red
    Write-Host "  Check that:" -ForegroundColor Yellow
    Write-Host "    - DNS is properly configured and propagated" -ForegroundColor Yellow
    Write-Host "    - Port 80 is accessible from the internet" -ForegroundColor Yellow
    Write-Host "    - No firewall is blocking Let's Encrypt servers" -ForegroundColor Yellow
    exit 1
}

Write-Host "`n  Certificate installed successfully!" -ForegroundColor Green

# Step 5: Configure HTTPS bindings for API
Write-Host "`n[5/5] Configuring HTTPS bindings..." -ForegroundColor Yellow

# Get the certificate
$cert = Get-ChildItem -Path Cert:\LocalMachine\My |
    Where-Object { $_.Subject -like "*$Domain*" } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1

if ($cert) {
    Write-Host "  Certificate found: $($cert.Thumbprint)"

    # Add HTTPS binding to web site (port 443)
    $webSite = Get-Website -Name "WhatsAppBridgeWeb"
    if ($webSite) {
        $existingBinding = Get-WebBinding -Name "WhatsAppBridgeWeb" -Protocol https -Port 443
        if (-not $existingBinding) {
            New-WebBinding -Name "WhatsAppBridgeWeb" -Protocol https -Port 443 -HostHeader $Domain -SslFlags 1
            Write-Host "  HTTPS binding created for web (port 443)" -ForegroundColor Green
        }

        # Bind certificate
        $binding = Get-WebBinding -Name "WhatsAppBridgeWeb" -Protocol https -Port 443
        $binding.AddSslCertificate($cert.Thumbprint, "my")
        Write-Host "  Certificate bound to web site" -ForegroundColor Green
    }

    # Add HTTPS binding to API (port 5001)
    $apiSite = Get-Website -Name "WhatsAppBridgeAPI"
    if ($apiSite) {
        $existingApiBinding = Get-WebBinding -Name "WhatsAppBridgeAPI" -Protocol https -Port 5001
        if (-not $existingApiBinding) {
            New-WebBinding -Name "WhatsAppBridgeAPI" -Protocol https -Port 5001 -HostHeader $Domain -SslFlags 1
            Write-Host "  HTTPS binding created for API (port 5001)" -ForegroundColor Green
        }

        # Bind certificate
        $apiBinding = Get-WebBinding -Name "WhatsAppBridgeAPI" -Protocol https -Port 5001
        $apiBinding.AddSslCertificate($cert.Thumbprint, "my")
        Write-Host "  Certificate bound to API site" -ForegroundColor Green
    }

} else {
    Write-Host "  WARNING: Certificate not found in store" -ForegroundColor Yellow
}

# Restart IIS
Write-Host "`n  Restarting IIS..."
iisreset /noforce | Out-Null

Write-Host "`n============================================================" -ForegroundColor Green
Write-Host "SSL Certificate Installation Complete!" -ForegroundColor Green
Write-Host "============================================================" -ForegroundColor Green
Write-Host ""
Write-Host "Your application is now accessible via HTTPS:" -ForegroundColor Cyan
Write-Host ""
Write-Host "  Web:     https://$Domain" -ForegroundColor White
Write-Host "  API:     https://${Domain}:5001/swagger" -ForegroundColor White
Write-Host ""
Write-Host "Certificate Details:" -ForegroundColor Cyan
Write-Host "  Issuer:  Let's Encrypt" -ForegroundColor White
Write-Host "  Valid:   90 days" -ForegroundColor White
Write-Host "  Renewal: Automatic (every 60 days)" -ForegroundColor White
Write-Host ""
Write-Host "Next Steps:" -ForegroundColor Cyan
Write-Host "  1. Test HTTPS access: https://$Domain" -ForegroundColor White
Write-Host "  2. Update frontend to use HTTPS API URL" -ForegroundColor White
Write-Host "  3. (Optional) Force HTTPS redirect in IIS" -ForegroundColor White
Write-Host ""
Write-Host "Certificate will auto-renew via Windows Task Scheduler" -ForegroundColor Yellow
Write-Host ""
