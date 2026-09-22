using Microsoft.Windows.ApplicationModel.Resources;

namespace ScreenshotBooth;

/// <summary>Shortcut for localized strings (Strings\*\Resources.resw).</summary>
public static class R
{
    private static readonly ResourceLoader Loader = new();

    public static string Get(string key) => Loader.GetString(key);

    public static string F(string key, params object[] args) =>
        string.Format(Loader.GetString(key), args);
}
