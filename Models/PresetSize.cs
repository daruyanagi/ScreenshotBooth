namespace ScreenshotBooth.Models;

/// <summary>A user-defined booth size (physical pixels), as stored in settings.</summary>
public sealed class PresetSize
{
    public const int Min = 100;
    public const int Max = 8192;

    public int Width { get; set; }
    public int Height { get; set; }

    public PresetSize()
    {
    }

    public PresetSize(int width, int height)
    {
        Width = width;
        Height = height;
    }

    public static bool IsValid(int width, int height) =>
        width is >= Min and <= Max && height is >= Min and <= Max;
}
