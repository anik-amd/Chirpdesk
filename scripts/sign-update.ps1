#requires -Version 7.0
<#
.SYNOPSIS
    Signs a release manifest for Chirpdesk's self-updater.

.DESCRIPTION
    Hashes the installer, fills UpdateManifest, signs the canonical bytes with the release key,
    writes update.json, then reads it back and verifies it with the derived public key. The app's
    update path is not involved.

.EXAMPLE
    pwsh scripts/sign-update.ps1 `
        -Installer dist/Chirpdesk-Setup.exe `
        -Version 1.3.0 `
        -Url https://github.com/anik-amd/Chirpdesk/releases/download/v1.3.0/Chirpdesk-Setup.exe `
        -KeyPath D:\keys\chirpdesk-update.pem `
        -Out dist/update.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Installer,

    [Parameter(Mandatory)]
    [string] $Version,

    [Parameter(Mandatory)]
    [string] $Url,

    [Parameter(Mandatory)]
    [string] $KeyPath,

    [string] $Out = 'update.json',

    [ValidateSet('stable')]
    [string] $Channel = 'stable'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$core = Join-Path $repo 'artifacts/bin/Chirpdesk.Core/debug/Chirpdesk.Core.dll'

if (-not (Test-Path -LiteralPath $core)) {
    throw "Chirpdesk.Core.dll not found at $core. Run 'dotnet build' first."
}

if (-not (Test-Path -LiteralPath $Installer)) {
    throw "Installer not found: $Installer"
}

if (-not (Test-Path -LiteralPath $KeyPath)) {
    throw "Signing key not found: $KeyPath"
}

Add-Type -Path $core

$payload = Get-Item -LiteralPath $Installer
$hash = (Get-FileHash -LiteralPath $Installer -Algorithm SHA256).Hash.ToLowerInvariant()

$manifest = [Chirpdesk.Core.Updates.UpdateManifest]::new()
$manifest.Version = $Version
$manifest.Channel = $Channel
$manifest.Url = $Url
$manifest.Size = $payload.Length
$manifest.Sha256 = $hash

$canonical = [Chirpdesk.Core.Updates.UpdateManifestCodec]::CanonicalBytes($manifest)

$key = [System.Security.Cryptography.ECDsa]::Create()
try {
    $key.ImportFromPem((Get-Content -LiteralPath $KeyPath -Raw))

    if ($key.KeySize -ne 256) {
        throw "Signing key is $($key.KeySize) bits; a P-256 key is required."
    }

    $signature = $key.SignData(
        $canonical,
        [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)

    $manifest.Signature = [Convert]::ToBase64String($signature)
    $publicPem = $key.ExportSubjectPublicKeyInfoPem()
}
finally {
    $key.Dispose()
}

$json = [ordered]@{
    version = $manifest.Version
    channel = $manifest.Channel
    url = $manifest.Url
    size = $manifest.Size
    sha256 = $manifest.Sha256
    signature = $manifest.Signature
} | ConvertTo-Json -Depth 4
Set-Content -LiteralPath $Out -Value $json -NoNewline

$readError = $null
$parsed = [Chirpdesk.Core.Updates.UpdateManifestCodec]::TryRead($json, [ref]$readError)
if ($null -eq $parsed) {
    throw "Written manifest does not read back: $readError"
}

$verifyError = $null
$acceptable = [Chirpdesk.Core.Updates.UpdateVerifier]::IsAcceptable(
    $parsed, $publicPem, [version]'0.0.0', [ref]$verifyError)
if (-not $acceptable) {
    throw "Written manifest does not verify: $verifyError"
}

$payloadError = $null
if (-not [Chirpdesk.Core.Updates.UpdateVerifier]::VerifyPayload($parsed, $Installer, [ref]$payloadError)) {
    throw "Written manifest does not match its payload: $payloadError"
}

Write-Host "Signed $Version -> $Out"
Write-Host "SHA-256: $hash"
Write-Host "Size: $($payload.Length) bytes"
Write-Host 'Verified with the derived public key and the payload hash.'
