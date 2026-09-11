# WhatsApp Bridge monitor reporter (task 3305)
#
# Runs every ~5 minutes (scheduled task on 85.215.217.154) and reports an HTTP
# up/down check for each production domain to the bridge's own monitor statusfeed:
#
#   POST https://api.whatsapp.wreckingball.ai/api/wa/monitor
#   { "subject": "portofgiethoorn.com", "status": "down", "detail": "HTTP 502" }
#
# This reporter has NO alert logic of its own: no dedup, no thresholds, no
# outbound WhatsApp send. It only reports what it sees, "up" included, every
# round -- the bridge (ServerMonitorService/MonitorAlertDispatcher) owns every
# decision about whether a state is worth alerting Martien. Reporting "up"
# every round is not optional: without a steady stream of up-heartbeats the
# bridge's silence check (a monitor that stops reporting entirely, distinct
# from a monitor that reports "down") can never fire correctly.
#
# Replaces wa-uptime-monitor.ps1 (referenced on `main`, commit bf83992) --
# that script is confirmed to have never actually reached this repo's git
# history or this server's filesystem (checked: `git log --all` across every
# branch, and a full filesystem sweep of 85.215.217.154). There is nothing to
# delete; if it resurfaces anywhere, treat it as superseded by this script and
# remove it -- do not extend it, its dedup/threshold/ntfy logic is exactly
# what moved into the bridge itself.
#
# The bridge API bearer token (Prospergenics vault project 8, credential 18)
# is fetched fresh from the vault EVERY run and is never written to disk. The
# only secret that lives on disk here is a narrowly-scoped, read-only vault
# bootstrap key (vault-key.txt, next to this script, permissions restricted to
# Administrator/SYSTEM) that can read project 8 only -- the same bootstrap-key
# pattern already used by the bridge's own VaultConfiguration.cs
# (Vault:ApiKey in appsettings.Production.json) and by the existing watchdog's
# config.json, just scoped down to the one project this script needs.
#
# Always exits 0. A crashing/silent reporter is caught by the bridge's own
# silence check (60 min with no reports) -- that is by design, not a gap this
# script needs to cover itself.

$ErrorActionPreference = 'Continue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$root         = Split-Path -Parent $MyInvocation.MyCommand.Path
$vaultKeyPath = Join-Path $root 'vault-key.txt'
$logPath      = Join-Path $root 'monitor-report.log'

$VaultBaseUrl      = 'https://vault.prospergenics.com'
$VaultProjectId    = 8
$VaultCredentialId = 18
$MonitorUrl        = 'https://api.whatsapp.wreckingball.ai/api/wa/monitor'

# Kale hostnames -- the bridge normalizes scheme/path itself (see
# ServerMonitorService.Normalize), but there is no reason to send it anything
# but the plain hostname these were seeded with.
$Subjects = @(
    'bugattiinsights.com',
    'app.bugattiinsights.com',
    'portofgiethoorn.com',
    'artrevisionist.com'
)

function Write-MonitorLog([string]$msg) {
    $line = "$((Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss'))Z $msg"
    try { Add-Content -Path $logPath -Value $line -Encoding utf8 } catch {}
}

try {
    if (-not (Test-Path $vaultKeyPath)) {
        Write-MonitorLog "FATAL: vault bootstrap key missing at $vaultKeyPath - skipping this round"
        exit 0
    }
    $vaultKey = (Get-Content $vaultKeyPath -Raw).Trim()
    if ([string]::IsNullOrWhiteSpace($vaultKey)) {
        Write-MonitorLog "FATAL: vault bootstrap key file is empty - skipping this round"
        exit 0
    }

    # Fetch the bridge's own API bearer token fresh, every run. Never cached,
    # never written to disk.
    $bridgeToken = $null
    try {
        $reason  = [uri]::EscapeDataString('wa-monitor-report 5-min uptime check')
        $credUrl = "$VaultBaseUrl/api/projects/$VaultProjectId/credentials/$VaultCredentialId`?reason=$reason"
        $cred    = Invoke-RestMethod -Uri $credUrl -Headers @{ 'X-API-Key' = $vaultKey } -Method Get -TimeoutSec 15
        $bridgeToken = $cred.password
    } catch {
        Write-MonitorLog "FATAL: vault fetch for bridge token failed: $($_.Exception.Message)"
    }
    if ([string]::IsNullOrWhiteSpace($bridgeToken)) {
        Write-MonitorLog "FATAL: no bridge token resolved from vault - skipping this round"
        exit 0
    }

    foreach ($subject in $Subjects) {
        $status = 'up'
        $detail = $null
        try {
            $resp = Invoke-WebRequest -Uri "https://$subject/" -Method Get -TimeoutSec 20 -MaximumRedirection 5 -UseBasicParsing
            if ($resp.StatusCode -ne 200) {
                $status = 'down'
                $detail = "HTTP $($resp.StatusCode)"
            }
        } catch {
            $status = 'down'
            $ex = $_.Exception
            $webResp = $null
            if ($ex.PSObject.Properties.Name -contains 'Response') { $webResp = $ex.Response }
            if ($webResp) {
                try { $detail = "HTTP $([int]$webResp.StatusCode)" } catch { $detail = $ex.Message }
            } else {
                $detail = $ex.Message
            }
            if ($detail -and $detail.Length -gt 200) { $detail = $detail.Substring(0, 200) }
        }

        $payload = @{ subject = $subject; status = $status; detail = $detail } | ConvertTo-Json -Compress
        try {
            $mResp = Invoke-RestMethod -Uri $MonitorUrl -Method Post -Headers @{ Authorization = "Bearer $bridgeToken" } `
                -ContentType 'application/json' -Body $payload -TimeoutSec 20
            Write-MonitorLog "$subject status=$status detail=$detail disposition=$($mResp.disposition) tier=$($mResp.tier)"
        } catch {
            Write-MonitorLog "$subject status=$status detail=$detail POST FAILED: $($_.Exception.Message)"
        }
    }
} catch {
    Write-MonitorLog "FATAL unhandled: $($_.Exception.Message)"
}

exit 0
