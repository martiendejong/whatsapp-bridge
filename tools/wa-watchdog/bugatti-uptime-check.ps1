# Bugatti Insights uptime check (prod + test). Runs every 10 min via scheduled task "BugattiUptimeCheck"
# on 85.215.217.154 (deployed at C:\scripts\bugatti-uptime-check.ps1 -- this repo copy is the
# version-controlled mirror for review; deploy is a byte-for-byte copy to that existing path).
#
# Task 3308: this script used to decide for itself when to alert Martien -- its own dedup/state
# file plus a direct POST to /api/wa/sendMessage. That duplicated the alert-timing logic task
# 3305 moved into the bridge (ServerMonitorService/MonitorAlertDispatcher): exactly the
# independently-alerting-watchdogs pattern that once let multiple uncoordinated senders (this
# script, morning-team-briefing.py, the bugatti-infra.md mission) hammer the bridge's outbound
# endpoint with no shared volume budget, part of what got the bridge's WhatsApp number banned
# (task 897). This script now only PROBES and self-heals -- it reports every result, "up"
# included, to the bridge's monitor statusfeed (POST /api/wa/monitor) and the bridge alone
# decides whether and when to message Martien, the same as every other reporter now does.
#
# app.bugattiinsights.com is already seeded "kritiek" in the bridge's Monitor:CriticalSubjects
# config (5 min threshold); app.test.bugattiinsights.com is left to self-register as "normaal"
# on its first report (ServerMonitorService's own self-registration default) -- no bridge
# config change needed for it.
#
# The bridge API bearer token (Prospergenics vault project 8, credential 18) is fetched fresh
# from the vault every run and never written to disk -- same fresh-token-per-run pattern as
# tools/wa-watchdog/monitor-report.ps1 (task 3305). Reuses THAT script's own already-deployed,
# narrowly-scoped (project-8-only), SYSTEM/Administrators-ACL'd vault bootstrap key
# (C:\tools\wa-watchdog\vault-key.txt) rather than provisioning a second copy of the same key
# for the same project on the same box.
#
# Test coverage added 2026-07-16 after a silent ~4h BugattiInsightsAPI-Test app-pool-stopped
# outage went undetected because this monitor previously only watched prod.
# Self-heal added 2026-07-17 after a second BugattiInsightsAPI-Test app-pool-stopped occurrence
# (~23min silent, caught by this monitor, manually restarted by Jengo): the test IIS app pool is
# still auto-restarted on detected DOWN, before reporting. Scoped to test only (not prod) since
# prod's pool topology is shared across apps (see infra_prod_apppool_collision) and a blind
# auto-restart there is riskier; test's pool serves only this one app. This self-heal block is
# unchanged by task 3308.
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$logFile = "C:\logs\bugatti-uptime.log"
$vaultKeyPath = "C:\tools\wa-watchdog\vault-key.txt"
$VaultBaseUrl = "https://vault.prospergenics.com"
$VaultProjectId = 8
$VaultCredentialId = 18
$MonitorUrl = "https://api.whatsapp.wreckingball.ai/api/wa/monitor"
$now = Get-Date

$targets = @(
    @{ key = "prod"; label = "production"; url = "https://app.bugattiinsights.com/api/version"; subject = "app.bugattiinsights.com" },
    @{ key = "test"; label = "test"; url = "https://app.test.bugattiinsights.com/api/version"; appPool = "BugattiInsightsAPI-Test"; subject = "app.test.bugattiinsights.com" }
)

function Get-BridgeToken {
    if (-not (Test-Path $vaultKeyPath)) {
        Add-Content -Path $logFile -Value "$now FATAL: vault bootstrap key missing at $vaultKeyPath - skipping report this round"
        return $null
    }
    $vaultKey = (Get-Content $vaultKeyPath -Raw).Trim()
    if ([string]::IsNullOrWhiteSpace($vaultKey)) {
        Add-Content -Path $logFile -Value "$now FATAL: vault bootstrap key file is empty - skipping report this round"
        return $null
    }
    try {
        $reason  = [uri]::EscapeDataString('bugatti-uptime-check 10-min uptime check')
        $credUrl = "$VaultBaseUrl/api/projects/$VaultProjectId/credentials/$VaultCredentialId`?reason=$reason"
        $cred    = Invoke-RestMethod -Uri $credUrl -Headers @{ 'X-API-Key' = $vaultKey } -Method Get -TimeoutSec 15
        return $cred.password
    } catch {
        Add-Content -Path $logFile -Value "$now FATAL: vault fetch for bridge token failed: $($_.Exception.Message)"
        return $null
    }
}

function Send-MonitorReport([string]$subject, [string]$status, [string]$detail, [string]$bridgeToken) {
    $payload = @{ subject = $subject; status = $status; detail = $detail } | ConvertTo-Json -Compress
    try {
        $mResp = Invoke-RestMethod -Uri $MonitorUrl -Method Post -Headers @{ Authorization = "Bearer $bridgeToken" } `
            -ContentType 'application/json' -Body $payload -TimeoutSec 20
        Add-Content -Path $logFile -Value "$now [$subject] reported status=$status detail=$detail disposition=$($mResp.disposition) tier=$($mResp.tier)"
    } catch {
        Add-Content -Path $logFile -Value "$now [$subject] REPORT FAILED: $($_.Exception.Message)"
    }
}

$bridgeToken = Get-BridgeToken

foreach ($t in $targets) {
    $status = "up"
    $detail = $null
    try {
        $resp = Invoke-WebRequest -Uri $t.url -TimeoutSec 15 -UseBasicParsing
        if ($resp.StatusCode -ne 200) {
            $status = "down"
            $detail = "HTTP $($resp.StatusCode)"
        }
    } catch {
        $status = "down"
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

    Add-Content -Path $logFile -Value "$now [$($t.key)] $status"

    if ($status -eq "down" -and $t.appPool) {
        try {
            $poolState = (Get-WebAppPoolState -Name $t.appPool -ErrorAction Stop).Value
            Add-Content -Path $logFile -Value "$now [$($t.key)] appPool '$($t.appPool)' state: $poolState"
            if ($poolState -eq "Stopped") {
                Start-WebAppPool -Name $t.appPool
                Add-Content -Path $logFile -Value "$now [$($t.key)] self-heal: started app pool '$($t.appPool)'"
                $detail = "$detail (auto-restarted app pool '$($t.appPool)', was Stopped)"
            }
        } catch {
            Add-Content -Path $logFile -Value "$now [$($t.key)] self-heal check failed: $_"
        }
    }

    if ($bridgeToken) {
        Send-MonitorReport -subject $t.subject -status $status -detail $detail -bridgeToken $bridgeToken
    }
}
