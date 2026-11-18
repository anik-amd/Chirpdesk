#requires -Version 7.0
<#
.SYNOPSIS
    Builds a static, signed Chirpdesk plugin registry from the bundled plugin folders.

.DESCRIPTION
    Creates one zip per plugin, computes its SHA-256 and size, writes a deterministic-by-id
    index.json, copies key.pem and signs the exact index bytes into index.json.sig. The output
    folder can be published as-is on any static HTTPS host (GitHub Pages, raw files, S3).

.EXAMPLE
    pwsh scripts/build-registry.ps1 `
      -KeyPath D:\keys\chirpdesk-registry.pem `
      -PublicKeyPath D:\keys\chirpdesk-registry.pub.pem `
      -OutDir dist/registry
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $KeyPath,

    [Parameter(Mandatory)]
    [string] $PublicKeyPath,

    [string] $PluginsDir = 'plugins',

    [string] $OutDir = 'dist/registry',

    [string] $Name = 'Chirpdesk Official',

    [string] $MinAppVersion = '0.1.0'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$pluginsRoot = if ([IO.Path]::IsPathRooted($PluginsDir)) { $PluginsDir } else { Join-Path $repo $PluginsDir }
$out = if ([IO.Path]::IsPathRooted($OutDir)) { $OutDir } else { Join-Path $repo $OutDir }

if (-not (Test-Path -LiteralPath $KeyPath)) {
    throw "Signing key not found: $KeyPath"
}

if (-not (Test-Path -LiteralPath $PublicKeyPath)) {
    throw "Public key not found: $PublicKeyPath"
}

if (-not (Test-Path -LiteralPath $pluginsRoot)) {
    throw "Plugin folder not found: $pluginsRoot"
}

if (Test-Path -LiteralPath $out) {
    Remove-Item -LiteralPath $out -Recurse -Force
}

New-Item -ItemType Directory -Force -Path (Join-Path $out 'plugins') | Out-Null

$publicPem = (Get-Content -LiteralPath $PublicKeyPath -Raw).Trim()
$key = [System.Security.Cryptography.ECDsa]::Create()
try {
    $key.ImportFromPem((Get-Content -LiteralPath $KeyPath -Raw))

    if ($key.KeySize -ne 256) {
        throw "Signing key is $($key.KeySize) bits; a P-256 key is required."
    }

    $fingerprint = 'ecdsa-p256:' + [Convert]::ToHexString(
        [System.Security.Cryptography.SHA256]::HashData($key.ExportSubjectPublicKeyInfo())
    ).ToLowerInvariant().Substring(0, 16)

    $entries = @()

    foreach ($folder in Get-ChildItem -LiteralPath $pluginsRoot -Directory | Sort-Object Name) {
        $manifestPath = Join-Path $folder.FullName 'plugin.json'
        if (-not (Test-Path -LiteralPath $manifestPath)) {
            Write-Warning "Skipping $($folder.Name): no plugin.json"
            continue
        }

        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        $id = [string]$manifest.id
        $version = [string]$manifest.version
        $kind = [string]$manifest.kind

        if ([string]::IsNullOrWhiteSpace($id) -or [string]::IsNullOrWhiteSpace($version) -or [string]::IsNullOrWhiteSpace($kind)) {
            Write-Warning "Skipping $($folder.Name): id, version or kind missing"
            continue
        }

        if ($kind -in @('tool', 'integration')) {
            Write-Warning "Skipping $($folder.Name): $kind plugins are bundled-only in v1"
            continue
        }

        $archiveName = "$id-$version.zip"
        $archivePath = Join-Path (Join-Path $out 'plugins') $archiveName

        Compress-Archive -Path (Join-Path $folder.FullName '*') -DestinationPath $archivePath -CompressionLevel Optimal -Force

        $archive = Get-Item -LiteralPath $archivePath
        $sha256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()

        $entries += [ordered]@{
            id = $id
            kind = $kind
            name = [string]$manifest.name
            version = $version
            apiVersion = [int]$manifest.apiVersion
            minAppVersion = $MinAppVersion
            sha256 = $sha256
            size = [long]$archive.Length
            url = "plugins/$archiveName"
            permissions = @($manifest.permissions | Where-Object { $_ })
        }
    }

    if ($entries.Count -eq 0) {
        throw "No plugins found under $pluginsRoot"
    }

    $index = [ordered]@{
        schema = 1
        name = $Name
        generated = [DateTimeOffset]::UtcNow.ToString('o')
        keyFingerprint = $fingerprint
        plugins = @($entries | Sort-Object { $_['id'] })
    }

    # LF, not the Windows default: the signature covers exact bytes and the registry must
    # survive Git checkout unchanged. .gitattributes marks registry/** as -text.
    $indexJson = ($index | ConvertTo-Json -Depth 8) -replace "`r`n", "`n"
    $indexBytes = [Text.Encoding]::UTF8.GetBytes($indexJson)

    [IO.File]::WriteAllBytes((Join-Path $out 'index.json'), $indexBytes)
    [IO.File]::WriteAllText((Join-Path $out 'key.pem'), $publicPem)

    $signature = $key.SignData(
        $indexBytes,
        [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)

    [IO.File]::WriteAllText(
        (Join-Path $out 'index.json.sig'),
        [Convert]::ToBase64String($signature))

    Write-Host "Registry: $out"
    Write-Host "Fingerprint: $fingerprint"
    Write-Host "Plugins: $($entries.Count)"
}
finally {
    $key.Dispose()
}
