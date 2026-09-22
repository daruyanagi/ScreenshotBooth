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

## Publish (release zip)

```powershell
dotnet publish -c Release -p:PublishProfile=win-x64     # -> bin\publish\win-x64\
dotnet publish -c Release -p:PublishProfile=win-arm64   # -> bin\publish\win-arm64\
```

Zip the contents of the output folder as-is; the `.exe`, the .NET runtime and the Windows App
Runtime DLLs (`Microsoft.WindowsAppRuntime.dll`, `Microsoft.ui.xaml.dll`, ...) are all inside it.
The profiles live in `Properties\PublishProfiles\` (self-contained, ReadyToRun, untrimmed, no single-file).

## Packaged builds (future MSIX / Store / winget)

The project defaults to `WindowsPackageType=None`. `Package.appxmanifest` is kept in the repo but is
not part of the unpackaged build. To build the packaged variant instead:

```powershell
dotnet build -p:Platform=x64 -p:WindowsPackageType=MSIX -p:WindowsAppSDKSelfContained=false
```

`Services\PackageContext.cs` detects package identity at runtime (`GetCurrentPackageFullName`) and
reports `InstallChannel.Packaged` / `InstallChannel.Zip` accordingly.
