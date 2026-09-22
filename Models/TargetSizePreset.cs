namespace ScreenshotBooth.Models;

/// <summary>A selectable booth-area size in the toolbar's size picker.</summary>
public sealed record TargetSizePreset(string Name, int Width, int Height, bool IsCustom = false, bool Overflows = false)
{
    /// <summary>"4:3"-style ratio; falls back to "1.71:1" when the reduced integers get unwieldy.</summary>
    public string AspectRatio
    {
        get
        {
            // Sizes derived from a window plus margins are rarely exact; snap to the familiar ratios.
            var ratio = (double)Width / Height;
            foreach (var (a, b) in new[] { (4, 3), (16, 9), (16, 10), (3, 2), (1, 1), (5, 4), (21, 9) })
            {
                if (Math.Abs(ratio - (double)a / b) < 0.01)
                {
                    return $"{a}:{b}";
                }
            }

            var g = Gcd(Width, Height);
            var ra = Width / g;
            var rb = Height / g;
            return ra <= 32 && rb <= 32 ? $"{ra}:{rb}" : $"{ratio:0.00}:1";
        }
    }

    /// <summary>Marked with an exclamation when the held target (plus margin) would not fit this size.</summary>
    public override string ToString() =>
        (Overflows ? "\u2757 " : "") + (IsCustom ? $"{Name} ({Width}x{Height}, {AspectRatio})" : $"{Width}x{Height} ({AspectRatio})");

    private static int Gcd(int a, int b) => b == 0 ? Math.Max(a, 1) : Gcd(b, a % b);

    public bool SameSizeAs(TargetSizePreset other) => Width == other.Width && Height == other.Height && IsCustom == other.IsCustom;

    public static IReadOnlyList<TargetSizePreset> BuiltIn { get; } =
    [
        new("540x405", 540, 405),
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
