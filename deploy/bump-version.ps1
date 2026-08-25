#Requires -Version 5.1
<#
.SYNOPSIS
    Bumps the WhatsApp Bridge version and tags the release in git.
.DESCRIPTION
    Reads the repo-root VERSION file, bumps it (patch by default), writes the new version
    back to VERSION and Backend/WhatsAppBridge.API/WhatsAppBridge.API.csproj, commits both,
    and creates+pushes an annotated `vX.Y.Z` git tag at that commit.

    Run this as the first step of a release (deploy-all.ps1 does this automatically) so every
    deployed build has a version JengoAGI (or a human) can look up via `git describe` / the
    VERSION file / the running instance's GET /api/version endpoint, instead of guessing from
    build timestamps or commit counts.
.PARAMETER Part
    Which part of major.minor.patch to bump. Defaults to "patch".
#>

param(
    [ValidateSet("major", "minor", "patch")]
    [string]$Part = "patch"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$versionFile = Join-Path $repoRoot "VERSION"
$csprojFile = Join-Path $repoRoot "Backend\WhatsAppBridge.API\WhatsAppBridge.API.csproj"

$current = if (Test-Path $versionFile) { (Get-Content $versionFile -Raw).Trim() } else { "1.0.0" }
if ($current -notmatch '^(\d+)\.(\d+)\.(\d+)$') {
    throw "VERSION file contains '$current', expected MAJOR.MINOR.PATCH"
}
$major, $minor, $patch = [int]$Matches[1], [int]$Matches[2], [int]$Matches[3]

switch ($Part) {
    "major" { $major++; $minor = 0; $patch = 0 }
    "minor" { $minor++; $patch = 0 }
    "patch" { $patch++ }
}
$newVersion = "$major.$minor.$patch"

Write-Host "Bumping version: $current -> $newVersion" -ForegroundColor Cyan

Set-Content -Path $versionFile -Value $newVersion -NoNewline
Add-Content -Path $versionFile -Value ""

$csprojContent = Get-Content $csprojFile -Raw
$csprojContent = $csprojContent -replace '<Version>[^<]*</Version>', "<Version>$newVersion</Version>"
Set-Content -Path $csprojFile -Value $csprojContent -NoNewline

Push-Location $repoRoot
try {
    git add VERSION "Backend/WhatsAppBridge.API/WhatsAppBridge.API.csproj"
    git commit -m "chore: bump version to $newVersion" | Out-Null
    git tag -a "v$newVersion" -m "Release v$newVersion"

    try {
        git push
        git push origin "v$newVersion"
        Write-Host "Pushed commit and tag v$newVersion" -ForegroundColor Green
    } catch {
        Write-Warning "Could not push version bump/tag automatically (commit + local tag were still created): $_"
    }
} finally {
    Pop-Location
}

Write-Host "Version is now $newVersion" -ForegroundColor Green
