#requires -Version 7.0
<#
.SYNOPSIS
    Runs the automated half of the Chirpdesk phase gate and prints one PASS/FAIL line per check.

.DESCRIPTION
    Two invocations, because one process cannot observe its own teardown:

      1. Chirpdesk.exe --selftest   headless checks: settings, definitions, catalog, migration,
                                    registry, command bar, environment, notifications, teardown
      2. Chirpdesk.exe --open=whatsapp --hidden --quit-after=25
                                    a real service is created, parked and disposed through the
                                    normal quit path, so the orphan check afterwards means something

    Exit code 0 means every check passed. The human half (two accounts, a real message, the
    visible palette) is in docs/testing-strategy.md §5 and cannot be automated.

.EXAMPLE
    pwsh scripts/gate.ps1
    pwsh scripts/gate.ps1 -SoakSeconds 600
#>
[CmdletBinding()]
param(
    [ValidateRange(10, 3600)]
    [int] $SoakSeconds = 120,

    [switch] $SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$failures = 0

function Write-Check {
    param([string] $Name, [bool] $Ok, [string] $Detail)

    $status = if ($Ok) { 'PASS' } else { 'FAIL' }
    if (-not $Ok) { $script:failures++ }
    Write-Host ("[{0}] {1} - {2}" -f $status, $Name, $Detail)
}

function Resolve-Dotnet {
    $onPath = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    $local = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
    if (Test-Path $local) { return $local }

    throw 'dotnet not found. Install the .NET 8 SDK or put it on PATH.'
}

Push-Location $repo
try {
    if (-not $SkipBuild) {
        $dotnet = Resolve-Dotnet
        & $dotnet build --nologo | Out-Null
        Write-Check 'build' ($LASTEXITCODE -eq 0) "dotnet build exited $LASTEXITCODE"

        & $dotnet build (Join-Path $repo 'tests/helpers/StubIntegration/StubIntegration.csproj') --nologo | Out-Null
        Write-Check 'build-stub' ($LASTEXITCODE -eq 0) "stub helper exited $LASTEXITCODE"
        if ($LASTEXITCODE -ne 0) { exit 1 }
        if ($LASTEXITCODE -ne 0) { exit 1 }
    }

    $exe = Join-Path $repo 'artifacts\bin\Chirpdesk\debug\Chirpdesk.exe'
    if (-not (Test-Path $exe)) {
        Write-Check 'binary' $false "not found at $exe (build first)"
        exit 1
    }

    # The gate needs the mutex: a resident instance would make --selftest exit 3 without
    # testing anything, and a green gate must never be produced by a run that did not happen.
    $running = @(Get-Process Chirpdesk -ErrorAction SilentlyContinue)
    Write-Check 'no-running-instance' ($running.Count -eq 0) "Chirpdesk processes: $($running.Count) (quit from the tray first)"
    if ($running.Count -gt 0) { exit 1 }

    # 0. Integration boundary: launch the stub helper through the real Windows launcher and
    # require a handshake, a notification, an unread count and a command before a clean stop.
    $stubDir = Join-Path $repo 'artifacts\bin\StubIntegration\debug'
    if (Test-Path (Join-Path $stubDir 'stub-integration.exe')) {
        $smokeDir = Join-Path ([IO.Path]::GetTempPath()) ("chirpdesk-integration-smoke-" + [Guid]::NewGuid().ToString('N'))
        try {
            New-Item -ItemType Directory -Force -Path $smokeDir | Out-Null
            Copy-Item (Join-Path $stubDir '*') $smokeDir -Recurse
            Copy-Item (Join-Path $repo 'tests\helpers\StubIntegration\sample\*') $smokeDir -Force

            $smoke = Start-Process -FilePath $exe -ArgumentList "--integration-smoke=$smokeDir" -PassThru -Wait
            Start-Sleep -Seconds 1
            $orphan = @(Get-Process stub-integration -ErrorAction SilentlyContinue).Count
            Write-Check 'integration-smoke' ($smoke.ExitCode -eq 0 -and $orphan -eq 0) "exit $($smoke.ExitCode), orphan helpers: $orphan"
        }
        finally {
            Remove-Item -LiteralPath $smokeDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    else {
        Write-Check 'integration-smoke' $false "stub helper not built at $stubDir"
    }

    # 1. The headless gate. Its own PASS/FAIL lines go to logs/selftest.log and the console.
    $selfTest = Start-Process -FilePath $exe -ArgumentList '--selftest', "--soak-seconds=$SoakSeconds" -PassThru -Wait
    Write-Check 'selftest' ($selfTest.ExitCode -eq 0) "headless checks in $(Join-Path $env:LOCALAPPDATA 'Chirpdesk\logs\selftest.log'); exit $($selfTest.ExitCode)"

    # 2. Real service, parked, normal quit. No window is ever shown.
    $before = @(Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'" |
        Where-Object { $_.CommandLine -like '*Chirpdesk*' }).Count

    $soak = Start-Process -FilePath $exe -ArgumentList '--open=whatsapp', '--hidden', '--quit-after=25' -PassThru -Wait
    Write-Check 'warm-service-quit' ($soak.ExitCode -eq 0) "exit $($soak.ExitCode)"

    Start-Sleep -Seconds 3
    $after = @(Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'" |
        Where-Object { $_.CommandLine -like '*Chirpdesk*' }).Count
    $app = @(Get-Process Chirpdesk -ErrorAction SilentlyContinue).Count

    Write-Check 'no-orphan-webviews' ($after -le $before) "webview processes using Chirpdesk profiles: $before before, $after after"
    Write-Check 'no-orphan-app' ($app -eq 0) "Chirpdesk processes left: $app"
}
finally {
    Pop-Location
}

if ($failures -gt 0) {
    Write-Host "$failures check(s) failed."
    exit 1
}

Write-Host 'All automated checks passed. Human rows: docs/testing-strategy.md section 5'
exit 0
