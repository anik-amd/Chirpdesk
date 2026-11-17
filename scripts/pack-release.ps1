#requires -Version 7.0
<#
.SYNOPSIS
    Builds the portable zip and, when Inno Setup is present, the per-user installer.

.DESCRIPTION
    Publishes the framework-dependent x64 build into dist/publish, adds the portable README,
    zips it, then compiles installer/Chirpdesk.iss with ISCC. The installer detects the .NET 8
    Desktop Runtime and WebView2 Evergreen before it writes anything.

.EXAMPLE
    pwsh scripts/pack-release.ps1 -Version 0.1.0
    pwsh scripts/pack-release.ps1 -Version 0.1.0 -SkipInstaller
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Version,

    [switch] $SkipInstaller,

    [switch] $SkipPublish
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $repo 'dist'
$publish = Join-Path $dist 'publish'
$project = Join-Path $repo 'src/Chirpdesk/Chirpdesk.csproj'

function Resolve-Dotnet {
    $onPath = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    $local = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $local) { return $local }

    throw 'dotnet not found. Install the .NET 8 SDK or put it on PATH.'
}

function Resolve-Iscc {
    $onPath = Get-Command iscc -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    )

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }

    return $null
}

New-Item -ItemType Directory -Force -Path $dist | Out-Null

if (-not $SkipPublish) {
    if (Test-Path -LiteralPath $publish) {
        Remove-Item -LiteralPath $publish -Recurse -Force
    }

    $dotnet = Resolve-Dotnet
    & $dotnet publish $project -c Release -r win-x64 --self-contained false `
        -p:DebugType=None -p:DebugSymbols=false -o $publish
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish exited $LASTEXITCODE"
    }
}

if (-not (Test-Path -LiteralPath (Join-Path $publish 'Chirpdesk.exe'))) {
    throw "Publish output is incomplete: $publish"
}

$readme = Join-Path $repo 'docs/portable-readme.txt'
if (Test-Path -LiteralPath $readme) {
    Copy-Item -LiteralPath $readme -Destination (Join-Path $publish 'README.txt') -Force
}

$zip = Join-Path $dist "Chirpdesk-$Version-portable.zip"
if (Test-Path -LiteralPath $zip) {
    Remove-Item -LiteralPath $zip -Force
}

Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip -CompressionLevel Optimal
Write-Host "Portable: $zip"

if ($SkipInstaller) {
    Write-Host 'Installer skipped.'
    return
}

$iscc = Resolve-Iscc
if (-not $iscc) {
    Write-Warning 'ISCC.exe not found; portable zip only. Install Inno Setup 6 to build the installer.'
    return
}

$iss = Join-Path $repo 'installer/Chirpdesk.iss'
& $iscc "/DAppVersion=$Version" $iss
if ($LASTEXITCODE -ne 0) {
    throw "ISCC exited $LASTEXITCODE"
}

$setup = Join-Path $dist "Chirpdesk-Setup-$Version.exe"
if (-not (Test-Path -LiteralPath $setup)) {
    throw "Installer was not produced at $setup"
}

Write-Host "Installer: $setup"
