using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;

namespace ScreenshotBooth.Services;

/// <summary>
/// Zip-channel self-update orchestration: eligibility, then download/verify/extract
/// (<see cref="ZipUpdater"/>) and launching the new version as the finisher
/// (<see cref="UpdateSwap"/>). Ported from Petapeta's ZipUpdateRunner.
/// </summary>
public static class ZipUpdateRunner
{
    public enum Eligibility
    {
        Ok,
        /// <summary>MSIX: the Store / Windows Update own updates.</summary>
        Packaged,
        /// <summary>winget-installed: winget owns updates.</summary>
        ManagedByWinget,
        /// <summary>The install folder (or its parent, where staging goes) is not writable.</summary>
        NotWritable,
    }

    public static Eligibility CheckEligibility(InstallChannel channel, string installDir)
    {
        if (channel == InstallChannel.Packaged) return Eligibility.Packaged;
        if (channel == InstallChannel.Winget) return Eligibility.ManagedByWinget;
        if (!ZipUpdater.CanWriteTo(installDir)) return Eligibility.NotWritable;

        var parent = Path.GetDirectoryName(installDir.TrimEnd(Path.DirectorySeparatorChar));
        return parent is null || !ZipUpdater.CanWriteTo(parent) ? Eligibility.NotWritable : Eligibility.Ok;
    }

    public enum RunResult
    {
        /// <summary>Extracted and the finisher is running: the caller must exit the app now.</summary>
        ReadyToRestart,
        /// <summary>The release has no verifiable zip for this architecture.</summary>
        NotSupported,
        /// <summary>Failed before touching the install folder.</summary>
        Failed,
        Canceled,
    }

    /// <summary>Download → verify → extract → start the finisher. The install folder is untouched until the finisher runs.</summary>
    public static async Task<RunResult> RunAsync(HttpClient http, string installDir, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        try
        {
            var updater = new ZipUpdater(http);

            var json = await updater.DownloadTextAsync(UpdateService.LatestReleaseApi, ct);
            var asset = ZipUpdater.SelectAsset(json, RuntimeInformation.ProcessArchitecture);
            if (asset is null)
            {
                UpdateService.Trace("ZipUpdateRunner: no verifiable asset in the latest release");
                return RunResult.NotSupported;
            }

            var staging = await updater.StageAsync(asset, installDir, progress, ct);

            var args = UpdateSwap.BuildFinishArgs(installDir, Environment.ProcessId);
            Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(staging, UpdateService.ExecutableName),
                WorkingDirectory = staging,
                UseShellExecute = false,
                ArgumentList = { args[0], args[1], args[2] },
            });

            UpdateService.Trace("ZipUpdateRunner: finisher started");
            return RunResult.ReadyToRestart;
        }
        catch (OperationCanceledException)
        {
            UpdateService.Trace("ZipUpdateRunner: canceled");
            return RunResult.Canceled;
        }
        catch (Exception ex)
        {
            UpdateService.Trace($"ZipUpdateRunner: failed {ex}");
            return RunResult.Failed;
        }
    }
}
