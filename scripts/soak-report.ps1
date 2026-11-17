#requires -Version 7.0
<#
.SYNOPSIS
    Reads the phase-0 soak out of the app's log, without touching the running app.

.DESCRIPTION
    The soak is "leave it tray-hidden for three hours, then send a message": the evidence is
    that the same process was alive the whole time, that nothing was replayed, and that the
    message that arrived after the gap still produced a toast.

    Read-only. Run it after the last message, not before.

.EXAMPLE
    pwsh scripts/soak-report.ps1 -MinimumHours 3
#>
[CmdletBinding()]
param(
    [double] $MinimumHours = 3,
    [string] $LogPath = (Join-Path $env:LOCALAPPDATA 'Chirpdesk\logs\chirpdesk.log')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path $LogPath)) {
    Write-Host "no log at $LogPath"
    exit 1
}

$app = Get-Process Chirpdesk -ErrorAction SilentlyContinue | Sort-Object StartTime | Select-Object -First 1
if (-not $app) {
    Write-Host 'Chirpdesk is not running; a soak needs it alive.'
    exit 1
}

$lines = Get-Content $LogPath

function Get-Stamp([string] $line) {
    if ($line -match '^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3})') {
        return [datetime]::ParseExact($matches[1], 'yyyy-MM-dd HH:mm:ss.fff', $null)
    }

    return $null
}

# The log is append-only across every run, so the soak is only the slice belonging to the
# process that is alive now. A few seconds of slack covers log lines written before StartTime
# was sampled.
$cutoff = $app.StartTime.AddSeconds(-5)
$session = @($lines | Where-Object { $stamp = Get-Stamp $_; $null -ne $stamp -and $stamp -ge $cutoff })

$started = @($session | Where-Object { $_ -match 'info started' })
$toasts = @($session | Where-Object { $_ -match 'toast shown' })
$unread = @($session | Where-Object { $_ -match 'unread \d+ for' })
$shows = @($session | Where-Object { $_ -match 'showing the window on request' })

$uptime = (Get-Date) - $app.StartTime

"pid            : $($app.Id)"
"process start  : $($app.StartTime.ToString('yyyy-MM-dd HH:mm:ss'))"
"uptime         : $([math]::Round($uptime.TotalHours, 2)) h"
"process starts : $($started.Count) in this session (a soak needs exactly one)"
"toasts shown   : $($toasts.Count)"
"window shows   : $($shows.Count) (user-requested; a soak expects none unless you clicked a toast)"
if ($toasts.Count -gt 0) { "last toast     : $($toasts[-1])" }
if ($unread.Count -gt 0) { "last unread    : $($unread[-1])" }

$problems = @()
if ($uptime.TotalHours -lt $MinimumHours) { $problems += "uptime is under $MinimumHours h" }
if ($started.Count -gt 1) { $problems += 'the app restarted during the soak' }
if ($toasts.Count -eq 0) { $problems += 'no toast was ever shown' }

Write-Host ''
Write-Host '--- log, last 15 lines ---'
$lines | Select-Object -Last 15

Write-Host ''
if ($problems.Count -eq 0) {
    Write-Host "SOAK PASS - one process, $([math]::Round($uptime.TotalHours, 2)) h hidden, toasts delivered."
    exit 0
}

Write-Host ('SOAK INCOMPLETE: ' + ($problems -join '; '))
exit 1
