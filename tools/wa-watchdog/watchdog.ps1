# WhatsApp Bridge Watchdog
# Runs every 6h (scheduled task). Detects the failure modes that have actually
# bitten us and alerts Martien via WhatsApp (the bridge itself):
#   1. No connected WhatsApp session          -> re-pair needed (the 401 logout of 13-jul)
#   2. "Authentication failure: 405" events   -> WA rejected our client version at pairing
#   3. "Authentication failure: 401" events   -> WA invalidated device creds (logged out),
#                                                 but only alerted when NO session is connected -
#                                                 the WA protocol log line carries no SessionId,
#                                                 so a dead/legacy session retried at startup logs
#                                                 the identical text as a genuine live-session death.
#   4. Version drift while disconnected       -> self-update (WaVersionProvider) not keeping up
#   5. New Baileys release                    -> upstream protocol changes worth reviewing
# Credentials live in config.json NEXT TO this script on the server - never in the repo:
#   apiBase, bridgeEmail, waToken, alertTo, activeVersionFile.
#   Optional: heartbeatScript (path to Send-Heartbeat.ps1) and heartbeatConfig (path passed
#   through as -ConfigPath) for the fallback alert below; both have sensible defaults.
# waToken is the plaintext token of the "jengo" ApiConnection (long-lived, does not rotate with
# the frontend login password - that's what broke this check on 2026-07-31/08-02). It is used
# both to send alerts (Bearer, /api/wa/sendMessage) and for the session-health check below
# (X-Api-Token/X-Email, /api/whatsapp/sessions). To inspect or rotate it: log into the bridge UI
# as bridgeEmail -> Settings -> API Connections -> "jengo".
# State (dedup, last-checked timestamps) in state.json. Log in watchdog.log.

param([switch]$TestAlert)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$root      = Split-Path -Parent $MyInvocation.MyCommand.Path
$configPath = Join-Path $root 'config.json'
$statePath  = Join-Path $root 'state.json'
$logPath    = Join-Path $root 'watchdog.log'

function Log([string]$msg) {
    $line = "$((Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss'))Z $msg"
    Add-Content -Path $logPath -Value $line -Encoding utf8
}

$config = Get-Content $configPath -Raw | ConvertFrom-Json
$state  = if (Test-Path $statePath) { Get-Content $statePath -Raw | ConvertFrom-Json } else { $null }
if ($null -eq $state) {
    $state = [PSCustomObject]@{ lastEventCheckUtc = (Get-Date).ToUniversalTime().ToString('o'); alerted = [PSCustomObject]@{}; lastBaileysTag = '' }
}

$alerts = New-Object System.Collections.ArrayList

# Dedup: same alert key at most once per 24h
function ShouldAlert([string]$key) {
    $prev = $state.alerted.PSObject.Properties[$key]
    if ($null -ne $prev) {
        $when = [DateTime]::Parse($prev.Value).ToUniversalTime()
        if (((Get-Date).ToUniversalTime() - $when).TotalHours -lt 24) { return $false }
    }
    return $true
}
function MarkAlerted([string]$key) {
    $now = (Get-Date).ToUniversalTime().ToString('o')
    if ($null -ne $state.alerted.PSObject.Properties[$key]) { $state.alerted.$key = $now }
    else { $state.alerted | Add-Member -NotePropertyName $key -NotePropertyValue $now }
}

# ---- 1. Session health via bridge API -------------------------------------
# Auth via the dedicated long-lived API token (see header comment) instead of a frontend
# login+password - that password rotates and broke this check twice already.
$sessionConnected = $false
try {
    $sessions = Invoke-RestMethod -Uri "$($config.apiBase)/api/whatsapp/sessions" -TimeoutSec 30 `
        -Headers @{ 'X-Api-Token' = $config.waToken; 'X-Email' = $config.bridgeEmail }
    $connected = @($sessions | Where-Object { $_.status -eq 'connected' })
    $sessionConnected = $connected.Count -gt 0
    if (-not $sessionConnected -and (ShouldAlert 'session-down')) {
        [void]$alerts.Add(@{ key = 'session-down'; body = "WhatsApp bridge has NO connected session (statuses: $(@($sessions | ForEach-Object { $_.status }) -join ', ' )). Outgoing+incoming DOWN. Re-pair: https://whatsapp.wreckingball.ai -> Connect WhatsApp -> scan QR." })
    }
    Log "session check: connected=$sessionConnected sessionIds=$(($connected | ForEach-Object { $_.sessionId }) -join ', ')"
} catch {
    Log "session check FAILED: $($_.Exception.Message)"
    if (ShouldAlert 'api-down') {
        [void]$alerts.Add(@{ key = 'api-down'; body = "WhatsApp bridge API is not responding, or the watchdog's API token is no longer valid ($($_.Exception.Message)). Check app pool WhatsAppBridgeAPIPool on 85.215.217.154, and that config.json's waToken is still an active ApiConnection." })
    }
}

# ---- 2+3. Auth failure events since last run --------------------------------
try {
    $since = [DateTime]::Parse($state.lastEventCheckUtc).ToLocalTime()
    $events = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $since } -ErrorAction SilentlyContinue |
        Where-Object { $_.Message -match 'Dawa' -and $_.Message -match 'Authentication failure' })
    $n405 = @($events | Where-Object { $_.Message -match 'Authentication failure: 405' }).Count
    $n401 = @($events | Where-Object { $_.Message -match 'Authentication failure: 401' }).Count
    if ($n405 -gt 0 -and (ShouldAlert 'auth-405')) {
        [void]$alerts.Add(@{ key = 'auth-405'; body = "WhatsApp 405-rejected $n405 connection attempt(s) since $($since.ToString('dd-MM HH:mm')) - client version outdated and self-update did not cover it. Check wa-version-active.json vs Baileys baileys-version.json; Dawa may need a code update." })
    }
    # 401 events carry no SessionId in the log line (Dawa/Noise/NoiseProcessor.cs logs only the WA
    # protocol reason code), so a dead/legacy session retried at startup produces byte-identical log
    # text to a genuine live-session logout (the false alarm of 2026-07-31/08-01). The only reliable
    # signal available is whether a connected session exists right now (from section 1, above) - if
    # one does, these 401s are noise from a session that isn't the one actually serving traffic.
    if ($n401 -gt 0 -and $sessionConnected) {
        Log "event check: $n401 x 401 since $($since.ToString('dd-MM HH:mm')) suppressed - a connected session exists"
    } elseif ($n401 -gt 0 -and (ShouldAlert 'auth-401')) {
        [void]$alerts.Add(@{ key = 'auth-401'; body = "WhatsApp invalidated the device credentials ($n401 x 401 since $($since.ToString('dd-MM HH:mm'))). No session is currently connected - session logged out, QR re-pair needed at https://whatsapp.wreckingball.ai." })
    }
    Log "event check since $($since.ToString('o')): 405=$n405 401=$n401 sessionConnected=$sessionConnected"
} catch {
    Log "event check FAILED: $($_.Exception.Message)"
}
$state.lastEventCheckUtc = (Get-Date).ToUniversalTime().ToString('o')

# ---- 4. Version drift (only alarming when we're also disconnected) ---------
try {
    $upstream = Invoke-RestMethod -Uri 'https://raw.githubusercontent.com/WhiskeySockets/Baileys/master/src/Defaults/baileys-version.json' -TimeoutSec 30
    $active = Get-Content $config.activeVersionFile -Raw | ConvertFrom-Json
    $drift = ($upstream.version[2] -ne $active.version[2])
    Log "version: upstream=$($upstream.version -join '.') active=$($active.version -join '.') drift=$drift"
    if ($drift -and -not $sessionConnected -and (ShouldAlert 'version-drift')) {
        [void]$alerts.Add(@{ key = 'version-drift'; body = "Bridge version ($($active.version -join '.')) lags WhatsApp Web ($($upstream.version -join '.')) AND the session is down - self-update is not keeping up. Restart app pool WhatsAppBridgeAPIPool first; if pairing still fails, Dawa needs attention." })
    }
} catch {
    Log "version check FAILED: $($_.Exception.Message)"
}

# ---- 5. New Baileys release -------------------------------------------------
try {
    $rel = Invoke-RestMethod -Uri 'https://api.github.com/repos/WhiskeySockets/Baileys/releases/latest' -TimeoutSec 30 `
        -Headers @{ 'User-Agent' = 'wa-bridge-watchdog' }
    if ($state.lastBaileysTag -and $rel.tag_name -ne $state.lastBaileysTag -and (ShouldAlert "baileys-$($rel.tag_name)")) {
        [void]$alerts.Add(@{ key = "baileys-$($rel.tag_name)"; body = "New Baileys release $($rel.tag_name) (was $($state.lastBaileysTag)). WhatsApp protocol may have changed - worth having Jengo review the diff for Dawa: $($rel.html_url)" })
    }
    $state.lastBaileysTag = $rel.tag_name
    Log "baileys release: $($rel.tag_name)"
} catch {
    Log "baileys release check FAILED: $($_.Exception.Message)"
}

if ($TestAlert) {
    [void]$alerts.Add(@{ key = "test-$((Get-Date).Ticks)"; body = 'Test alert - WhatsApp Bridge watchdog is live on 85.215.217.154 (runs every 6h). You will only hear from me when something needs attention.' })
}

# ---- Fallback alert channel (task 856) --------------------------------------
# When the WhatsApp send below fails, the outage silences its own alarm (the 27-08 session
# loss went unnoticed exactly this way). Fallback = the shared dead-man's-switch library
# (jengo-system-private/tools/heartbeat/Send-Heartbeat.ps1): an instant ntfy.sh push (plus
# the healthchecks.io /fail ping once that account exists), deliberately NOT WhatsApp.
# Send-Heartbeat is guaranteed harmless (missing script/config/network = silent no-op), and
# it reads C:\tools\heartbeat-config.json (ntfyTopic) unless heartbeatConfig overrides it.
function Resolve-HeartbeatScript {
    $candidates = @()
    if ($config.PSObject.Properties['heartbeatScript'] -and -not [string]::IsNullOrWhiteSpace($config.heartbeatScript)) {
        $candidates += $config.heartbeatScript
    }
    $candidates += @(
        'C:\projects\jengo\jengo-system-private\tools\heartbeat\Send-Heartbeat.ps1',
        'E:\projects\jengo\jengo-system-private\tools\heartbeat\Send-Heartbeat.ps1',
        (Join-Path $root 'Send-Heartbeat.ps1')
    )
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    return $null
}
function Send-FallbackAlert([string]$key, [string]$body) {
    try {
        $script = Resolve-HeartbeatScript
        if ($null -eq $script) {
            Log "FALLBACK ALERT SKIPPED ($key): Send-Heartbeat.ps1 not found (set heartbeatScript in config.json)"
            return
        }
        $hbArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $script,
                    '-Check', 'wa-watchdog', '-Fail', '-Message', "[WA-Watchdog fallback - WhatsApp send failed] $body")
        if ($config.PSObject.Properties['heartbeatConfig'] -and -not [string]::IsNullOrWhiteSpace($config.heartbeatConfig)) {
            $hbArgs += @('-ConfigPath', $config.heartbeatConfig)
        }
        & powershell.exe @hbArgs | Out-Null
        Log "FALLBACK ALERT ($key): heartbeat fired via $script (exit=$LASTEXITCODE)"
    } catch {
        # Never let the fallback path break the remaining alerts.
        Log "FALLBACK ALERT FAILED ($key): $($_.Exception.Message)"
    }
}

# ---- Send alerts ------------------------------------------------------------
foreach ($a in $alerts) {
    $sent = $false
    try {
        Invoke-RestMethod -Uri "$($config.apiBase)/api/wa/sendMessage" -Method Post -ContentType 'application/json' -TimeoutSec 30 `
            -Headers @{ Authorization = "Bearer $($config.waToken)" } `
            -Body (@{ to = $config.alertTo; body = "[WA-Watchdog] $($a.body)" } | ConvertTo-Json) | Out-Null
        $sent = $true
    } catch {
        Log "ALERT SEND FAILED ($($a.key)): $($_.Exception.Message)"
        # Only when the WhatsApp send actually failed - never on every alert.
        Send-FallbackAlert $a.key $a.body
    }
    Log "ALERT [$($a.key)] sent=$sent : $($a.body)"
    MarkAlerted $a.key
}
if ($alerts.Count -eq 0) { Log 'OK - no alerts' }

$state | ConvertTo-Json -Depth 5 | Set-Content -Path $statePath -Encoding utf8
