namespace ScreenshotBooth.Services;

/// <summary>
/// Minimal always-on trace to %LOCALAPPDATA%\ScreenshotBooth\Logs\app.log. The booth window is
/// usually hidden in the tray, so failures that would otherwise only reach the status bar are
/// invisible without this.
/// </summary>
public static class AppLog
{
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenshotBooth", "Logs");

    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDir);
                var path = Path.Combine(LogDir, "app.log");
                if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024)
                {
                    File.Delete(path);
                }
                File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
