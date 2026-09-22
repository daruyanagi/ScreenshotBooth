namespace ScreenshotBooth.Models;

/// <summary>A selectable booth-area size in the toolbar's size picker.</summary>
public sealed record TargetSizePreset(string Name, int Width, int Height, bool IsCustom = false)
{
    public override string ToString() => $"{Name} ({Width}x{Height})";

    public static IReadOnlyList<TargetSizePreset> BuiltIn { get; } =
    [
        new("640x480", 640, 480),
        new("800x600", 800, 600),
        new("1024x600", 1024, 600),
        new("1024x768", 1024, 768),
        new("1280x720", 1280, 720),
        new("1920x1080", 1920, 1080),
    ];

    /// <summary>The size the booth was given automatically when the target was acquired, so it can be restored.</summary>
    public static TargetSizePreset Custom(int width, int height) => new(R.Get("PresetCustom"), width, height, IsCustom: true);
}
