<#
.SYNOPSIS
    First-time winget submission for daruyanagi.ScreenshotBooth (later versions are submitted
    automatically by release.yml through winget-releaser).

.DESCRIPTION
    Renders winget\*.yaml.template for a published GitHub Release: fills in the version, the
    release date and the SHA256 of the win-x64 / win-arm64 zips (read from the .sha256 sidecars
    on the release), validates the manifests with `winget validate`, and - unless -DryRun -
    opens the pull request to microsoft/winget-pkgs with `wingetcreate submit` (which asks for
    GitHub sign-in the first time; no token is stored here).

.PARAMETER Version
    The released version, x.y.z (the tag is v<Version>).

.PARAMETER DryRun
    Render and validate only; print the manifest folder instead of submitting.

.EXAMPLE
    .\Tools\Submit-Winget.ps1 -Version 1.1.0 -DryRun
    .\Tools\Submit-Winget.ps1 -Version 1.1.0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Version,
    [switch] $DryRun
)

$ErrorActionPreference = 'Stop'
$repo = 'daruyanagi/ScreenshotBooth'
$id = 'daruyanagi.ScreenshotBooth'
$root = Split-Path $PSScriptRoot -Parent
$templates = Join-Path $root 'winget'
$out = Join-Path $root "winget\out\$Version"
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must be x.y.z: $Version" }

function Step($msg) { Write-Host "`n=== $msg ===" -ForegroundColor Cyan }

Step "Release v$Version"
$release = gh release view "v$Version" --repo $repo --json publishedAt,assets | ConvertFrom-Json
if (-not $release) { throw "Release v$Version not found on $repo" }
$date = ([DateTime]$release.publishedAt).ToString('yyyy-MM-dd')

function Sha($rid) {
    $name = "ScreenshotBooth-v$Version-$rid.zip.sha256"
    if (-not ($release.assets.name -contains $name)) { throw "Release asset missing: $name" }
    $tmp = Join-Path ([IO.Path]::GetTempPath()) $name
    gh release download "v$Version" --repo $repo --pattern $name --output $tmp --clobber | Out-Null
    $hash = ((Get-Content $tmp -Raw) -split '\s+')[0].ToUpperInvariant()
    if ($hash.Length -ne 64) { throw "Bad sha256 in $name" }
    return $hash
}
$shaX64 = Sha 'win-x64'
$shaArm64 = Sha 'win-arm64'
Write-Host "date=$date`nx64=$shaX64`narm64=$shaArm64"

Step 'Render manifests'
New-Item -ItemType Directory -Force $out | Out-Null
foreach ($t in Get-ChildItem $templates -Filter '*.yaml.template') {
    $text = (Get-Content $t.FullName -Raw).
        Replace('{{VERSION}}', $Version).Replace('{{DATE}}', $date).
        Replace('{{SHA_X64}}', $shaX64).Replace('{{SHA_ARM64}}', $shaArm64)
    $dest = Join-Path $out ($t.Name -replace '\.template$', '')
    [IO.File]::WriteAllText($dest, $text, (New-Object Text.UTF8Encoding $false))
    Write-Host "-> $dest"
}

Step 'winget validate'
winget validate --manifest $out
if ($LASTEXITCODE -ne 0) { throw 'winget validate failed' }

if ($DryRun) {
    Write-Host "`nDry run: manifests are in $out (not submitted)." -ForegroundColor Yellow
    return
}

Step 'Submit to microsoft/winget-pkgs'
# wingetcreate signs in with GitHub (device flow) when no token is cached; the PR is opened from
# your fork of winget-pkgs, exactly like Petapeta / XTimelineViewer.
wingetcreate submit --prtitle "New package: $id version $Version" $out
if ($LASTEXITCODE -ne 0) { throw 'wingetcreate submit failed' }
Write-Host "`nSubmitted. Once the PR is merged, later releases are published by release.yml (WINGET_TOKEN)." -ForegroundColor Green
