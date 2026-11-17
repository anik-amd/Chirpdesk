#requires -Version 7.0
<#
.SYNOPSIS
    Creates an ECDSA P-256 release key for update manifests.

.DESCRIPTION
    Writes the private key as PKCS#8 PEM and prints the public SPKI PEM plus its SHA-256
    fingerprint. Keep the private key off-repo and on a machine that is not used for browsing;
    paste the public key into the app's pinned key constant and the release workflow secret.

.EXAMPLE
    pwsh scripts/new-update-key.ps1 -Path D:\keys\chirpdesk-update.pem
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Path,

    # Optional copy of the public half, for the pinned-key step.
    [string] $PublicPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (Test-Path -LiteralPath $Path) {
    throw "$Path already exists; refusing to overwrite a signing key."
}

$directory = Split-Path -Parent $Path
if ($directory) {
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
}

$key = [System.Security.Cryptography.ECDsa]::Create()
try {
    $key.GenerateKey([System.Security.Cryptography.ECCurve+NamedCurves]::nistP256)

    $privatePem = $key.ExportPkcs8PrivateKeyPem()
    $publicPem = $key.ExportSubjectPublicKeyInfoPem()
    $der = $key.ExportSubjectPublicKeyInfo()
    $fingerprint = [Convert]::ToHexString(
        [System.Security.Cryptography.SHA256]::HashData($der)).ToLowerInvariant()

    Set-Content -LiteralPath $Path -Value $privatePem -NoNewline
    if ($PublicPath) {
        Set-Content -LiteralPath $PublicPath -Value $publicPem -NoNewline
    }

    Write-Host "Private key: $Path (PKCS#8 PEM, keep off-repo)"
    Write-Host "Fingerprint: ecdsa-p256:$($fingerprint.Substring(0, 16))"
    Write-Host ''
    Write-Host 'Public key (SPKI PEM):'
    Write-Host $publicPem
}
finally {
    $key.Dispose()
}
