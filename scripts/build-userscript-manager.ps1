#requires -Version 7.0
<#
.SYNOPSIS
    Builds the Userscripts integration helper into its registry plugin folder.

.DESCRIPTION
    The helper is a normal .NET console app. It is published framework-dependent (the installer
    already requires the .NET 8 Desktop Runtime) and copied beside the plugin manifests so
    scripts/build-registry.ps1 can package it as a signed integration plugin.
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'src/UserscriptManager/UserscriptManager.csproj'
$target = Join-Path $repo 'registry-src/userscripts'
$staging = Join-Path $repo 'dist/userscript-manager'

if (-not (Test-Path -LiteralPath $project)) {
    throw "Project not found: $project"
}

if (Test-Path -LiteralPath $staging) {
    Remove-Item -LiteralPath $staging -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $target | Out-Null

dotnet publish $project -c $Configuration -o $staging
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

Get-ChildItem -LiteralPath $staging -File | Where-Object {
    $_.Extension -in '.exe', '.dll', '.json' -and $_.Name -notlike '*.pdb'
} | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $target -Force
}

Write-Host "Userscript manager published to $target"
Get-ChildItem -LiteralPath $target -File | Select-Object Name, Length | Format-Table -AutoSize
