using System.Diagnostics;

namespace ScreenshotBooth.Services;

/// <summary>
/// Zip-channel self-update, part 2: replacing the install folder. Ported from Petapeta's UpdateSwap.
///
/// A running exe/DLL cannot be overwritten, so the process doing the swap must live OUTSIDE the
/// folder being replaced. Rather than shipping a separate updater binary (an unsigned helper that
/// downloads and rewrites its parent is exactly what Defender flags), the freshly extracted NEW
/// version plays that role:
///
///   old: extract → start new with --finish-update → exit
///   new: wait for old to exit → rename-swap → start from the install folder → exit
///
/// Staging sits next to the install folder so the swap is a rename (no copy across volumes).
/// </summary>
public static class UpdateSwap
{
    public const string FinishArg = "--finish-update";

    // Right after extraction, antivirus scanning holds the new files for a while and renames
    // fail. The finisher has no window, so a long wait never looks like a hang.
    private const int DefaultAttempts = 30;
    private const int DefaultDelayMs = 1000;

    public enum SwapResult
    {
        Succeeded,
        /// <summary>The swap failed and the old version was put back; it still runs.</summary>
        RolledBack,
        /// <summary>Even the rollback failed. Needs a human.</summary>
        Broken,
    }

    /// <summary>
    /// Rename old → .old, then copy staging → install. On failure, restore the old version:
    /// "could not update" is retryable; "updated into a broken install" is not.
    /// </summary>
    public static SwapResult Swap(string installDir, string stagingDir, int attempts = DefaultAttempts, int delayMs = DefaultDelayMs)
    {
        var backup = ZipUpdater.BackupDirFor(installDir);
        var movedAway = false;

        try
        {
            if (Directory.Exists(backup))
            {
                DeleteBestEffort(backup);
            }
            if (Directory.Exists(backup))
            {
                Directory.Move(backup, backup + "." + Guid.NewGuid().ToString("N")[..8]);
            }

            MoveWithRetry(installDir, backup, attempts, delayMs);
            movedAway = true;

            // Copy, not move: this very process runs from the staging folder, so its exe and DLLs
            // are mapped and the folder cannot be renamed - but it can be read.
            CopyDirectory(stagingDir, installDir, attempts, delayMs);

            if (!ZipUpdater.LooksLikeApp(installDir))
            {
                throw new InvalidDataException($"The app is missing after the swap: {installDir}");
            }

            UpdateService.Trace($"UpdateSwap: swapped {installDir}");
            return SwapResult.Succeeded;
        }
        catch (Exception ex)
        {
            UpdateService.Trace($"UpdateSwap: failed {ex}");

            if (!movedAway)
            {
                return SwapResult.RolledBack;   // nothing was touched yet
            }

            try
            {
                // A half-copied install folder must go, or old and new files would mix.
                if (Directory.Exists(installDir))
                {
                    Directory.Delete(installDir, recursive: true);
                }

                MoveWithRetry(backup, installDir, attempts, delayMs);
                UpdateService.Trace("UpdateSwap: rolled back to the previous version");
                return SwapResult.RolledBack;
            }
            catch (Exception restoreEx)
            {
                UpdateService.Trace($"UpdateSwap(rollback): failed {restoreEx}");
                return SwapResult.Broken;
            }
        }
    }

    public static void CopyDirectory(string source, string dest, int attempts = DefaultAttempts, int delayMs = DefaultDelayMs)
    {
        Directory.CreateDirectory(dest);

        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(dest, Path.GetRelativePath(source, dir)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            CopyFileWithRetry(file, Path.Combine(dest, Path.GetRelativePath(source, file)), attempts, delayMs);
        }
    }

    private static void CopyFileWithRetry(string from, string to, int attempts, int delayMs)
    {
        for (var i = 1; ; i++)
        {
            try
            {
                File.Copy(from, to, overwrite: true);
                return;
            }
            catch (Exception ex) when (i < attempts && IsTransient(ex))
            {
                if (i == 1)
                {
                    UpdateService.Trace($"UpdateSwap: {from} is in use; waiting");
                }
                Thread.Sleep(delayMs);
            }
        }
    }

    /// <summary>Directory.Move with patience: scanners and the search indexer let go within seconds.</summary>
    public static void MoveWithRetry(string source, string dest, int attempts = DefaultAttempts, int delayMs = DefaultDelayMs)
    {
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException($"Move source missing: {source}");
        }

        for (var i = 1; ; i++)
        {
            try
            {
                Directory.Move(source, dest);
                if (i > 1)
                {
                    UpdateService.Trace($"UpdateSwap: renamed {source} on attempt {i}");
                }
                return;
            }
            catch (Exception ex) when (i < attempts && IsTransient(ex))
            {
                if (i == 1)
                {
                    UpdateService.Trace($"UpdateSwap: {source} is in use; waiting: {ex.Message}");
                }
                Thread.Sleep(delayMs);
            }
        }
    }

    /// <summary>Only "in use" style failures are worth waiting out; missing/duplicate paths never fix themselves.</summary>
    private static bool IsTransient(Exception ex) => ex switch
    {
        DirectoryNotFoundException => false,
        FileNotFoundException => false,
        UnauthorizedAccessException => true,
        IOException => true,
        _ => false,
    };

    /// <summary>Removes the .old and .update folders a previous update left behind. Best effort, at startup.</summary>
    public static void CleanupBackup(string installDir)
    {
        foreach (var dir in new[] { ZipUpdater.BackupDirFor(installDir), ZipUpdater.StagingDirFor(installDir) })
        {
            if (Directory.Exists(dir) && DeleteBestEffort(dir))
            {
                UpdateService.Trace($"UpdateSwap: cleaned up {dir}");
            }
        }
    }

    private static bool DeleteBestEffort(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
            return true;
        }
        catch (Exception ex)
        {
            // The process that just exited may still hold a file; the next start tries again.
            UpdateService.Trace($"UpdateSwap: could not delete {dir} (will retry next start): {ex.Message}");
            return false;
        }
    }

    public static string[] BuildFinishArgs(string installDir, int waitForPid)
        => [FinishArg, installDir, waitForPid.ToString()];

    public static (string InstallDir, int WaitForPid)? ParseFinishArgs(string[] args)
    {
        if (args.Length < 3 || !string.Equals(args[0], FinishArg, StringComparison.Ordinal))
        {
            return null;
        }
        if (!int.TryParse(args[2], out var pid) || string.IsNullOrWhiteSpace(args[1]))
        {
            return null;
        }
        return (args[1], pid);
    }

    /// <summary>Waits for a process to exit; true when it is gone (or never was), false on timeout.</summary>
    public static async Task<bool> WaitForExitAsync(int pid, TimeSpan timeout, CancellationToken ct = default)
    {
        Process proc;
        try
        {
            proc = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return true;
        }

        using (proc)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeout);
                await proc.WaitForExitAsync(cts.Token);
                return true;
            }
            catch (OperationCanceledException)
            {
                UpdateService.Trace($"UpdateSwap: gave up waiting for PID {pid}");
                return false;
            }
        }
    }

    /// <summary>
    /// The finisher role (this process runs from the staging folder): wait for the old process,
    /// swap, relaunch from the install folder, exit. Never throws.
    /// </summary>
    public static async Task FinishAsync(string installDir, int waitForPid)
    {
        try
        {
            var staging = PackageContext.InstallDirectory;
            UpdateService.Trace($"FinishUpdate: staging={staging} install={installDir} pid={waitForPid}");

            if (await WaitForExitAsync(waitForPid, TimeSpan.FromSeconds(30)))
            {
                UpdateService.Trace($"FinishUpdate: {Swap(installDir, staging)}");
            }
            else
            {
                UpdateService.Trace("FinishUpdate: the old process did not exit; swap skipped");
            }
        }
        catch (Exception ex)
        {
            UpdateService.Trace($"FinishUpdate: failed {ex}");
        }
        finally
        {
            // Even when Broken, launching (and failing) leaves more of a trail than exiting silently.
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = Path.Combine(installDir, UpdateService.ExecutableName),
                    WorkingDirectory = installDir,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                UpdateService.Trace($"FinishUpdate(launch): failed {ex}");
            }
            Environment.Exit(0);
        }
    }
}
