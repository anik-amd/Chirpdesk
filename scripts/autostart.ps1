#requires -Version 7.0
<#
.SYNOPSIS
    Enables, disables or inspects the temporary phase-0 autostart entry.

.DESCRIPTION
    The phase-0 checklist wants one reboot with Chirpdesk starting by itself and no window
    appearing. Run-key entries have no delay field, so the value wraps the exe in a
    `timeout 20` to approximate the 20 s delay the product wants at login.

    This writes HKCU only - no elevation, nothing machine-wide - and `-Action disable`
    removes it again.

.EXAMPLE
    pwsh scripts/autostart.ps1 -Action enable
    pwsh scripts/autostart.ps1 -Action status
    pwsh scripts/autostart.ps1 -Action disable
#>
[CmdletBinding()]
param(
    [ValidateSet('enable', 'disable', 'status')]
    [string] $Action = 'status'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$name = 'Chirpdesk'
$exe = Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\bin\Chirpdesk\debug\Chirpdesk.exe'

switch ($Action) {
    'enable' {
        if (-not (Test-Path $exe)) {
            throw "binary not found at $exe; build first"
        }

        $value = "cmd.exe /c timeout /t 20 /nobreak >nul & start `"`" `"$exe`" --autostart"
        New-ItemProperty -Path $runKey -Name $name -Value $value -PropertyType String -Force | Out-Null
        Write-Host "enabled: $value"
    }
    'disable' {
        Remove-ItemProperty -Path $runKey -Name $name -ErrorAction SilentlyContinue
        Write-Host 'disabled'
    }
    'status' {
        $item = Get-ItemProperty -Path $runKey -ErrorAction SilentlyContinue
        $current = if ($item -and $item.PSObject.Properties.Name -contains $name) { $item.$name } else { $null }

        if ($current) { Write-Host "enabled: $current" } else { Write-Host 'not registered' }
    }
}
