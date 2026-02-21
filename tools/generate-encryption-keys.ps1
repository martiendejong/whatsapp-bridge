#!/usr/bin/env pwsh

<#
.SYNOPSIS
    Generate AES-256 encryption keys for WhatsApp Bridge
.DESCRIPTION
    Generates cryptographically secure encryption keys and IV for use with WhatsApp Bridge encryption
#>

Add-Type -AssemblyName System.Security

# Generate 32-byte key (256 bits) for AES-256
$key = New-Object byte[] 32
$rng = [System.Security.Cryptography.RNGCryptoServiceProvider]::Create()
$rng.GetBytes($key)
$keyBase64 = [Convert]::ToBase64String($key)

# Generate 16-byte IV (128 bits) for AES
$iv = New-Object byte[] 16
$rng.GetBytes($iv)
$ivBase64 = [Convert]::ToBase64String($iv)

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "WhatsApp Bridge - Encryption Keys" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Copy these values to your appsettings.Production.json:" -ForegroundColor Yellow
Write-Host ""
Write-Host "Key (32 bytes / 256 bits):" -ForegroundColor Green
Write-Host $keyBase64 -ForegroundColor White
Write-Host ""
Write-Host "IV (16 bytes / 128 bits):" -ForegroundColor Green
Write-Host $ivBase64 -ForegroundColor White
Write-Host ""
Write-Host "Example configuration:" -ForegroundColor Yellow
Write-Host @"
{
  "Encryption": {
    "Enabled": true,
    "Key": "$keyBase64",
    "IV": "$ivBase64"
  }
}
"@ -ForegroundColor White
Write-Host ""
Write-Host "IMPORTANT: Store these keys securely!" -ForegroundColor Red
Write-Host "- Do NOT commit them to version control" -ForegroundColor Yellow
Write-Host "- Keep backups in a secure location" -ForegroundColor Yellow
Write-Host "- If keys are lost, encrypted data cannot be recovered" -ForegroundColor Yellow
Write-Host ""
