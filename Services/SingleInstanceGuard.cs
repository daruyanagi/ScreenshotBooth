namespace ScreenshotBooth.Services;

/// <summary>
/// Prevents a second ScreenshotBooth process from fully starting, via a classic named Mutex.
/// This is a minimal stub for this pass: a second instance simply exits immediately rather than
/// activating/notifying the first one. A follow-up could use a named pipe or WM_COPYDATA to
/// forward the second instance's activation (e.g. re-trigger the hotkey flow) to the first.
/// </summary>
public static class SingleInstanceGuard
{
    private const string MutexName = "ScreenshotBooth-SingleInstance";

    // Held for the process lifetime; a static field keeps it from being finalized/released early.
    private static Mutex? _mutex;

    /// <summary>
    /// Attempts to acquire the single-instance lock. Returns true if this is the first (and only)
    /// running instance; false if another instance already holds the lock.
    /// </summary>
    public static bool TryAcquire()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        return createdNew;
    }
}
