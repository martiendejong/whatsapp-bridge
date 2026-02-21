#Requires -Version 5.1
<#
.SYNOPSIS
    Complete WhatsApp Bridge deployment to remote server
.DESCRIPTION
    Deploys WhatsApp Bridge by copying files to C:\whatsappbridge on the remote server
    and running the installation script
#>

param(
    [string]$RemoteHost = "85.215.217.154",
    [string]$Username = "administrator",
    [string]$Password = "3WsXcFr`$7YhNmKi*",
    [string]$Domain = "whatsapp.wreckingball.ai"
)

$ErrorActionPreference = "Stop"

Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "WhatsApp Bridge - Remote Deployment" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host ""

$ProjectRoot = Split-Path -Parent $PSScriptRoot

try {
    Write-Host "[1/5] Connecting to remote server..." -ForegroundColor Yellow

    # Create PSCredential
    $SecurePassword = ConvertTo-SecureString $Password -AsPlainText -Force
    $Credential = New-Object System.Management.Automation.PSCredential($Username, $SecurePassword)

    # Create PSSession
    $Session = New-PSSession -ComputerName $RemoteHost -Credential $Credential -ErrorAction SilentlyContinue

    if (-not $Session) {
        Write-Host "  PowerShell Remoting not available, using file copy fallback..." -ForegroundColor Yellow

        Write-Host "`n[2/5] Creating archive locally..." -ForegroundColor Yellow
        $ArchivePath = Join-Path $env:TEMP "whatsappbridge.zip"
        if (Test-Path $ArchivePath) {
            Remove-Item $ArchivePath -Force
        }

        # Create zip file
        Add-Type -Assembly System.IO.Compression.FileSystem
        [System.IO.Compression.ZipFile]::CreateFromDirectory($ProjectRoot, $ArchivePath)

        Write-Host "  Archive created: $((Get-Item $ArchivePath).Length / 1MB) MB" -ForegroundColor Green

        Write-Host "`n[3/5] Instructions for manual deployment:" -ForegroundColor Yellow
        Write-Host "  1. Copy $ArchivePath to the server" -ForegroundColor White
        Write-Host "  2. Extract to C:\whatsappbridge" -ForegroundColor White
        Write-Host "  3. Run: powershell -ExecutionPolicy Bypass -File C:\whatsappbridge\deploy\INSTALL_ON_SERVER.ps1 -Domain $Domain" -ForegroundColor White

    } else {
        Write-Host "  Connected to $RemoteHost" -ForegroundColor Green

        Write-Host "`n[2/5] Copying project files..." -ForegroundColor Yellow

        # Create remote directory
        Invoke-Command -Session $Session -ScriptBlock {
            New-Item -ItemType Directory -Path "C:\whatsappbridge" -Force | Out-Null
        }

        # Copy files using PSSession
        Copy-Item -Path "$ProjectRoot\*" -Destination "C:\whatsappbridge" -ToSession $Session -Recurse -Force

        Write-Host "  Files copied successfully" -ForegroundColor Green

        Write-Host "`n[3/5] Running installation script..." -ForegroundColor Yellow

        $Result = Invoke-Command -Session $Session -ScriptBlock {
            param($Domain)
            & "C:\whatsappbridge\deploy\INSTALL_ON_SERVER.ps1" -Domain $Domain
        } -ArgumentList $Domain

        Write-Host $Result -ForegroundColor White

        Remove-PSSession $Session
    }

    Write-Host "`n[4/5] DNS Configuration Required:" -ForegroundColor Yellow
    Write-Host "============================================================" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "Add this A record to wreckingball.ai DNS:" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "  Type:  A" -ForegroundColor White
    Write-Host "  Name:  whatsapp" -ForegroundColor White
    Write-Host "  Value: $RemoteHost" -ForegroundColor White
    Write-Host "  TTL:   3600" -ForegroundColor White
    Write-Host ""
    Write-Host "============================================================" -ForegroundColor Cyan

    Write-Host "`n[5/5] After DNS propagation (5-30 minutes):" -ForegroundColor Yellow
    Write-Host "  Website: http://$Domain" -ForegroundColor White
    Write-Host "  API:     http://${Domain}:5000/swagger" -ForegroundColor White
    Write-Host ""

} catch {
    Write-Host "`nError: $_" -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
}
