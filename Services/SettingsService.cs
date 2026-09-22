using System.Text.Json;
using ScreenshotBooth.Models;

namespace ScreenshotBooth.Services;

/// <summary>
/// Hand-rolled JSON settings persistence, ported from the Petapeta convention:
/// a POCO serialized to %LOCALAPPDATA%\ScreenshotBooth\settings.json. Deliberately does NOT
/// use ApplicationData.Current.LocalSettings, which throws in unpackaged processes and
/// otherwise ties settings to package identity - this file-based approach works identically
/// whether the app is packaged, unpackaged, or distributed as a self-contained zip.
/// </summary>
public static class SettingsService
{
    private static readonly string SettingsDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenshotBooth");

    private static readonly string SettingsFilePath = Path.Combine(SettingsDirectory, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private static AppSettings? _cached;

    /// <summary>Loads settings from disk, or returns defaults if the file doesn't exist or is corrupt.</summary>
    public static AppSettings Load()
    {
        if (_cached is not null)
        {
            return _cached;
        }

        try
        {
            if (File.Exists(SettingsFilePath))
            {
                var json = File.ReadAllText(SettingsFilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded is not null)
                {
                    _cached = loaded;
                    return loaded;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Corrupt or inaccessible settings file: fall through to defaults rather than crash startup.
        }

        _cached = new AppSettings();
        return _cached;
    }

    /// <summary>Persists the given settings to disk, creating the app-data folder if needed.</summary>
    public static void Save(AppSettings settings)
    {
        _cached = settings;

        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(SettingsFilePath, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort persistence: a failed save shouldn't crash the app.
        }
    }
}
