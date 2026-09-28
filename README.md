# ScreenshotBooth

WinUI 3 / Windows App SDK desktop app. Ships as a standalone **unpackaged, self-contained** zip
(extract and run `ScreenshotBooth.exe`; no .NET or Windows App Runtime install required).
Settings live in `%LOCALAPPDATA%\ScreenshotBooth\settings.json`.

## Build

Prerequisites: .NET 10 SDK on Windows 10 1809+ (Visual Studio is optional; the build also works with plain `dotnet`).

```powershell
# Dev inner loop: build Debug for the host architecture and launch the exe from the build output
.\BuildAndRun.ps1            # stays attached until the app exits
.\BuildAndRun.ps1 -Detach    # launch and return
.\BuildAndRun.ps1 -SkipRun   # build only

# Plain build
dotnet build -p:Platform=x64
dotnet build -p:Platform=ARM64
```

## Icons

Both icon sets are generated, not hand-drawn. Re-run the scripts after editing them:

```powershell
pwsh Tools/Generate-AppIcon.ps1   # Assets/AppIcon.svg (master), AppIcon.ico, Square*/Store/Splash PNGs
pwsh Tools/Generate-Icons.ps1     # Assets/Icons/IconPaths.xaml (badged toolbar PathIcons)
```

## Release

Releases are built by CI from a `v*` tag ([.github/workflows/release.yml](.github/workflows/release.yml)):
self-contained zips for win-x64 / win-arm64 with `.sha256` sidecars, plus an unsigned MSIX.

```powershell
.\Release.ps1 -Version 1.0.1        # bumps <Version> in the csproj and Package.appxmanifest
git commit -am "Release 1.0.1"; git push
git tag v1.0.1; git push origin v1.0.1
```

The tag must match the csproj `<Version>` (the workflow checks). `.\Release.ps1 -Version x.y.z -WithArtifacts`
builds the same artifacts locally into `publish\release\` for a dry run.

Manual publish of one zip:

```powershell
dotnet publish -c Release -p:PublishProfile=win-x64     # -> bin\publish\win-x64\
dotnet publish -c Release -p:PublishProfile=win-arm64   # -> bin\publish\win-arm64\
```

Zip the contents of the output folder as-is; the `.exe`, the .NET runtime and the Windows App
Runtime DLLs are all inside it. The profiles live in `Properties\PublishProfiles\` (self-contained,
ReadyToRun, untrimmed, no single-file).

## Updates

`Services\UpdateService.cs` checks the latest GitHub release (5 s after start, then daily) and the
settings page offers "Update and restart" on the zip channel: the zip for the running architecture is
downloaded, verified against its `.sha256`, extracted **next to** the install folder
(`<install>.update`), and the new exe is started with `--finish-update <install> <pid>`. That new
process waits for the old one to exit, renames the old folder to `<install>.old`, copies itself into
place and relaunches; leftovers are cleaned up on the next start (`UpdateSwap.cs`). Releases without
`.sha256` sidecars are not self-updated (the release page is offered instead). Installs under
`...\WinGet\Packages\` hand over to `winget upgrade`; packaged builds leave updates to the Store.

## Packaged build (MSIX / Store / winget)

```powershell
msbuild ScreenshotBooth.csproj -restore -p:Configuration=Release -p:Platform=x64 -p:Packaged=true   # -> bin\msix\
```

`-p:Packaged=true` switches to `WindowsPackageType=MSIX` with the Windows App Runtime as a framework
dependency and produces an **unsigned** `.msix` (the Store signs on submission). To sideload it, sign
with your own certificate (`signtool sign /fd SHA256 /a /f cert.pfx /p ... *.msix`) and install the
certificate on the target machine. `Package.appxmanifest` carries placeholder identity values; replace
`Identity/@Name` and `@Publisher` with the ones Partner Center assigns before a Store submission.
`Services\PackageContext.cs` detects package identity at runtime and reports
`InstallChannel.Packaged` / `Winget` / `Zip`.

### winget

Same recipe as Petapeta / XTimelineViewer: the release zips are the installers
(`InstallerType: zip`, `NestedInstallerType: portable`, alias `screenshotbooth`), identifier
`daruyanagi.ScreenshotBooth`.

- First submission (once): after the release is published, `.\Tools\Submit-Winget.ps1 -Version x.y.z`
  renders `winget\*.yaml.template` with the release's SHA256 sidecars, runs `winget validate`, and
  opens the PR to microsoft/winget-pkgs with `wingetcreate submit` (add `-DryRun` to only render).
- Every later release: `release.yml` runs winget-releaser when the `WINGET_TOKEN` secret is set
  (`gh secret set WINGET_TOKEN --repo daruyanagi/ScreenshotBooth`; the same token as the other
  repos, `public_repo` scope on the winget-pkgs fork). Without the secret the steps are skipped.
- `winget install daruyanagi.ScreenshotBooth` installs into `%LOCALAPPDATA%\Microsoft\WinGet\Packages\`;
  the app detects that path as the winget channel and hands updates to `winget upgrade`.
