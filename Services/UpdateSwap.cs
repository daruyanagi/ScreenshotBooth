namespace ScreenshotBooth.Services;

/// <summary>
/// NOT YET IMPLEMENTED. Placeholder for the self-swap step of a Zip-channel update: a running
/// .exe cannot overwrite its own file on Windows, so this needs to:
///   1. Write a small helper script/exe (or use a detached cmd /c) that waits for this process to
///      exit, then robocopy/move the staged files from ZipUpdater's extraction folder over the
///      current install directory.
///   2. Launch that helper as a detached process (no parent-child lifetime tie).
///   3. Exit this process so the swap can proceed.
///   4. The helper relaunches ScreenshotBooth.exe from the (now updated) install directory and
///      cleans up the staging/helper files.
///
/// Ported structurally from Petapeta's UpdateSwap, but deliberately left unimplemented for this
/// pass per the scaffold scope (a full self-swap needs careful handling of in-use file locks,
/// antivirus false positives on the relaunch helper, and rollback on partial failure - each
/// worth its own follow-up task rather than a rushed first cut).
/// </summary>
public static class UpdateSwap
{
    public static Task ExecuteAsync(string stagedDirectory, string installDirectory, string executableName)
    {
        throw new NotImplementedException(
            "Zip self-swap is not implemented yet. See the TODO in UpdateSwap.cs for the intended " +
            "wait-and-replace design; ZipUpdater.DownloadAndStageAsync already stages the new files.");
    }
}
