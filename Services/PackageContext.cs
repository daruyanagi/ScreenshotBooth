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
    private static InstallChannel? _cached;

    public static InstallChannel CurrentChannel => _cached ??= Detect();

    private static InstallChannel Detect()
    {
        try
        {
            // Throws InvalidOperationException/COMException when running without package identity.
            var _ = Windows.ApplicationModel.Package.Current.Id.FullName;
            return InstallChannel.Packaged;
        }
        catch
        {
            // Unpackaged. A future Winget (MSIX-lite/portable) install could drop a marker file or
            // registry key next to the exe to distinguish itself from a plain zip extraction; for
            // now, any unpackaged instance is treated as the Zip channel.
            return InstallChannel.Zip;
        }
    }
}
