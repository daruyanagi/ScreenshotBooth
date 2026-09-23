using System.Runtime.InteropServices;

namespace ScreenshotBooth.Services;

/// <summary>How the running instance of the app was installed/distributed.</summary>
public enum InstallChannel
{
    /// <summary>Running with MSIX package identity (Store, sideloaded MSIX, or `winapp run` dev identity).</summary>
    Packaged,

    /// <summary>Installed by winget (portable zip under %LOCALAPPDATA%\Microsoft\WinGet\Packages). winget owns updates.</summary>
    Winget,

    /// <summary>Unpackaged, extracted from a GitHub Releases zip. The self-update channel.</summary>
    Zip,
}

/// <summary>
/// Detects how this instance of ScreenshotBooth was installed, so the update code can pick the
/// right strategy. Ported from Petapeta's PackageContext. Detected at runtime, not compile time:
/// the same binary reports Packaged when launched with identity and Zip when launched from an
/// extracted folder.
/// </summary>
public static class PackageContext
{
    // kernel32 GetCurrentPackageFullName returns ERROR_INSUFFICIENT_BUFFER (122) when the process
    // has package identity (and reports the required length), or APPMODEL_ERROR_NO_PACKAGE (15700)
    // when it has none.
    private const int AppModelErrorNoPackage = 15700;

    private static InstallChannel? _cached;

    /// <summary>True when the process runs with MSIX package identity; false for the unpackaged zip build.</summary>
    public static bool IsPackaged => CurrentChannel == InstallChannel.Packaged;

    public static InstallChannel CurrentChannel => _cached ??= Detect();

    /// <summary>The folder the running exe lives in, without a trailing separator (the self-update "install dir").</summary>
    public static string InstallDirectory => AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

    private static InstallChannel Detect()
    {
        uint length = 0;
        if (GetCurrentPackageFullName(ref length, null) != AppModelErrorNoPackage)
        {
            return InstallChannel.Packaged;
        }

        // winget's portable installs land under ...\Microsoft\WinGet\Packages\<id>\. Rewriting that
        // folder ourselves would desync winget's installed-version bookkeeping, so leave it to winget.
        var path = Environment.ProcessPath ?? AppContext.BaseDirectory;
        return path.Contains(@"\WinGet\Packages\", StringComparison.OrdinalIgnoreCase)
            ? InstallChannel.Winget
            : InstallChannel.Zip;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, char[]? packageFullName);
}
