#requires -Version 7.0
<#
.SYNOPSIS
    Records the numbers the phase gate asks for: app + WebView2 RAM and profile disk.

.DESCRIPTION
    Read-only. WebView2 children are selected by their command line pointing at Chirpdesk's
    profiles root, so unrelated Edge/WebView2 processes are not counted. Run it with the app
    warm, after the services have settled.

.EXAMPLE
    pwsh scripts/measure.ps1
    pwsh scripts/measure.ps1 -Append
#>
[CmdletBinding()]
param(
    [switch] $Append,
    [string] $ProfilesRoot = (Join-Path $env:LOCALAPPDATA 'Chirpdesk\profiles'),
    [string] $LogPath = (Join-Path $env:LOCALAPPDATA 'Chirpdesk\logs\chirpdesk.log')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$app = Get-Process Chirpdesk -ErrorAction SilentlyContinue | Sort-Object StartTime | Select-Object -First 1
$webviews = @(
    Get-CimInstance Win32_Process -Filter "Name = 'msedgewebview2.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -and $_.CommandLine -like "*$ProfilesRoot*" }
)

$webviewProcesses = @($webviews | ForEach-Object {
    Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue
})

$appMb = if ($app) { [math]::Round($app.WorkingSet64 / 1MB, 1) } else { 0 }
$webviewMb = [math]::Round((($webviewProcesses | Measure-Object WorkingSet64 -Sum).Sum) / 1MB, 1)
$totalMb = [math]::Round($appMb + $webviewMb, 1)

$diskMb = 0
if (Test-Path -LiteralPath $ProfilesRoot) {
    $bytes = (Get-ChildItem -LiteralPath $ProfilesRoot -Recurse -File -ErrorAction SilentlyContinue |
        Measure-Object Length -Sum).Sum
    $diskMb = [math]::Round($bytes / 1MB, 1)
}

$warm = 0
$blocking = 'none'
if (Test-Path -LiteralPath $LogPath) {
    $lines = Get-Content -LiteralPath $LogPath
    $cutoff = if ($app) { $app.StartTime.AddSeconds(-5) } else { Get-Date }

    $session = @($lines | Where-Object {
        if ($_ -match '^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3})') {
            [datetime]::ParseExact($matches[1], 'yyyy-MM-dd HH:mm:ss.fff', $null) -ge $cutoff
        } else {
            $false
        }
    })

    $ids = @($session | ForEach-Object {
        if ($_ -match "blocking for '([^']+)': on") { $matches[1] }
    })
    $warm = @($ids | Select-Object -Unique).Count

    $last = $session | Select-String -Pattern 'blocking for .+: on' | Select-Object -Last 1
    if ($last) { $blocking = $last.Line }
}

$head = git -C (Split-Path -Parent $PSScriptRoot) rev-parse --short HEAD 2>$null
if (-not $head) { $head = 'unknown' }

$summary = @"
## Measurement, $(Get-Date -Format 'yyyy-MM-dd HH:mm')

Base commit ``$head``. App process $appMb MB; WebView2 $webviewMb MB across $($webviewProcesses.Count) processes; total **$totalMb MB**. Profiles on disk **$diskMb MB**. Warm services logged: $warm.

Last blocking line: ``$blocking``
"@

Write-Host $summary

if ($Append) {
    $path = Join-Path (Split-Path -Parent $PSScriptRoot) 'docs/measurements.md'
    Add-Content -LiteralPath $path -Value "`n$summary"
    Write-Host "Appended to $path"
}
