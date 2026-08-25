#Requires -Version 5.1
<#
.SYNOPSIS
    Remote-side helper invoked by deploy/deploy.py over SSH. Not a standalone deploy tool —
    deploy.py uploads this file and calls it once per phase (Backup / SwapBackend /
    SwapFrontend / Rollback / Prune). Never run manually except to debug a failed deploy;
    deploy/deploy.py is the only supported entry point (see AGENTS.md).
.DESCRIPTION
    SwapBackend/SwapFrontend assume deploy.py already uploaded the new files into a temp
    folder and verified every file's size — this script only ever moves already-verified,
    already-complete files into the live folder (never partial/in-flight ones).
#>
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("Backup", "SwapBackend", "SwapFrontend", "Rollback", "Prune")]
    [string]$Action,

    [string]$BackupDir,
    [string]$BackendLiveDir = "C:\inetpub\whatsappbridge-api",
    [string]$FrontendLiveDir = "C:\inetpub\whatsappbridge-web",
    [string]$BackendTempDir,
    [string]$FrontendAssetsTempDir,
    [string]$FrontendIndexTempFile,
    [string]$BackupRoot = "C:\inetpub\_deploy_backups",
    [int]$KeepBackups = 5,
    # Server-only files a deploy must never overwrite or delete. Keep in sync with
    # deploy.py's BACKEND_CONFIG_EXCLUDE.
    [string[]]$ExcludeNames = @(
        "appsettings.Production.json",
        "appsettings.Secrets.json",
        "whatsappbridge.db",
        "whatsappbridge.db-shm",
        "whatsappbridge.db-wal"
    )
)

$ErrorActionPreference = "Stop"

function Move-TreeInto {
    param([string]$SourceDir, [string]$DestDir)
    Get-ChildItem $SourceDir -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($SourceDir.Length).TrimStart('\', '/')
        $dest = Join-Path $DestDir $rel
        New-Item -ItemType Directory -Force -Path (Split-Path $dest) | Out-Null
        Move-Item -Path $_.FullName -Destination $dest -Force
    }
}

function Invoke-Backup {
    if (-not $BackupDir) { throw "Backup requires -BackupDir" }
    New-Item -ItemType Directory -Force -Path "$BackupDir\backend" | Out-Null
    New-Item -ItemType Directory -Force -Path "$BackupDir\frontend" | Out-Null

    if (Test-Path $BackendLiveDir) {
        robocopy $BackendLiveDir "$BackupDir\backend" /E /R:2 /W:2 /XF $ExcludeNames | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "robocopy backend backup failed with code $LASTEXITCODE" }
    }
    if (Test-Path $FrontendLiveDir) {
        robocopy $FrontendLiveDir "$BackupDir\frontend" /E /R:2 /W:2 | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "robocopy frontend backup failed with code $LASTEXITCODE" }
    }
    Write-Host "BACKUP_OK:$BackupDir"
}

function Invoke-SwapBackend {
    if (-not $BackendTempDir) { throw "SwapBackend requires -BackendTempDir" }
    New-Item -ItemType Directory -Force -Path $BackendLiveDir | Out-Null

    # ASP.NET Core Module (IIS) watches for app_offline.htm and stops the dotnet process,
    # releasing its DLL file locks — this is what makes a full-set replace possible at all.
    New-Item -ItemType File -Force -Path "$BackendLiveDir\app_offline.htm" -Value "offline" | Out-Null
    Start-Sleep -Seconds 5

    Get-ChildItem $BackendLiveDir -File | Where-Object {
        ($ExcludeNames -notcontains $_.Name) -and ($_.Name -ne "app_offline.htm")
    } | Remove-Item -Force
    Get-ChildItem $BackendLiveDir -Directory | Remove-Item -Recurse -Force

    Move-TreeInto -SourceDir $BackendTempDir -DestDir $BackendLiveDir

    Remove-Item "$BackendLiveDir\app_offline.htm" -Force -ErrorAction SilentlyContinue
    Remove-Item $BackendTempDir -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "SWAP_BACKEND_OK"
}

function Invoke-SwapFrontend {
    New-Item -ItemType Directory -Force -Path $FrontendLiveDir | Out-Null

    # Assets first: the OLD index.html (still live) only ever references OLD asset
    # filenames, which still exist until this move completes — no 404s mid-deploy.
    if ($FrontendAssetsTempDir -and (Test-Path $FrontendAssetsTempDir)) {
        Move-TreeInto -SourceDir $FrontendAssetsTempDir -DestDir $FrontendLiveDir
        Remove-Item $FrontendAssetsTempDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    # index.html last — a single move, so the switch to the new asset hashes is instant
    # and no request can ever see an index.html referencing not-yet-uploaded assets.
    if ($FrontendIndexTempFile -and (Test-Path $FrontendIndexTempFile)) {
        Move-Item -Path $FrontendIndexTempFile -Destination (Join-Path $FrontendLiveDir "index.html") -Force
        Remove-Item (Split-Path $FrontendIndexTempFile) -Recurse -Force -ErrorAction SilentlyContinue
    }
    Write-Host "SWAP_FRONTEND_OK"
}

function Invoke-Rollback {
    if (-not $BackupDir) { throw "Rollback requires -BackupDir" }
    if (-not (Test-Path $BackupDir)) { throw "Backup dir not found: $BackupDir" }

    if (Test-Path "$BackupDir\backend") {
        New-Item -ItemType Directory -Force -Path $BackendLiveDir | Out-Null
        New-Item -ItemType File -Force -Path "$BackendLiveDir\app_offline.htm" -Value "offline" | Out-Null
        Start-Sleep -Seconds 5
        Get-ChildItem $BackendLiveDir -File -ErrorAction SilentlyContinue |
            Where-Object { ($ExcludeNames -notcontains $_.Name) -and ($_.Name -ne "app_offline.htm") } |
            Remove-Item -Force
        Get-ChildItem $BackendLiveDir -Directory -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
        # /XF keeps the live server-only files (e.g. whatsappbridge.db) untouched — a rollback
        # must never overwrite them with the pre-deploy snapshot, or it silently loses any
        # message data received during the deploy window.
        robocopy "$BackupDir\backend" $BackendLiveDir /E /R:2 /W:2 /XF $ExcludeNames | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "robocopy backend restore failed with code $LASTEXITCODE" }
        Remove-Item "$BackendLiveDir\app_offline.htm" -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path "$BackupDir\frontend") {
        robocopy "$BackupDir\frontend" $FrontendLiveDir /E /R:2 /W:2 /PURGE | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "robocopy frontend restore failed with code $LASTEXITCODE" }
    }
    Write-Host "ROLLBACK_OK"
}

function Invoke-Prune {
    if (-not (Test-Path $BackupRoot)) { return }
    Get-ChildItem $BackupRoot -Directory | Sort-Object Name -Descending | Select-Object -Skip $KeepBackups |
        ForEach-Object { Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
    Write-Host "PRUNE_OK"
}

switch ($Action) {
    "Backup" { Invoke-Backup }
    "SwapBackend" { Invoke-SwapBackend }
    "SwapFrontend" { Invoke-SwapFrontend }
    "Rollback" { Invoke-Rollback }
    "Prune" { Invoke-Prune }
}
