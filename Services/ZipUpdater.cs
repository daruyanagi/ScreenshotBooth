using System.IO.Compression;

namespace ScreenshotBooth.Services;

/// <summary>
/// Downloads a GitHub Releases zip asset and extracts it to a staging folder. This pass
/// implements the download+stage step for real; the actual self-swap (replacing the running
/// app's files and relaunching) is left to <see cref="UpdateSwap"/>/<see cref="ZipUpdateRunner"/>
/// as a documented TODO - see those files for why it's a deliberately separate, harder step.
/// </summary>
public static class ZipUpdater
{
    /// <summary>Downloads the release zip and extracts it to a fresh staging directory under %TEMP%.</summary>
    /// <returns>The staging directory path, or null if no downloadable asset was found.</returns>
    public static async Task<string?> DownloadAndStageAsync(UpdateCheckResult result, string executableName, CancellationToken cancellationToken = default)
    {
        if (result.DownloadUrl is null)
        {
            return null;
        }

        var stagingRoot = Path.Combine(Path.GetTempPath(), "ScreenshotBooth-Update", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingRoot);

        var zipPath = Path.Combine(stagingRoot, "update.zip");

        using (var client = new HttpClient())
        using (var response = await client.GetAsync(result.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            await using var fileStream = File.Create(zipPath);
            await response.Content.CopyToAsync(fileStream, cancellationToken);
        }

        var extractDir = Path.Combine(stagingRoot, "extracted");
        ZipFile.ExtractToDirectory(zipPath, extractDir);

        // TODO: verify the extracted package (e.g. checksum or Authenticode signature on
        // executableName) before ever handing it to UpdateSwap - staged content should never be
        // trusted purely because it came from the expected URL.
        _ = executableName;

        return extractDir;
    }
}
