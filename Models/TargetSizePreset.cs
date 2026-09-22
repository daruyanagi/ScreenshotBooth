namespace ScreenshotBooth.Models;

/// <summary>A selectable booth-area size in the toolbar's size picker.</summary>
public sealed record TargetSizePreset(string Name, int Width, int Height, bool IsCustom = false, bool Overflows = false)
{
    /// <summary>"4:3"-style ratio; falls back to "1.71:1" when the reduced integers get unwieldy.</summary>
    public string AspectRatio
    {
        get
        {
            var g = Gcd(Width, Height);
            var a = Width / g;
            var b = Height / g;
            return a <= 32 && b <= 32 ? $"{a}:{b}" : $"{(double)Width / Height:0.00}:1";
        }
    }

    /// <summary>Marked with an exclamation when the held target (plus margin) would not fit this size.</summary>
    public override string ToString() =>
        (Overflows ? "\u2757 " : "") + (IsCustom ? $"{Name} ({Width}x{Height}, {AspectRatio})" : $"{Width}x{Height} ({AspectRatio})");

    private static int Gcd(int a, int b) => b == 0 ? Math.Max(a, 1) : Gcd(b, a % b);

    public bool SameSizeAs(TargetSizePreset other) => Width == other.Width && Height == other.Height && IsCustom == other.IsCustom;

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
