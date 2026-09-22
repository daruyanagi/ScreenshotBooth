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

    /// <summary>UI language override (BCP-47, e.g. "ja", "en-US"). Empty follows the system.</summary>
    public string Language { get; set; } = "";

    /// <summary>When true, resizing the booth resizes the target to fill it (minus the shadow margin).</summary>
    public bool FitToBooth { get; set; }
}

/// <summary>Shared default hotkey constants (used by AppSettings and HotkeyService).</summary>
public static class HotkeyDefaults
{
    // MOD_WIN (0x0008) | MOD_SHIFT (0x0004) | MOD_NOREPEAT (0x4000)
    public const uint Modifiers = 0x0008 | 0x0004 | 0x4000;

    // VK_B
    public const uint VirtualKey = 0x42;
}
