<#
.SYNOPSIS
Builds and optionally runs ScreenshotBooth (unpackaged, self-contained WinUI 3 app).

.DESCRIPTION
One command to build and run:  .\BuildAndRun.ps1

- Auto-detects platform (x64/ARM64), defaults to Debug, auto-restores
- Finds MSBuild via vswhere, falls back to dotnet build
- After a successful build, launches ScreenshotBooth.exe straight from the build output folder
  (the app is unpackaged: no MSIX deployment, no `winapp run`, no Developer Mode required)
- Without -Detach the script stays attached until the app exits and reports its exit code
- Pass -SkipRun to build without launching

.EXAMPLE
.\BuildAndRun.ps1 MyApp.csproj                    # Build + run
.\BuildAndRun.ps1 MyApp.csproj -SkipRun           # Build only
.\BuildAndRun.ps1 MyApp.csproj /p:Configuration=Release  # Override config
#>

param(
    [Parameter(Position = 0)]
    [string]$Project,
    [switch]$SkipRun,
    [switch]$Detach,
    [Parameter(ValueFromRemainingArguments)]
    [string[]]$ExtraArgs
)

$ErrorActionPreference = 'Stop'

# Accept --detach (CLI style) as an alias for -Detach (PS style)
if ($ExtraArgs -contains '--detach') {
    $Detach = $true
    $ExtraArgs = $ExtraArgs | Where-Object { $_ -ne '--detach' }
}

# Extra args are MSBuild-style flags like /p:Platform=x64
$extraArgs = $ExtraArgs

# -- 1. Find the .csproj if not specified --
if (-not $Project) {
    $csprojFiles = Get-ChildItem -Path . -Filter "*.csproj" -Depth 0
    if ($csprojFiles.Count -eq 1) {
        $Project = $csprojFiles[0].Name
    } elseif ($csprojFiles.Count -gt 1) {
        Write-Error "Multiple .csproj files found. Specify which one: .\BuildAndRun.ps1 <name>.csproj"
        exit 1
    } else {
        Write-Error "No .csproj file found in current directory."
        exit 1
    }
}

# -- 2. Auto-detect platform --
$detectedPlatform = if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "ARM64" } else { "x64" }
$detectedConfig = "Debug"

$hasPlatform = $extraArgs | Where-Object { $_ -match "^[/|-]p:Platform=" }
$hasConfig = $extraArgs | Where-Object { $_ -match "^[/|-]p:Configuration=" }
$hasRestore = $extraArgs | Where-Object { $_ -match "^[/|-]restore$|^[/|-]t:restore$|^--restore$" }

# Extract actual values if overridden
if ($hasPlatform -and $hasPlatform -match "Platform=(\w+)") { $detectedPlatform = $Matches[1] }
if ($hasConfig -and $hasConfig -match "Configuration=(\w+)") { $detectedConfig = $Matches[1] }

$autoArgs = @()
if (-not $hasPlatform) { $autoArgs += "/p:Platform=$detectedPlatform" }
if (-not $hasConfig)   { $autoArgs += "/p:Configuration=$detectedConfig" }
if (-not $hasRestore)  { $autoArgs += "/restore" }

# -- 3. Find build tool --
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = $null

if (Test-Path $vswhere) {
    $vsPath = & $vswhere -latest -requires Microsoft.Component.MSBuild -property installationPath 2>$null
    if ($vsPath) {
        $candidate = Join-Path $vsPath "MSBuild\Current\Bin\MSBuild.exe"
        if (Test-Path $candidate) { $msbuild = $candidate }
    }
}

# -- 4. Build --
$defaultArgs = @("/nologo")
$hasVerbosity = $extraArgs | Where-Object { $_ -match "^[/|-]v(erbosity)?:" }
if (-not $hasVerbosity) { $defaultArgs += "/v:m" }

# -- 4a. Inject Microsoft.WindowsAppSDK.Analyzers if available --
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
# Look for pre-built analyzer DLL in the skill folder first, then fall back to source tree
$analyzerDll = Join-Path $scriptDir "analyzer\Microsoft.WindowsAppSDK.Analyzers.dll"
$analyzerTargets = Join-Path $scriptDir "analyzer\Microsoft.WindowsAppSDK.Analyzers.targets"
if (-not (Test-Path $analyzerDll)) {
    $analyzerDll = Join-Path $scriptDir "..\..\tools\winui-analyzer\Microsoft.WindowsAppSDK.Analyzers\bin\Release\netstandard2.0\Microsoft.WindowsAppSDK.Analyzers.dll"
    $analyzerTargets = Join-Path $scriptDir "..\..\tools\winui-analyzer\Microsoft.WindowsAppSDK.Analyzers\Microsoft.WindowsAppSDK.Analyzers.targets"
}

$analyzerArgs = @()
$tempBuildProps = $null
if (Test-Path $analyzerDll) {
    $analyzerDll = (Resolve-Path $analyzerDll).Path
    $analyzerTargets = (Resolve-Path $analyzerTargets).Path

    # Inject via temporary Directory.Build.props (works with both MSBuild and dotnet build)
    $projectDir = Split-Path (Resolve-Path $Project) -Parent
    if (-not $projectDir) { $projectDir = "." }
    $tempBuildProps = Join-Path $projectDir "Directory.Build.props"
    $existingProps = $null

    if (Test-Path $tempBuildProps) {
        $existingProps = Get-Content $tempBuildProps -Raw
    }

    # Only create if one doesn't already exist (don't overwrite user's file)
    if (-not $existingProps) {
        @"
<Project>
  <ItemGroup>
    <Analyzer Include="$analyzerDll" />
  </ItemGroup>
  <Import Project="$analyzerTargets" />
</Project>
"@ | Set-Content $tempBuildProps
        Write-Host "--> Microsoft.WindowsAppSDK.Analyzers: enabled" -ForegroundColor DarkGray
    } else {
        $tempBuildProps = $null  # Don't clean up a pre-existing file
        Write-Host "--> Microsoft.WindowsAppSDK.Analyzers: skipped (existing Directory.Build.props)" -ForegroundColor DarkGray
    }
}

Write-Host ""
try {
    if ($msbuild) {
        Write-Host "--> Building with MSBuild (Platform: $detectedPlatform, Config: $detectedConfig)" -ForegroundColor Cyan
        Write-Host "--> MSBuild: $msbuild" -ForegroundColor DarkGray
        $allArgs = $defaultArgs + $autoArgs + @($Project) + $extraArgs
        & $msbuild $allArgs
        $buildExit = $LASTEXITCODE
    } else {
        Write-Host "--> Building with dotnet build (Platform: $detectedPlatform, Config: $detectedConfig)" -ForegroundColor Yellow
        $dotnetArgs = @($Project)
        foreach ($a in ($autoArgs + $extraArgs)) {
            if ($a -match "^[/|-]restore$|^[/|-]t:restore$") {
                # dotnet build restores by default
            } elseif ($a -match "^[/|-]p:(.+)$") {
                $dotnetArgs += "-p:$($Matches[1])"
            } elseif ($a -notmatch "\.(csproj|sln)$") {
                $dotnetArgs += $a
            }
        }
        & dotnet build @dotnetArgs
        $buildExit = $LASTEXITCODE
    }
}
finally {
    # Always clean up the temp Directory.Build.props we created — even on
    # Ctrl-C, throws, or unexpected exits. Otherwise the user's project
    # gets a stray file pointing at our analyzer that subsequent vanilla
    # `dotnet build` invocations will fail to resolve.
    if ($tempBuildProps -and (Test-Path $tempBuildProps)) {
        Remove-Item $tempBuildProps -Force -ErrorAction SilentlyContinue
    }
}

if ($buildExit -ne 0) {
    Write-Host ""
    Write-Host "BUILD FAILED (exit code $buildExit)" -ForegroundColor Red
    exit $buildExit
}

Write-Host ""
Write-Host "BUILD SUCCEEDED" -ForegroundColor Green

# -- 5. Run the unpackaged exe from the build output --
if ($SkipRun) {
    Write-Host "--> Skipping run (-SkipRun)" -ForegroundColor DarkGray
    exit 0
}

# Output folder pattern: bin\<Platform>\<Config>\<tfm>\win-<rid>\
$rid = $detectedPlatform.ToLower()
$projectDir = Split-Path (Resolve-Path $Project) -Parent
if (-not $projectDir) { $projectDir = "." }
$projectName = [System.IO.Path]::GetFileNameWithoutExtension($Project)

$binDir = Join-Path $projectDir "bin\$detectedPlatform\$detectedConfig"
if (-not (Test-Path $binDir)) {
    Write-Host "WARNING: Build output not found at $binDir -- skipping run" -ForegroundColor Yellow
    exit 0
}

$tfmDirs = Get-ChildItem $binDir -Directory | Where-Object { $_.Name -match "^net\d" }
if (-not $tfmDirs) {
    Write-Host "WARNING: No TFM folder found in $binDir -- skipping run" -ForegroundColor Yellow
    exit 0
}

$tfmDir = $tfmDirs | Sort-Object Name -Descending | Select-Object -First 1
$outputDir = Join-Path $tfmDir.FullName "win-$rid"
if (-not (Test-Path $outputDir)) {
    # Try without RID subfolder
    $outputDir = $tfmDir.FullName
}

$exePath = Join-Path $outputDir "$projectName.exe"
if (-not (Test-Path $exePath)) {
    Write-Host "WARNING: $exePath not found -- skipping run" -ForegroundColor Yellow
    exit 0
}

# Working directory = the exe folder, mirroring a user double-clicking the exe in an extracted zip.
Write-Host ""
Write-Host "--> Launching: $exePath" -ForegroundColor Cyan
$proc = Start-Process -FilePath $exePath -WorkingDirectory $outputDir -PassThru
$null = $proc.Handle  # cache the handle so ExitCode is readable after the process exits
Write-Host "$projectName launched (PID: $($proc.Id))" -ForegroundColor Green

if ($Detach) {
    exit 0
}

Write-Host "    The script stays attached while the app is open (Ctrl+C leaves the app running)." -ForegroundColor DarkGray
Wait-Process -Id $proc.Id
Write-Host "$projectName exited (code: $($proc.ExitCode))" -ForegroundColor DarkGray
