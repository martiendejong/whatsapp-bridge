#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Make a user an administrator in WhatsApp Bridge

.DESCRIPTION
    This script updates a user's IsAdmin flag in the database.
    Must be run on the server where the database is located.

.PARAMETER Email
    The email address of the user to make admin

.PARAMETER DbPath
    Path to the SQLite database file (default: ../whatsappbridge.db)

.EXAMPLE
    .\make-admin.ps1 -Email "user@example.com"

.EXAMPLE
    .\make-admin.ps1 -Email "user@example.com" -DbPath "C:\path\to\whatsappbridge.db"
#>

param(
    [Parameter(Mandatory=$true)]
    [string]$Email,

    [Parameter(Mandatory=$false)]
    [string]$DbPath = "../whatsappbridge.db"
)

# Check if SQLite is available
$sqliteCommand = Get-Command sqlite3 -ErrorAction SilentlyContinue

if (-not $sqliteCommand) {
    Write-Host "ERROR: sqlite3 command not found. Please install SQLite." -ForegroundColor Red
    Write-Host "You can download it from: https://www.sqlite.org/download.html" -ForegroundColor Yellow
    exit 1
}

# Resolve absolute path
$DbPath = Resolve-Path $DbPath -ErrorAction SilentlyContinue
if (-not $DbPath) {
    Write-Host "ERROR: Database file not found at: $DbPath" -ForegroundColor Red
    exit 1
}

Write-Host "WhatsApp Bridge - Make User Admin" -ForegroundColor Cyan
Write-Host "=================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Database: $DbPath" -ForegroundColor Gray
Write-Host "Email: $Email" -ForegroundColor Gray
Write-Host ""

# Check if user exists
$checkQuery = "SELECT Id, Email, IsAdmin FROM Users WHERE Email = '$Email';"
$userInfo = sqlite3 $DbPath $checkQuery

if (-not $userInfo) {
    Write-Host "ERROR: User with email '$Email' not found in database." -ForegroundColor Red
    exit 1
}

# Parse user info
$userId, $userEmail, $isAdmin = $userInfo -split '\|'

Write-Host "Found user:" -ForegroundColor Green
Write-Host "  ID: $userId" -ForegroundColor White
Write-Host "  Email: $userEmail" -ForegroundColor White
Write-Host "  Current Admin Status: $isAdmin" -ForegroundColor White
Write-Host ""

if ($isAdmin -eq "1") {
    Write-Host "User is already an administrator." -ForegroundColor Yellow
    exit 0
}

# Update user to admin
$updateQuery = "UPDATE Users SET IsAdmin = 1 WHERE Email = '$Email';"
sqlite3 $DbPath $updateQuery

# Verify update
$verifyQuery = "SELECT IsAdmin FROM Users WHERE Email = '$Email';"
$newStatus = sqlite3 $DbPath $verifyQuery

if ($newStatus -eq "1") {
    Write-Host "SUCCESS: User '$Email' is now an administrator!" -ForegroundColor Green
} else {
    Write-Host "ERROR: Failed to update user admin status." -ForegroundColor Red
    exit 1
}
