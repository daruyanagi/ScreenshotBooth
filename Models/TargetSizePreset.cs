namespace ScreenshotBooth.Models;

/// <summary>A selectable target-window size in the toolbar's size picker.</summary>
public sealed record TargetSizePreset(string Name, int Width, int Height, bool IsCustom = false)
{
    public override string ToString() => IsCustom ? Name : $"{Name} ({Width}x{Height})";

    public static IReadOnlyList<TargetSizePreset> BuiltIn { get; } =
    [
        new("800x600", 800, 600),
        new("1024x600", 1024, 600),
        new("1024x768", 1024, 768),
        new("1280x720", 1280, 720),
        new("1920x1080", 1920, 1080),
        new("Custom", 0, 0, IsCustom: true),
    ];
}
