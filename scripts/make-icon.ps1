#requires -Version 7.0
<#
.SYNOPSIS
    Renders the Chirpdesk mark from SVG into the multi-size ICO assets used by the app.

.DESCRIPTION
    Source of truth is src/Chirpdesk/Assets/*.svg. ImageMagick (with librsvg) rasterises the
    tile at every Windows icon size; the tray variants stay transparent. Re-run after editing an
    SVG and commit the regenerated ICOs.

.EXAMPLE
    pwsh scripts/make-icon.ps1
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$magick = Get-Command magick -ErrorAction SilentlyContinue
if (-not $magick) {
    throw 'ImageMagick (magick) is required. Install it with: scoop install imagemagick'
}

$magickExe = $magick.Source

$assets = Join-Path (Split-Path -Parent $PSScriptRoot) 'src/Chirpdesk/Assets'

$app = Join-Path $assets 'chirpdesk.svg'
$tray = Join-Path $assets 'chirpdesk-tray.svg'
$unread = Join-Path $assets 'chirpdesk-tray-unread.svg'

if (-not (Test-Path -LiteralPath $app) -or -not (Test-Path -LiteralPath $tray) -or -not (Test-Path -LiteralPath $unread)) {
    throw "Missing SVG source under $assets"
}

& $magickExe -background none $app -resize 256x256 `
    -define 'icon:auto-resize=256,128,64,48,32,24,20,16' `
    (Join-Path $assets 'chirpdesk.ico')

& $magickExe -background none $tray -resize 32x32 `
    -define 'icon:auto-resize=32,24,20,16' `
    (Join-Path $assets 'chirpdesk-tray.ico')

& $magickExe -background none $unread -resize 32x32 `
    -define 'icon:auto-resize=32,24,20,16' `
    (Join-Path $assets 'chirpdesk-tray-unread.ico')

& $magickExe -background none $app -resize 512x512 (Join-Path $assets 'chirpdesk-512.png')
& $magickExe -background none $tray -resize 128x128 (Join-Path $assets 'chirpdesk-tray-preview.png')

Write-Host 'Rendered chirpdesk.ico, chirpdesk-tray.ico, chirpdesk-tray-unread.ico and previews.'
