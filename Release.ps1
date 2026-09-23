<#
.SYNOPSIS
    Bumps the version for a release (and optionally builds the release artifacts locally).

.DESCRIPTION
    Distribution is a GitHub Release (zips + unsigned MSIX), produced by CI
    (.github/workflows/release.yml) when a v* tag is pushed. This script does the part that has
    to happen before the tag: updating <Version> in the csproj and the Identity version in
    Package.appxmanifest so the tag, the assembly version (which the in-app update check compares
    against) and the package agree.

.PARAMETER Version
    New version, x.y.z. Omitted: the csproj's current value is used and nothing is modified.

.PARAMETER WithArtifacts
    Also publish x64/arm64 zips (+ .sha256) and the unsigned MSIX into publish\release\ for a
    local check. Normally unnecessary; CI builds the real ones.

.EXAMPLE
    .\Release.ps1 -Version 1.0.1
    .\Release.ps1 -Version 1.0.1 -WithArtifacts
#>
[CmdletBinding()]
param(
    [string] $Version,
    [switch] $WithArtifacts
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$proj = Join-Path $root 'ScreenshotBooth.csproj'
$manifest = Join-Path $root 'Package.appxmanifest'
$outDir = Join-Path $root 'publish\release'

function Step($msg) { Write-Host "`n=== $msg ===" -ForegroundColor Cyan }

Step 'Version'
if ($Version) {
    if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must be x.y.z: $Version" }
    (Get-Content $proj -Raw) -replace '<Version>[\d.]+</Version>', "<Version>$Version</Version>" |
        Set-Content $proj -Encoding utf8 -NoNewline
    # Only the Identity version (not the XML declaration's version="1.0" or MinVersion).
    (Get-Content $manifest -Raw) -replace '(<Identity[^>]*?Version=")[\d.]+(")', "`${1}$Version.0`${2}" |
        Set-Content $manifest -Encoding utf8 -NoNewline
    Write-Host "csproj / appxmanifest -> $Version"
} elseif ((Get-Content $proj -Raw) -match '<Version>([\d.]+)</Version>') {
    $Version = $Matches[1]
} else {
    throw 'No <Version> in the csproj'
}
Write-Host "Version: $Version"

if ($WithArtifacts) {
    Remove-Item $outDir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $outDir | Out-Null
    try { Stop-Process -Name ScreenshotBooth -Force -ErrorAction Stop } catch {}

    foreach ($rid in 'win-x64', 'win-arm64') {
        Step "Publish $rid"
        dotnet publish $proj -c Release -p:PublishProfile=$rid
        if ($LASTEXITCODE -ne 0) { throw "publish failed: $rid" }
        $zip = Join-Path $outDir "ScreenshotBooth-v$Version-$rid.zip"
        Compress-Archive -Path (Join-Path $root "bin\publish\$rid\*") -DestinationPath $zip -Force
        $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
        Set-Content -Path "$zip.sha256" -Value "$hash  $(Split-Path $zip -Leaf)" -NoNewline
        Write-Host "-> $zip"
    }

    Step 'MSIX (unsigned)'
    msbuild $proj -restore -p:Configuration=Release -p:Platform=x64 -p:Packaged=true -v:m
    if ($LASTEXITCODE -ne 0) { throw 'MSIX build failed' }
    $msix = Get-ChildItem (Join-Path $root 'bin\msix') -Recurse -Filter 'ScreenshotBooth_*.msix' | Select-Object -First 1
    if ($msix) { Copy-Item $msix.FullName (Join-Path $outDir "ScreenshotBooth-v$Version-x64-unsigned.msix"); Write-Host "-> $($msix.Name)" }

    Step 'Artifacts'
    Get-ChildItem $outDir | Select-Object Name, @{N = 'MB'; E = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize
}

Write-Host @"
Next (manual):
  1. Commit the version bump and push
  2. git tag v$Version && git push origin v$Version
     -> CI builds the zips, checksums and MSIX and creates the GitHub Release
"@ -ForegroundColor Yellow
