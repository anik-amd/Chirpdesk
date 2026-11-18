#requires -Version 7.0
<#
.SYNOPSIS
    Builds the stub integration into a signed registry and publishes it on a throwaway branch.

.DESCRIPTION
    Developer-only tool for exercising the repository install and consent flow without a real
    third-party plugin. It signs a registry with a stable local key, pushes it to a branch, and
    prints the raw URL to add in Plugins -> Browse. The branch is public while it exists; run
    -Remove when finished.

.EXAMPLE
    pwsh scripts/test-registry.ps1
    # add the printed URL in the app, then:
    pwsh scripts/test-registry.ps1 -Remove
#>
[CmdletBinding()]
param(
    [string] $Branch = 'dev-test-registry',

    [switch] $Remove
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$worktree = Join-Path $env:TEMP 'chirpdesk-dev-registry-wt'
$source = Join-Path $env:TEMP 'chirpdesk-dev-registry-src'

function Resolve-Dotnet {
    $local = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
    if (Test-Path $local) { return $local }

    $onPath = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    throw 'dotnet not found.'
}

function Remove-Worktree {
    if (Test-Path $worktree) {
        git -C $repo worktree remove --force $worktree 2>$null | Out-Null
        Remove-Item -LiteralPath $worktree -Recurse -Force -ErrorAction SilentlyContinue
    }

    git -C $repo branch -D $Branch 2>$null | Out-Null
}

if ($Remove) {
    Remove-Worktree
    git -C $repo push origin --delete $Branch 2>$null | Out-Null
    Write-Host "Removed '$Branch'."
    exit 0
}

$remote = (git -C $repo remote get-url origin).Trim()
$match = [regex]::Match($remote, 'github\.com[:/](?<owner>[^/]+)/(?<name>[^/]+?)(?:\.git)?$')
if (-not $match.Success) {
    throw "Could not parse a GitHub owner/repo from '$remote'."
}

$owner = $match.Groups['owner'].Value
$name = $match.Groups['name'].Value

$dotnet = Resolve-Dotnet
& $dotnet build (Join-Path $repo 'tests/helpers/StubIntegration/StubIntegration.csproj') -c Debug --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Stub build exited $LASTEXITCODE" }

Remove-Item -LiteralPath $source -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path (Join-Path $source 'stub-integration') | Out-Null
Copy-Item (Join-Path $repo 'artifacts/bin/StubIntegration/debug/*') (Join-Path $source 'stub-integration') -Recurse
Copy-Item (Join-Path $repo 'tests/helpers/StubIntegration/sample/*') (Join-Path $source 'stub-integration') -Force

$empty = Join-Path $env:TEMP 'chirpdesk-dev-registry-empty'
New-Item -ItemType Directory -Force -Path $empty | Out-Null

$keyDir = Join-Path $env:LOCALAPPDATA 'Chirpdesk\dev'
$keyPath = Join-Path $keyDir 'test-registry.pem'
$publicPath = Join-Path $keyDir 'test-registry.pub.pem'
New-Item -ItemType Directory -Force -Path $keyDir | Out-Null

if (-not (Test-Path $keyPath)) {
    pwsh -NoProfile -File (Join-Path $repo 'scripts/new-update-key.ps1') -Path $keyPath -PublicPath $publicPath | Out-Null
}

$out = Join-Path $env:TEMP 'chirpdesk-dev-registry-out'
pwsh -NoProfile -File (Join-Path $repo 'scripts/build-registry.ps1') `
    -PluginsDir $source `
    -ExtraPluginsDir $empty `
    -KeyPath $keyPath `
    -PublicKeyPath $publicPath `
    -OutDir $out `
    -Name 'Chirpdesk Dev' | Select-Object -Last 3

Remove-Worktree
git -C $repo worktree add -B $Branch $worktree HEAD 2>&1 | Out-Null

$target = Join-Path $worktree 'registry'
Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item $out $target -Recurse

git -C $worktree add registry
git -C $worktree commit -m "test: dev registry $(Get-Date -Format o)" 2>&1 | Out-Null
git -C $worktree push -f origin $Branch 2>&1 | Select-Object -Last 2

$url = "https://raw.githubusercontent.com/$owner/$name/$Branch/registry/"
Write-Host ''
Write-Host "Test registry: $url"
Write-Host "Remove it later: pwsh scripts/test-registry.ps1 -Remove"
