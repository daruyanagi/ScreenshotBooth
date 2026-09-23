using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace ScreenshotBooth.Services;

/// <summary>
/// Zip-channel self-update, part 1: download, verify (sha256), extract. Ported from Petapeta's
/// ZipUpdater. Nothing here touches the install folder; <see cref="UpdateSwap"/> does that, from
/// a separate process.
/// </summary>
public sealed class ZipUpdater
{
    private readonly HttpClient _http;

    public ZipUpdater(HttpClient http) => _http = http;

    /// <summary>The release assets an update needs: the zip and its sha256 sidecar.</summary>
    public sealed record Asset(string ZipName, string ZipUrl, string ChecksumUrl);

    // ── pure helpers ───────────────────────────────────────────────────────

    public static string ArchSuffix(Architecture arch) => arch switch
    {
        Architecture.Arm64 => "win-arm64",
        Architecture.X64 => "win-x64",
        _ => throw new PlatformNotSupportedException($"Unsupported architecture: {arch}"),
    };

    /// <summary>
    /// Picks the zip + .sha256 pair for this architecture out of a GitHub release JSON. Null when
    /// the release has no verifiable asset (the app keeps running; the user gets the release page).
    /// </summary>
    public static Asset? SelectAsset(string releaseJson, Architecture arch)
    {
        var suffix = ArchSuffix(arch);
        using var doc = JsonDocument.Parse(releaseJson);
        if (!doc.RootElement.TryGetProperty("assets", out var assets))
        {
            return null;
        }

        string? zipName = null, zipUrl = null, sumUrl = null;
        foreach (var a in assets.EnumerateArray())
        {
            var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
            var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (name is null || url is null || !name.Contains(suffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (name.EndsWith(".zip.sha256", StringComparison.OrdinalIgnoreCase))
            {
                sumUrl = url;
            }
            else if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                zipName = name;
                zipUrl = url;
            }
        }

        // Never extract and launch something that cannot be verified.
        return zipName is null || zipUrl is null || sumUrl is null ? null : new Asset(zipName, zipUrl, sumUrl);
    }

    /// <summary>Reads the hash out of a `sha256sum`-style line ("&lt;hash&gt;  &lt;file&gt;").</summary>
    public static string? ParseChecksum(string content)
    {
        foreach (var line in content.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length == 0)
            {
                continue;
            }

            var hash = t.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)[0];
            if (hash.Length == 64 && hash.All(Uri.IsHexDigit))
            {
                return hash.ToLowerInvariant();
            }
        }
        return null;
    }

    /// <summary>Sanity check that a folder holds the app (not an empty or foreign zip).</summary>
    public static bool LooksLikeApp(string dir)
        => File.Exists(Path.Combine(dir, UpdateService.ExecutableName))
        && File.Exists(Path.Combine(dir, "ScreenshotBooth.dll"));

    /// <summary>
    /// Where the new version is extracted: NEXT TO the install folder ("&lt;install&gt;.update"). Inside
    /// it would be deleted along with the old version; on the same volume the swap is a rename.
    /// </summary>
    public static string StagingDirFor(string installDir)
    {
        var trimmed = installDir.TrimEnd(Path.DirectorySeparatorChar);
        var parent = Path.GetDirectoryName(trimmed)
            ?? throw new InvalidOperationException($"No parent folder for {installDir}");
        return Path.Combine(parent, Path.GetFileName(trimmed) + ".update");
    }

    /// <summary>Where the old version is parked during the swap ("&lt;install&gt;.old").</summary>
    public static string BackupDirFor(string installDir)
        => installDir.TrimEnd(Path.DirectorySeparatorChar) + ".old";

    /// <summary>Whether we can write there (Program Files would need elevation: then no self-update).</summary>
    public static bool CanWriteTo(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, $".screenshotbooth-write-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ── IO ─────────────────────────────────────────────────────────────────

    public static async Task<string> ComputeSha256Async(string path, CancellationToken ct = default)
    {
        await using var fs = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(fs, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Downloads to a file, reporting 0..1 progress when the length is known.</summary>
    public async Task<string> DownloadAsync(string url, string destPath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", "ScreenshotBooth");

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        var total = resp.Content.Headers.ContentLength;
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(destPath);

        var buffer = new byte[81920];
        long read = 0;
        int n;
        while ((n = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n), ct);
            read += n;
            if (total is > 0)
            {
                progress?.Report((double)read / total.Value);
            }
        }
        return destPath;
    }

    public async Task<string> DownloadTextAsync(string url, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", "ScreenshotBooth");
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    /// <summary>Extracts into a fresh folder (a leftover from a failed attempt must not mix in).</summary>
    public static void Extract(string zipPath, string destDir)
    {
        if (Directory.Exists(destDir))
        {
            Directory.Delete(destDir, recursive: true);
        }
        Directory.CreateDirectory(destDir);
        ZipFile.ExtractToDirectory(zipPath, destDir, overwriteFiles: true);
    }

    /// <summary>Download → verify → extract. Returns the staging folder; throws (without extracting) when verification fails.</summary>
    public async Task<string> StageAsync(Asset asset, string installDir, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        // A fresh work folder per call, so two concurrent attempts never step on each other's zip.
        var work = Path.Combine(Path.GetTempPath(), "ScreenshotBooth-Update", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var zipPath = Path.Combine(work, asset.ZipName);

        try
        {
            var expected = ParseChecksum(await DownloadTextAsync(asset.ChecksumUrl, ct))
                ?? throw new InvalidDataException($"Could not read {asset.ZipName}.sha256.");

            await DownloadAsync(asset.ZipUrl, zipPath, progress, ct);

            var actual = await ComputeSha256Async(zipPath, ct);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Downloaded zip hash mismatch. expected={expected} actual={actual}");
            }

            var staging = StagingDirFor(installDir);
            Extract(zipPath, staging);

            if (!LooksLikeApp(staging))
            {
                throw new InvalidDataException($"The extracted zip does not contain the app: {staging}");
            }

            UpdateService.Trace($"ZipUpdater: staged at {staging}");
            return staging;
        }
        finally
        {
            try
            {
                if (Directory.Exists(work))
                {
                    Directory.Delete(work, recursive: true);
                }
            }
            catch (Exception ex)
            {
                UpdateService.Trace($"ZipUpdater: could not delete the temp zip {ex.Message}");
            }
        }
    }
}
