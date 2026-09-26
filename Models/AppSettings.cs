namespace ScreenshotBooth.Models;

/// <summary>
/// Persisted user preferences. Serialized to JSON by <see cref="Services.SettingsService"/>
/// at %LOCALAPPDATA%\ScreenshotBooth\settings.json (works identically packaged or unpackaged,
/// unlike ApplicationData.Current.LocalSettings).
/// </summary>
public sealed class AppSettings
{
    /// <summary>Win32 hotkey modifier flags (MOD_WIN | MOD_SHIFT | MOD_NOREPEAT, etc).</summary>
    public uint HotkeyModifiers { get; set; } = HotkeyDefaults.Modifiers;

    /// <summary>Virtual-key code for the hotkey (default: 'B').</summary>
    public uint HotkeyVirtualKey { get; set; } = HotkeyDefaults.VirtualKey;

    /// <summary>Booth area (captured image) width in physical pixels.</summary>
    public int BoothWidth { get; set; } = 1024;

    /// <summary>Booth area (captured image) height in physical pixels.</summary>
    public int BoothHeight { get; set; } = 768;

    /// <summary>Countdown delay in seconds before a capture fires. 0 = off.</summary>
    public int DefaultCountdownSeconds { get; set; } = 0;

    /// <summary>Index into DisplayArea.FindAll() of the display to use. -1 = primary.</summary>
    public int SelectedDisplayIndex { get; set; } = -1;

    /// <summary>Put the target back where (and how big) it was once the booth lets go of it.</summary>
    public bool RestoreTargetLayout { get; set; } = true;

    /// <summary>UI language override (BCP-47, e.g. "ja", "en-US"). Empty follows the system.</summary>
    public string Language { get; set; } = "";

    /// <summary>The booth sizes offered by the size picker. Null = the built-in list (<see cref="TargetSizePreset.BuiltIn"/>).</summary>
    public List<PresetSize>? SizePresets { get; set; }

    /// <summary>Breathing room (DIPs) between the target window and the booth edge.</summary>
    public int MarginDip { get; set; } = DefaultMarginDip;

    public const int DefaultMarginDip = 64;

    /// <summary>How a fresh capture is presented: "Print" (flash + tilted print) or "None" (as-is).</summary>
    public string CaptureEffect { get; set; } = CaptureEffectPrint;

    public const string CaptureEffectPrint = "Print";
    public const string CaptureEffectNone = "None";

    /// <summary>Format preselected in the Save dialog: "Png" or "Jpeg".</summary>
    public string SaveFormat { get; set; } = "Png";

    /// <summary>JPEG quality (1-100) used when saving as JPEG.</summary>
    public int JpegQuality { get; set; } = 90;

    /// <summary>Check GitHub Releases for a newer version in the background (Zip channel only).</summary>
    public bool UpdateCheckEnabled { get; set; } = true;

    /// <summary>The newest tag seen by the last successful check when it was newer than this build (e.g. "v1.0.1"); null otherwise.</summary>
    public string? CachedLatestVersion { get; set; }

    /// <summary>When the last successful update check happened.</summary>
    public DateTimeOffset? LastUpdateCheck { get; set; }
}

/// <summary>Shared default hotkey constants (used by AppSettings and HotkeyService).</summary>
public static class HotkeyDefaults
{
    // MOD_WIN (0x0008) | MOD_SHIFT (0x0004) | MOD_NOREPEAT (0x4000)
    public const uint Modifiers = 0x0008 | 0x0004 | 0x4000;

    // VK_B
    public const uint VirtualKey = 0x42;
}
