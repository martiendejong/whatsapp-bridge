# wa-uptime-monitor.ps1
# WA Bridge uptime monitor · runs every 5-15 min as scheduled task on 85.215.217.154
#
# Checks 4 endpoints:
#   1. api.whatsapp.wreckingball.ai  → GET /api/version  (anonymous, expect 200)
#   2. whatsapp.wreckingball.ai      → GET /             (HTML, expect 200)
#   3. localhost:8199                → GET /             (basic-auth, vault 6/81)
#   4. mailbridge.wreckingball.ai    → GET /             (expect 200 or 401; try HTTPS then HTTP)
#
# On failure: ntfy alert to jengo-wa-watchdog-856-test-9dc712ae56a3
# On recovery (was down, now up): ntfy "recovered" alert
# Dedup: never re-alert for same domain within 24h (wa-uptime-dedup.json in same dir)
#
# Never exits non-zero — watchdog must not crash.

$ErrorActionPreference = 'Continue'

$scriptDir   = Split-Path -Parent $MyInvocation.MyCommand.Path
$dedupFile   = Join-Path $scriptDir 'wa-uptime-dedup.json'
$logFile     = Join-Path $scriptDir 'wa-uptime-monitor.log'
$ntfyTopic   = 'jengo-wa-watchdog-856-test-9dc712ae56a3'
$ntfyBase    = 'https://ntfy.sh'

# ── Vault script path — same machine, E:\projects\jengo ─────────────────────
$vaultScript = 'E:\projects\jengo\jengo_vault.py'

# ── Logging ──────────────────────────────────────────────────────────────────
function Write-Log($msg) {
    $line = '{0} {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $msg
    try { Add-Content -Path $logFile -Value $line -ErrorAction SilentlyContinue } catch {}
    Write-Host $line
}

# Rotate log at 2 MB
try {
    if ((Test-Path $logFile) -and ((Get-Item $logFile).Length -gt 2MB)) {
        Move-Item $logFile "$logFile.1" -Force
    }
} catch {}

# ── Dedup state ───────────────────────────────────────────────────────────────
# Schema: { "<domainKey>": { "lastAlerted": "<ISO8601>", "down": true|false } }

function Load-Dedup {
    if (Test-Path $dedupFile) {
        try {
            $raw = Get-Content $dedupFile -Raw -ErrorAction Stop
            return $raw | ConvertFrom-Json
        } catch {
            Write-Log "DEDUP: could not read $dedupFile, starting fresh: $_"
        }
    }
    return [PSCustomObject]@{}
}

function Save-Dedup($state) {
    try {
        $state | ConvertTo-Json -Depth 3 | Set-Content $dedupFile -Encoding UTF8
    } catch {
        Write-Log "DEDUP: could not write $($dedupFile): $_"
    }
}

function Get-DomainState($state, $key) {
    $prop = $state.PSObject.Properties[$key]
    if ($prop) { return $prop.Value }
    return $null
}

function Set-DomainState($state, $key, $down, $lastAlerted) {
    $entry = [PSCustomObject]@{ down = $down; lastAlerted = $lastAlerted }
    if ($state.PSObject.Properties[$key]) {
        $state.PSObject.Properties[$key].Value = $entry
    } else {
        $state | Add-Member -MemberType NoteProperty -Name $key -Value $entry
    }
}

function Should-Alert($entry) {
    # First alert for this domain
    if ($null -eq $entry) { return $true }
    # Was up last time — this is a new failure
    if (-not $entry.down) { return $true }
    # Was already down — suppress if alerted within 24h
    if ($null -ne $entry.lastAlerted) {
        try {
            $last = [datetime]::Parse($entry.lastAlerted)
            if (([datetime]::UtcNow - $last.ToUniversalTime()).TotalHours -lt 24) {
                return $false
            }
        } catch {}
    }
    return $true
}

# ── ntfy ─────────────────────────────────────────────────────────────────────
function Send-Ntfy($title, $message, $priority = 'high', $tags = '') {
    try {
        $body = $message
        $headers = @{
            'Title'    = $title
            'Priority' = $priority
        }
        if ($tags) { $headers['Tags'] = $tags }
        Invoke-RestMethod -Method POST `
            -Uri "$ntfyBase/$ntfyTopic" `
            -Body $body `
            -Headers $headers `
            -TimeoutSec 15 `
            -ErrorAction Stop | Out-Null
        Write-Log "NTFY: sent [$priority] $title"
    } catch {
        Write-Log "NTFY: failed to send alert: $_"
    }
}

# ── Vault: fetch basic-auth credential for localhost:8199 ────────────────────
function Get-RelayCredential {
    try {
        # jengo_vault.py has no CLI get command — invoke via inline Python snippet
        $pyCode = @"
import sys, json
sys.path.insert(0, r'E:\projects\jengo')
from jengo_vault import get_credential
c = get_credential(6, 81)
print(json.dumps({'username': c.get('username',''), 'password': c.get('password','')}))
"@
        $tmpPy = [System.IO.Path]::GetTempFileName() + '.py'
        [System.IO.File]::WriteAllText($tmpPy, $pyCode, [System.Text.Encoding]::UTF8)
        $json = & python $tmpPy 2>&1
        Remove-Item $tmpPy -Force -ErrorAction SilentlyContinue
        # Filter out vault info lines (start with [vault])
        $jsonLine = ($json | Where-Object { $_ -notmatch '^\[vault\]' } | Select-Object -Last 1)
        if (-not $jsonLine) { throw "No JSON output from vault" }
        $cred = $jsonLine | ConvertFrom-Json
        return $cred
    } catch {
        Write-Log "VAULT: could not fetch cred 6/81: $_"
        return $null
    }
}

# ── HTTP probe ────────────────────────────────────────────────────────────────
# Returns [bool ok, int statusCode, string error]
function Probe-Url($url, $credential = $null, $acceptCodes = @(200)) {
    try {
        $params = @{
            UseBasicParsing = $true
            Uri             = $url
            TimeoutSec      = 15
            ErrorAction     = 'Stop'
        }
        if ($credential) {
            $pair  = "$($credential.username):$($credential.password)"
            $b64   = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes($pair))
            $params['Headers'] = @{ Authorization = "Basic $b64" }
        }
        $resp = Invoke-WebRequest @params
        $sc   = [int]$resp.StatusCode
        $ok   = $acceptCodes -contains $sc
        return $ok, $sc, ''
    } catch [System.Net.WebException] {
        if ($_.Exception.Response) {
            $sc = [int]$_.Exception.Response.StatusCode
            $ok = $acceptCodes -contains $sc
            return $ok, $sc, ''
        }
        return $false, 0, $_.Exception.Message
    } catch {
        return $false, 0, $_.Exception.Message
    }
}

# ── Main ──────────────────────────────────────────────────────────────────────
Write-Log 'START wa-uptime-monitor'

$dedup = Load-Dedup

# Fetch relay cred once (lazy — only if needed)
$relayCred = $null

# Endpoint definitions
# Each: key, label, probeFunc (scriptblock returning [ok, code, err])
$endpoints = @(
    [PSCustomObject]@{
        key   = 'api-whatsapp-wreckingball-ai'
        label = 'api.whatsapp.wreckingball.ai /api/version'
        probe = { Probe-Url 'https://api.whatsapp.wreckingball.ai/api/version' -acceptCodes @(200) }
    },
    [PSCustomObject]@{
        key   = 'whatsapp-wreckingball-ai'
        label = 'whatsapp.wreckingball.ai /'
        probe = { Probe-Url 'https://whatsapp.wreckingball.ai/' -acceptCodes @(200) }
    },
    [PSCustomObject]@{
        key   = 'localhost-8199-relay'
        label = 'localhost:8199 jengo-agi relay /'
        probe = {
            if ($null -eq $relayCred) {
                $script:relayCred = Get-RelayCredential
            }
            if ($null -eq $script:relayCred) {
                # Vault failure — treat as probe error, not endpoint down
                return $false, 0, 'vault fetch failed'
            }
            Probe-Url 'http://localhost:8199/' -credential $script:relayCred -acceptCodes @(200, 204, 302, 400, 404)
        }
    },
    [PSCustomObject]@{
        key   = 'mailbridge-wreckingball-ai'
        label = 'mailbridge.wreckingball.ai /'
        probe = {
            # HTTPS not yet configured — try HTTPS first, fall back to HTTP
            $ok, $sc, $err = Probe-Url 'https://mailbridge.wreckingball.ai/' -acceptCodes @(200, 401)
            if ($ok -or $sc -gt 0) { return $ok, $sc, $err }
            # HTTPS failed at TCP/TLS level — try HTTP
            $ok2, $sc2, $err2 = Probe-Url 'http://mailbridge.wreckingball.ai/' -acceptCodes @(200, 401)
            return $ok2, $sc2, "HTTPS-fail($err) HTTP: $err2"
        }
    }
)

foreach ($ep in $endpoints) {
    $key   = $ep.key
    $label = $ep.label

    try {
        $ok, $sc, $err = & $ep.probe
    } catch {
        $ok  = $false
        $sc  = 0
        $err = "probe exception: $_"
    }

    $entry     = Get-DomainState $dedup $key
    $wasDown   = ($entry -ne $null) -and $entry.down
    $now       = [datetime]::UtcNow.ToString('o')

    if ($ok) {
        Write-Log "OK [$sc]: $label"
        if ($wasDown) {
            # Recovery: always notify (no dedup on recoveries)
            Write-Log "RECOVERED: $label"
            Send-Ntfy "WA bridge RECOVERED: $key" "$label is back up (HTTP $sc)" -priority 'default' -tags 'white_check_mark'
        }
        Set-DomainState $dedup $key $false $now
    } else {
        $detail = if ($sc -gt 0) { "HTTP $sc" } else { $err }
        Write-Log "DOWN [$detail]: $label"

        if (Should-Alert $entry) {
            Send-Ntfy "WA bridge DOWN: $key" "$label is DOWN · $detail" -priority 'high' -tags 'rotating_light'
            Set-DomainState $dedup $key $true $now
        } else {
            Write-Log "DEDUP: suppressed repeat alert for $key (alerted within 24h)"
            # Keep existing lastAlerted; update down flag in case it was stale
            Set-DomainState $dedup $key $true $entry.lastAlerted
        }
    }
}

Save-Dedup $dedup
Write-Log 'DONE wa-uptime-monitor'
exit 0
