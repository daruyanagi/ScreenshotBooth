namespace ScreenshotBooth.Services;

/// <summary>
/// NOT YET IMPLEMENTED. Intended orchestrator that ties the update flow together for the Zip
/// channel: UpdateService.CheckForUpdateAsync -> ZipUpdater.DownloadAndStageAsync -> (on user
/// confirmation) UpdateSwap.ExecuteAsync -> process exit. Left as a seam/TODO for this pass,
/// alongside UpdateSwap - see that file for the self-swap design notes.
/// </summary>
public static class ZipUpdateRunner
{
    public static async Task RunAsync(Version currentVersion, CancellationToken cancellationToken = default)
    {
        var checkResult = await UpdateService.CheckForUpdateAsync(currentVersion, cancellationToken);
        if (!checkResult.IsUpdateAvailable)
        {
            return;
        }

        // TODO: surface an InfoBar/dialog asking the user to confirm before downloading, then:
        //   var staged = await ZipUpdater.DownloadAndStageAsync(checkResult, "ScreenshotBooth.exe", cancellationToken);
        //   if (staged is not null) await UpdateSwap.ExecuteAsync(staged, AppContext.BaseDirectory, "ScreenshotBooth.exe");
        throw new NotImplementedException("Zip update apply flow is not wired up yet - see TODOs in this method.");
    }
}
