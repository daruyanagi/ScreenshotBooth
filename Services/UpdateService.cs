using System.Net.Http.Headers;
using System.Text.Json;

namespace ScreenshotBooth.Services;

/// <summary>Result of an update check.</summary>
public sealed record UpdateCheckResult(bool IsUpdateAvailable, string? LatestVersion, string? ReleaseUrl, string? DownloadUrl);

/// <summary>
/// Checks GitHub Releases for a newer version and, on the Zip channel, can hand off to
/// <see cref="ZipUpdater"/> to perform the actual update. Ported from Petapeta's UpdateService,
/// parameterized for this app's repo slug and exe name.
///
/// Scope for this pass: version-check is fully implemented; the Zip self-swap download/stage/
/// relaunch dance is stubbed in <see cref="ZipUpdater"/>/<see cref="ZipUpdateRunner"/>/
/// <see cref="UpdateSwap"/> with clear TODOs (see those files) rather than fully ported, to keep
/// this pass's scope manageable. Packaged/Winget channels are recognized but not wired to any
/// update mechanism here - the Store and winget already manage updates for those channels.
/// </summary>
public static class UpdateService
{
    // TODO: point this at the real GitHub repo once ScreenshotBooth is published (owner/repo).
    private const string RepoSlug = "your-org/ScreenshotBooth";

    private const string ExecutableName = "ScreenshotBooth.exe";

    public static async Task<UpdateCheckResult> CheckForUpdateAsync(Version currentVersion, CancellationToken cancellationToken = default)
    {
        if (PackageContext.CurrentChannel is InstallChannel.Packaged or InstallChannel.Winget)
        {
            // Store/winget own updates for these channels; nothing for this app to do.
            return new UpdateCheckResult(false, null, null, null);
        }

        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ScreenshotBooth", currentVersion.ToString()));
            client.Timeout = TimeSpan.FromSeconds(10);

            var json = await client.GetStringAsync($"https://api.github.com/repos/{RepoSlug}/releases/latest", cancellationToken);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tagName = root.GetProperty("tag_name").GetString() ?? "";
            var releaseUrl = root.TryGetProperty("html_url", out var htmlUrlEl) ? htmlUrlEl.GetString() : null;

            var versionText = tagName.TrimStart('v', 'V');
            var downloadUrl = FindZipAssetUrl(root);

            if (Version.TryParse(versionText, out var latest) && latest > currentVersion)
            {
                return new UpdateCheckResult(true, versionText, releaseUrl, downloadUrl);
            }

            return new UpdateCheckResult(false, versionText, releaseUrl, downloadUrl);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            // Network hiccup or API shape change: treat as "no update available" rather than crash.
            return new UpdateCheckResult(false, null, null, null);
        }
    }

    private static string? FindZipAssetUrl(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets))
        {
            return null;
        }

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
            if (name is not null && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                return asset.GetProperty("browser_download_url").GetString();
            }
        }

        return null;
    }

    /// <summary>Kicks off the full Zip-channel update (download, stage, self-swap, relaunch).</summary>
    public static Task ApplyZipUpdateAsync(UpdateCheckResult result, CancellationToken cancellationToken = default) =>
        ZipUpdater.DownloadAndStageAsync(result, ExecutableName, cancellationToken);
}
