using System.Runtime.InteropServices;

namespace ScreenshotBooth.Services;

/// <summary>How the running instance of the app was installed/distributed.</summary>
public enum InstallChannel
{
    /// <summary>Could not be determined.</summary>
    Unknown,

    /// <summary>Running with MSIX package identity (Store, sideloaded MSIX, or `winapp run` dev identity).</summary>
    Packaged,

    /// <summary>Installed via `winget install` (MSIX or portable manifest). Not yet implemented - see UpdateService.</summary>
    Winget,

    /// <summary>Unpackaged, extracted from a GitHub Releases zip. The only fully-implemented update channel.</summary>
    Zip,
}

/// <summary>
/// Detects how this instance of ScreenshotBooth was installed, so UpdateService can pick the
/// right update strategy. Ported from Petapeta's PackageContext/InstallChannel seam. Only the Zip
/// channel's updater is fully implemented in this pass; Packaged/Winget are recognized but their
/// update paths are placeholders (Store/winget handle their own updates outside this app anyway).
/// </summary>
public static class PackageContext
{
    // kernel32 GetCurrentPackageFullName returns ERROR_INSUFFICIENT_BUFFER (122) when the process
    // has package identity (and reports the required length), or APPMODEL_ERROR_NO_PACKAGE (15700)
    // when it has none. Detected at runtime, not compile time: the same binary reports Packaged
    // when launched with identity and Zip when launched from an extracted folder.
    private const int AppModelErrorNoPackage = 15700;

    private static InstallChannel? _cached;

    /// <summary>True when the process runs with MSIX package identity; false for the unpackaged zip build.</summary>
    public static bool IsPackaged => CurrentChannel == InstallChannel.Packaged;

    public static InstallChannel CurrentChannel => _cached ??= Detect();

    private static InstallChannel Detect()
    {
        uint length = 0;
        var result = GetCurrentPackageFullName(ref length, null);

        if (result == AppModelErrorNoPackage)
        {
            // Unpackaged. A future Winget (MSIX-lite/portable) install could drop a marker file or
            // registry key next to the exe to distinguish itself from a plain zip extraction; for
            // now, any unpackaged instance is treated as the Zip channel.
            return InstallChannel.Zip;
        }

        return InstallChannel.Packaged;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, char[]? packageFullName);
}
