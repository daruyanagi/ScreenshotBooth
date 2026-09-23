using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using ScreenshotBooth.Models;

namespace ScreenshotBooth.Services;

/// <summary>
/// Update checking, per install channel. Ported from Petapeta's UpdateService.
///
/// - Packaged (MSIX): nothing to do; the Store / Windows Update handle it.
/// - Winget: asks winget for the newest version it offers and hands the upgrade to winget.
/// - Zip: looks at the latest GitHub release; the self-update itself lives in
///   <see cref="ZipUpdateRunner"/> / <see cref="ZipUpdater"/> / <see cref="UpdateSwap"/>.
/// </summary>
public static class UpdateService
{
    public const string RepoSlug = "daruyanagi/ScreenshotBooth";
    public const string WingetId = "daruyanagi.ScreenshotBooth";
    public const string LatestReleaseApi = $"https://api.github.com/repos/{RepoSlug}/releases/latest";
    public const string ReleasesPageUrl = $"https://github.com/{RepoSlug}/releases/latest";
    public const string ExecutableName = "ScreenshotBooth.exe";

    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromHours(2);

    // The 15s check timeout would cut a multi-MB download short; downloads are cancelled through
    // their CancellationToken (the Cancel button) instead.
    private static readonly HttpClient CheckHttp = new() { Timeout = TimeSpan.FromSeconds(15) };
    public static readonly HttpClient DownloadHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>Raised (on any thread) when "an update is available" changed.</summary>
    public static event Action? AvailabilityChanged;

    /// <summary>The running version, three parts.</summary>
    public static Version CurrentVersion { get; } = Normalize(
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0));

    /// <summary>The newer tag (e.g. "v1.0.1") known from the last check, or null when this build is current.</summary>
    public static string? AvailableTag
    {
        get
        {
            var cached = SettingsService.Load().CachedLatestVersion;
            return cached is not null
                && Version.TryParse(cached.TrimStart('v', 'V'), out var v)
                && v > CurrentVersion
                ? cached : null;
        }
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    /// <summary>
    /// Checks 5s after start, then every 24h (2h after a failure). The app lives in the tray for
    /// days, so a single check at startup would miss releases.
    /// </summary>
    public static async Task RunBackgroundLoopAsync()
    {
        if (PackageContext.IsPackaged)
        {
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(5));
        while (true)
        {
            var ok = true;
            if (SettingsService.Load().UpdateCheckEnabled)
            {
                ok = await CheckOnceAsync() is not null;
            }
            await Task.Delay(ok ? CheckInterval : RetryInterval);
        }
    }

    /// <summary>One check; records the result. Null when the version could not be fetched (the previous state stays).</summary>
    public static async Task<Version?> CheckOnceAsync()
    {
        try
        {
            var latest = await FetchLatestVersionAsync();
            if (latest is null)
            {
                Trace("UpdateCheck: could not fetch the latest version");
                return null;
            }

            var wasAvailable = AvailableTag is not null;
            var settings = SettingsService.Load();
            settings.CachedLatestVersion = latest > CurrentVersion ? $"v{latest.ToString(3)}" : null;
            settings.LastUpdateCheck = DateTimeOffset.Now;
            SettingsService.Save(settings);
            Trace($"UpdateCheck: current=v{CurrentVersion.ToString(3)} latest=v{latest.ToString(3)} available={AvailableTag is not null}");

            if (wasAvailable != (AvailableTag is not null))
            {
                AvailabilityChanged?.Invoke();
            }
            return latest;
        }
        catch (Exception ex)
        {
            Trace($"UpdateCheck: failed {ex.Message}");
            return null;
        }
    }

    public static Task<Version?> FetchLatestVersionAsync() => PackageContext.CurrentChannel switch
    {
        InstallChannel.Winget => FetchWingetLatestVersionAsync(),
        InstallChannel.Zip => FetchGitHubLatestVersionAsync(),
        _ => Task.FromResult<Version?>(null),
    };

    private static async Task<Version?> FetchWingetLatestVersionAsync()
    {
        var winget = FindWinget();
        if (winget is null)
        {
            return null;
        }

        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = winget,
                Arguments = $"show {WingetId} --versions --disable-interactivity",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (proc is null)
            {
                return null;
            }

            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            if (proc.ExitCode != 0)
            {
                return null;
            }

            // Versions are listed newest-first after a "---" rule.
            var lines = output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var separator = Array.FindIndex(lines, l => l.StartsWith("---"));
            for (var i = separator + 1; separator >= 0 && i < lines.Length; i++)
            {
                if (Version.TryParse(lines[i], out var version))
                {
                    return version;
                }
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<Version?> FetchGitHubLatestVersionAsync()
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
            req.Headers.TryAddWithoutValidation("User-Agent", "ScreenshotBooth");   // required by the GitHub API
            req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");

            using var resp = await CheckHttp.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStreamAsync());
            if (!doc.RootElement.TryGetProperty("tag_name", out var tag))
            {
                return null;
            }

            return Version.TryParse(tag.GetString()?.TrimStart('v', 'V'), out var v) ? v : null;
        }
        catch
        {
            return null;
        }
    }

    public static string? FindWinget()
    {
        var candidate = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", "winget.exe");
        if (File.Exists(candidate))
        {
            return candidate;
        }

        return Environment.GetEnvironmentVariable("PATH")
            ?.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d, "winget.exe"))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Launches a detached `winget upgrade` that first waits for this process to exit (winget
    /// cannot replace a running exe). The caller exits the app right after.
    /// </summary>
    public static void LaunchDetachedWingetUpgrade()
    {
        Trace("UpdateCheck: handing over to winget upgrade and exiting");
        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -Command \"Wait-Process -Id "
                + Environment.ProcessId
                + $" -Timeout 60 -ErrorAction SilentlyContinue; winget upgrade --id {WingetId}\"",
            UseShellExecute = true,
        });
    }

    /// <summary>Update trace, always on: the swap runs in a window-less finisher process, so this is the only witness.</summary>
    public static void Trace(string message) => AppLog.Write("Update: " + message);
}
