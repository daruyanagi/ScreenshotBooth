using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace ScreenshotBooth.Services;

/// <summary>
/// Remembers, on disk, the one window this app made always-on-top (only when it was not on top by
/// itself). If the process dies without releasing it (crash, kill, power loss), the next run finds
/// the record, checks that it still points at the very same window, and takes the flag off again.
/// </summary>
public static class HeldTargetRecord
{
    private sealed class Model
    {
        public long Hwnd { get; set; }
        public int ProcessId { get; set; }
        public DateTime? ProcessStartTime { get; set; }
        public string Title { get; set; } = "";
    }

    private static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenshotBooth", "held-target.json");

    public static void Save(nint hwnd, string title)
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            DateTime? started = null;
            try
            {
                started = Process.GetProcessById((int)pid).StartTime;
            }
            catch
            {
                // Elevated or gone; the PID alone is still a reasonable (if weaker) match.
            }

            var model = new Model { Hwnd = hwnd, ProcessId = (int)pid, ProcessStartTime = started, Title = title };
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            System.IO.File.WriteAllText(Path, JsonSerializer.Serialize(model));
        }
        catch (Exception ex)
        {
            AppLog.Write($"HeldTargetRecord: save failed {ex.Message}");
        }
    }

    public static void Clear()
    {
        try
        {
            if (System.IO.File.Exists(Path))
            {
                System.IO.File.Delete(Path);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"HeldTargetRecord: clear failed {ex.Message}");
        }
    }

    /// <summary>
    /// If a record from a previous run still points at the same window and it is still on top,
    /// takes the flag off and returns the window's title; otherwise returns null. The record is
    /// removed either way.
    /// </summary>
    public static string? TryRecover()
    {
        Model? model;
        try
        {
            if (!System.IO.File.Exists(Path))
            {
                return null;
            }
            model = JsonSerializer.Deserialize<Model>(System.IO.File.ReadAllText(Path));
        }
        catch (Exception ex)
        {
            AppLog.Write($"HeldTargetRecord: read failed {ex.Message}");
            Clear();
            return null;
        }
        Clear();
        if (model is null)
        {
            return null;
        }

        var hwnd = (nint)model.Hwnd;
        if (!IsWindow(hwnd))
        {
            return null;
        }

        // HWND values get reused, so insist on the same process (and, when readable, its start time).
        GetWindowThreadProcessId(hwnd, out var pid);
        if ((int)pid != model.ProcessId)
        {
            return null;
        }
        if (model.ProcessStartTime is { } started)
        {
            try
            {
                if (Math.Abs((Process.GetProcessById((int)pid).StartTime - started).TotalSeconds) > 2)
                {
                    return null;
                }
            }
            catch
            {
                // Cannot read it now; accept the PID match.
            }
        }

        var exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        if ((exStyle & WS_EX_TOPMOST) == 0)
        {
            return null;
        }

        SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        AppLog.Write($"HeldTargetRecord: released stuck topmost window 0x{hwnd:X} \"{model.Title}\"");
        return model.Title;
    }

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOPMOST = 0x00000008;
    private static readonly nint HWND_NOTOPMOST = -2;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
