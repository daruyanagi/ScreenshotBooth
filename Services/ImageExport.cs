using System.Drawing;
using System.Drawing.Imaging;

namespace ScreenshotBooth.Services;

/// <summary>Turns the capture's PNG bytes into the bytes to write for the chosen save format.</summary>
public static class ImageExport
{
    public const string Png = "Png";
    public const string Jpeg = "Jpeg";

    /// <summary>File-name stem for a capture taken now, e.g. "ScreenshotBooth 2026-09-23 14-30-05".</summary>
    public static string SuggestedFileName(DateTime when) => $"ScreenshotBooth {when:yyyy-MM-dd HH-mm-ss}";

    public static byte[] Encode(byte[] pngBytes, string format, int jpegQuality)
    {
        if (!string.Equals(format, Jpeg, StringComparison.OrdinalIgnoreCase))
        {
            return pngBytes;
        }

        using var source = new MemoryStream(pngBytes);
        using var image = Image.FromStream(source);
        var encoder = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, (long)Math.Clamp(jpegQuality, 1, 100));

        using var output = new MemoryStream();
        image.Save(output, encoder, parameters);
        return output.ToArray();
    }
}
