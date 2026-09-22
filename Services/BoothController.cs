using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Vanara.PInvoke;
using Windows.Storage.Streams;
using ScreenshotBooth.Models;
using static Vanara.PInvoke.DwmApi;
using static Vanara.PInvoke.User32;

namespace ScreenshotBooth.Services;

/// <summary>Result of a shutter capture: raw PNG bytes (for clipboard/save) plus a ready-to-bind preview image.</summary>
public sealed record CaptureResult(byte[] PngBytes, BitmapImage Preview);

/// <summary>
/// Owns all target-window and booth-window interop: acquiring the foreground window as the
/// capture target, centering/sizing it and the booth window on the chosen display, toggling
/// topmost, restoring focus before a capture, and grabbing pixels off the screen.
/// </summary>
public sealed class BoothController
{
    // Visual breathing room (physical px, at 96 DPI baseline) around the target window so its
    // DWM-composited rounded corners and drop shadow are fully visible against the white backdrop.
    private const int MarginDip = 64;

    // Window chrome above/below the booth area (title bar + toolbar on top), in DIPs. These are
    // estimates for the very first layout; MeasureChrome replaces them once the area has been laid out.
    private const double DefaultChromeTopDip = 32 + 72;
    private const double DefaultChromeBottomDip = 0;

    private double _chromeTopDip = DefaultChromeTopDip;
    private double _chromeBottomDip = DefaultChromeBottomDip;

    private readonly HWND _boothHwnd;
    private readonly AppWindow _boothAppWindow;

    private HWND _targetHwnd;
    private bool _isPinned;

    // Where the target was last centered, so a user drag can be snapped back (the target is
    // effectively immovable while the booth holds it).
    private Point? _lastAreaCenter;

    private readonly TargetFrameOverlay _frame;
    private readonly WinEventDelegate _winEventProc;   // rooted for the hook's lifetime
    private readonly IntPtr _moveSizeHook;

    public BoothController(nint boothWindowHandle)
    {
        _boothHwnd = (HWND)boothWindowHandle;

        // Derived purely from the HWND via Win32 interop rather than the Window.AppWindow
        // property, so this can be constructed safely before InitializeComponent() has run
        // (the underlying native window already exists once the Window base constructor returns).
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(boothWindowHandle);
        _boothAppWindow = AppWindow.GetFromWindowId(windowId);

        _frame = new TargetFrameOverlay();

        // EVENT_SYSTEM_MOVESIZEEND (0x000B): fires on this (UI) thread when a user move/resize of
        // any window ends; we only react for the held target.
        _winEventProc = OnWinEvent;
        _moveSizeHook = SetWinEventHookNative(0x000B, 0x000B, IntPtr.Zero, _winEventProc, 0, 0, 0);
    }

    /// <summary>True while a valid target window is being tracked.</summary>
    public bool HasTarget => _targetHwnd != HWND.NULL && IsWindow(_targetHwnd);

    private double DpiScale => GetDpiForWindow(_boothHwnd) / 96.0;

    /// <summary>
    /// Grabs the current foreground window as the capture target (ignoring the booth window
    /// itself, and the desktop/shell when nothing else is foreground). Returns false if there is
    /// no suitable target.
    /// </summary>
    public bool TryAcquireForegroundAsTarget()
    {
        var fg = GetForegroundWindow();
        if (fg.IsNull || fg == _boothHwnd || !IsWindow(fg) || !IsWindowVisible(fg))
        {
            return false;
        }

        _targetHwnd = fg;
        var title = new System.Text.StringBuilder(256);
        GetWindowText(fg, title, title.Capacity);
        AppLog.Write($"Acquire: target=0x{(nint)fg:X} \"{title}\" bounds={GetTargetExtendedFrameBounds()}");
        return true;
    }

    /// <summary>Centers the target on <paramref name="display"/> at its current size and returns its actual bounds.</summary>
    public Rectangle CenterTarget(DisplayArea display)
    {
        var work = display.WorkArea;
        var actual = GetTargetExtendedFrameBounds();

        // Recenter using the actual size in case it differs from what we asked for.
        var actualX = work.X + (work.Width - actual.Width) / 2;
        var actualY = work.Y + (work.Height - actual.Height) / 2;
        if (actualX != actual.X || actualY != actual.Y)
        {
            SetWindowPos(_targetHwnd, HWND.NULL, actualX, actualY, 0, 0,
                SetWindowPosFlags.SWP_NOSIZE | SetWindowPosFlags.SWP_NOZORDER | SetWindowPosFlags.SWP_NOACTIVATE);
            actual = GetTargetExtendedFrameBounds();
        }

        return actual;
    }

    /// <summary>
    /// Reads the target window's true visible bounds (DWM extended frame bounds), which excludes
    /// the invisible resize-border padding that GetWindowRect includes on modern Windows.
    /// </summary>
    public Rectangle GetTargetExtendedFrameBounds()
    {
        var hr = DwmGetWindowAttribute(_targetHwnd, DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS, out RECT bounds);

        if (hr.Failed)
        {
            GetWindowRect(_targetHwnd, out bounds);
        }

        return bounds;
    }

    /// <summary>The largest booth area (physical px) that fits the work area of <paramref name="display"/> with the chrome.</summary>
    public Size GetMaxAreaSize(DisplayArea display)
    {
        var chromePx = (int)((_chromeTopDip + _chromeBottomDip) * DpiScale);
        var work = display.WorkArea;
        return new Size(Math.Max(1, work.Width), Math.Max(1, work.Height - chromePx));
    }

    /// <summary>The smallest booth area (physical px) that shows the held target with its full shadow margin, or null without a target.</summary>
    public Size? GetMinAreaForTarget()
    {
        if (!HasTarget)
        {
            return null;
        }

        var frame = GetTargetExtendedFrameBounds();
        var marginPx = (int)(MarginDip * DpiScale);
        return new Size(frame.Width + marginPx * 2, frame.Height + marginPx * 2);
    }

    /// <summary>The booth area's current on-screen size in physical px.</summary>
    public Size GetAreaSizePx(FrameworkElement boothArea)
    {
        var scale = DpiScale;
        return new Size((int)Math.Round(boothArea.ActualWidth * scale), (int)Math.Round(boothArea.ActualHeight * scale));
    }

    /// <summary>Sets or clears WS_EX_TOPMOST on the target window, and shows or hides the frame ring around it.</summary>
    public void SetTargetTopMost(bool topMost)
    {
        _isPinned = topMost && HasTarget;
        if (!HasTarget)
        {
            _frame.Hide();
            return;
        }

        SetWindowPos(_targetHwnd, topMost ? HWND.HWND_TOPMOST : HWND.HWND_NOTOPMOST,
            0, 0, 0, 0,
            SetWindowPosFlags.SWP_NOMOVE | SetWindowPosFlags.SWP_NOSIZE | SetWindowPosFlags.SWP_NOACTIVATE);

        if (topMost)
        {
            _frame.Show(GetTargetExtendedFrameBounds());
        }
        else
        {
            _frame.Hide();
        }
    }

    /// <summary>Hides the frame ring without releasing the target - called right before a capture so the ring is not in the shot.</summary>
    public void HideTargetFrame() => _frame.Hide();

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint eventThread, uint eventTime)
    {
        if (_isPinned && hwnd == (IntPtr)_targetHwnd && _lastAreaCenter is { } center)
        {
            CenterTargetAt(center.X, center.Y);
        }
    }

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWinEventHook")]
    private static extern IntPtr SetWinEventHookNative(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    /// <summary>
    /// Sizes and positions the booth window as a white 4:3 client area around the target's bounds
    /// (plus shadow margin) with a fixed-height toolbar row, centered on <paramref name="display"/>.
    /// </summary>
    /// <summary>Size the booth was last given programmatically; a different current size means the user resized it.</summary>
    public Windows.Graphics.SizeInt32? LastLayoutSize { get; private set; }

    public bool IsBoothAtLayoutSize => LastLayoutSize is { } s && _boothAppWindow.Size == s;

    /// <summary>
    /// Resizes the target to fill the booth area as it is on screen right now (minus the shadow
    /// margin) and centers it there, without moving the booth. Returns the target's actual bounds.
    /// </summary>
    public Rectangle FitTargetToArea(FrameworkElement boothArea)
    {
        var area = GetScreenRectOfElement(boothArea);
        var marginPx = (int)(MarginDip * DpiScale);
        var width = Math.Max(1, area.Width - marginPx * 2);
        var height = Math.Max(1, area.Height - marginPx * 2);

        // Size the visible frame, not the window rect (which includes invisible borders).
        var insets = GetTargetFrameInsets();
        SetWindowPos(_targetHwnd, HWND.NULL,
            area.X + (area.Width - width) / 2 - insets.Left, area.Y + (area.Height - height) / 2 - insets.Top,
            width + insets.Left + insets.Right, height + insets.Top + insets.Bottom,
            SetWindowPosFlags.SWP_NOZORDER | SetWindowPosFlags.SWP_NOACTIVATE);

        // The window may have refused the size; center whatever it ended up as.
        _lastAreaCenter = new Point(area.X + area.Width / 2, area.Y + area.Height / 2);
        CenterTargetAt(area.X + area.Width / 2, area.Y + area.Height / 2);
        return GetTargetExtendedFrameBounds();
    }

    public Size LayoutBoothWindowAroundTarget(Rectangle targetBounds, DisplayArea display)
    {
        var marginPx = (int)(MarginDip * DpiScale);
        var max = GetMaxAreaSize(display);

        double rawW = targetBounds.Width + marginPx * 2;
        double rawH = targetBounds.Height + marginPx * 2;

        const double ratio = 4.0 / 3.0;
        double areaW, areaH;
        if (rawW / rawH > ratio)
        {
            areaW = rawW;
            areaH = rawW / ratio;
        }
        else
        {
            areaH = rawH;
            areaW = rawH * ratio;
        }

        // Clamp to the work area while preserving the 4:3 ratio.
        if (areaH > max.Height)
        {
            areaH = max.Height;
            areaW = areaH * ratio;
        }
        if (areaW > max.Width)
        {
            areaW = max.Width;
            areaH = areaW / ratio;
        }

        return LayoutBoothWithAreaSize((int)areaW, (int)areaH, display, recordLayoutSize: true);
    }

    /// <summary>
    /// Gives the booth area an exact size (physical px; this is the captured image size), centered
    /// on <paramref name="display"/>, and re-centers the target in it. When
    /// <paramref name="recordLayoutSize"/> is false the resulting size is not remembered as "ours",
    /// so the booth-area layout handler treats it like a user resize (fit mode applies).
    /// </summary>
    public Size LayoutBoothWithAreaSize(int areaW, int areaH, DisplayArea display, bool recordLayoutSize)
    {
        var scale = DpiScale;
        var chromeTopPx = (int)(_chromeTopDip * scale);
        var chromeBottomPx = (int)(_chromeBottomDip * scale);
        var max = GetMaxAreaSize(display);
        areaW = Math.Clamp(areaW, 1, max.Width);
        areaH = Math.Clamp(areaH, 1, max.Height);

        _boothAppWindow.ResizeClient(new Windows.Graphics.SizeInt32(areaW, areaH + chromeTopPx + chromeBottomPx));
        AppLog.Write($"Layout: area={areaW}x{areaH} chrome={chromeTopPx}+{chromeBottomPx} -> client={_boothAppWindow.ClientSize.Width}x{_boothAppWindow.ClientSize.Height} size={_boothAppWindow.Size.Width}x{_boothAppWindow.Size.Height}");

        var work = display.WorkArea;
        var size = _boothAppWindow.Size;
        var x = work.X + (work.Width - size.Width) / 2;
        var y = work.Y + (work.Height - size.Height) / 2;
        _boothAppWindow.Move(new Windows.Graphics.PointInt32(x, y));

        LastLayoutSize = recordLayoutSize ? size : null;

        // Center the target in the booth AREA (not the window), so the margins are even.
        var origin = new POINT(0, 0);
        ClientToScreen(_boothHwnd, ref origin);
        _lastAreaCenter = new Point(origin.X + areaW / 2, origin.Y + chromeTopPx + areaH / 2);
        CenterTargetAt(_lastAreaCenter.Value.X, _lastAreaCenter.Value.Y);

        return new Size(areaW, areaH);
    }

    /// <summary>
    /// Records how much window chrome sits above/below the booth area, from its actual layout.
    /// Returns true when the values changed enough that the current layout should be redone.
    /// </summary>
    public bool MeasureChrome(FrameworkElement boothArea)
    {
        if (boothArea.ActualHeight <= 0)
        {
            return false;
        }

        var bounds = boothArea.TransformToVisual(null)
            .TransformBounds(new Windows.Foundation.Rect(0, 0, boothArea.ActualWidth, boothArea.ActualHeight));
        var clientHeightDip = _boothAppWindow.ClientSize.Height / DpiScale;
        var top = bounds.Y;
        var bottom = Math.Max(0, clientHeightDip - (bounds.Y + bounds.Height));

        var changed = Math.Abs(top - _chromeTopDip) > 1 || Math.Abs(bottom - _chromeBottomDip) > 1;
        _chromeTopDip = top;
        _chromeBottomDip = bottom;
        return changed;
    }

    /// <summary>
    /// How far the window rect (what SetWindowPos positions and sizes) extends beyond the visible
    /// frame on each side - the invisible resize borders on modern Windows, typically 7-8px.
    /// </summary>
    private (int Left, int Top, int Right, int Bottom) GetTargetFrameInsets()
    {
        GetWindowRect(_targetHwnd, out RECT rect);
        var frame = GetTargetExtendedFrameBounds();
        return (frame.X - rect.left, frame.Y - rect.top, rect.right - frame.Right, rect.bottom - frame.Bottom);
    }

    private void CenterTargetAt(int centerX, int centerY)
    {
        if (!HasTarget)
        {
            return;
        }

        var frame = GetTargetExtendedFrameBounds();
        var insets = GetTargetFrameInsets();
        var x = centerX - frame.Width / 2 - insets.Left;
        var y = centerY - frame.Height / 2 - insets.Top;
        GetWindowRect(_targetHwnd, out RECT rect);
        if (x != rect.left || y != rect.top)
        {
            SetWindowPos(_targetHwnd, HWND.NULL, x, y, 0, 0,
                SetWindowPosFlags.SWP_NOSIZE | SetWindowPosFlags.SWP_NOZORDER | SetWindowPosFlags.SWP_NOACTIVATE);
        }

        if (_isPinned)
        {
            _frame.Show(GetTargetExtendedFrameBounds());
        }
    }

    /// <summary>
    /// Restores the target window's active/focused state before a capture - clicking the shutter
    /// button (or a countdown finishing) can otherwise leave it visibly inactive (dimmed title
    /// bar), which defeats the purpose of a "screenshot booth". Waits briefly for the redraw.
    /// </summary>
    public async Task RestoreTargetFocusAsync()
    {
        if (!HasTarget)
        {
            return;
        }

        SetForegroundWindow(_targetHwnd);
        await Task.Delay(150);
    }

    /// <summary>
    /// Captures the booth's client area (white backdrop + target window on top of it) via a raw
    /// screen-region grab - deliberately System.Drawing.CopyFromScreen rather than
    /// Windows.Graphics.Capture, since the latter draws a capture-indicator border on modern
    /// Windows that would visually pollute the screenshot.
    /// </summary>
    public async Task<CaptureResult> CaptureAsync(FrameworkElement boothArea)
    {
        var screenRect = GetScreenRectOfElement(boothArea);

        using var bitmap = new Bitmap(screenRect.Width, screenRect.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.CopyFromScreen(screenRect.X, screenRect.Y, 0, 0, screenRect.Size, CopyPixelOperation.SourceCopy);
        }

        using var pngStream = new MemoryStream();
        bitmap.Save(pngStream, ImageFormat.Png);
        var pngBytes = pngStream.ToArray();

        var preview = new BitmapImage();
        using var ras = new InMemoryRandomAccessStream();
        await ras.WriteAsync(pngBytes.AsBuffer());
        ras.Seek(0);
        await preview.SetSourceAsync(ras);

        return new CaptureResult(pngBytes, preview);
    }

    /// <summary>
    /// Returns the given element's on-screen bounds in physical pixels, by combining its
    /// transform-to-root (DIPs) with the window's client-to-screen offset and DPI scale.
    /// </summary>
    private Rectangle GetScreenRectOfElement(FrameworkElement element)
    {
        var transform = element.TransformToVisual(null);
        var bounds = transform.TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));

        var scale = DpiScale;
        var physicalX = (int)(bounds.X * scale);
        var physicalY = (int)(bounds.Y * scale);
        var physicalW = (int)(bounds.Width * scale);
        var physicalH = (int)(bounds.Height * scale);

        var origin = new POINT(0, 0);
        ClientToScreen(_boothHwnd, ref origin);

        return new Rectangle(origin.X + physicalX, origin.Y + physicalY, physicalW, physicalH);
    }

    /// <summary>
    /// Returns to the "live booth" state after a Retake: re-shows the target window topmost if it
    /// is still alive, or is a no-op (leaving a blank white booth) if it was closed meanwhile.
    /// </summary>
    public void ReturnToLiveState()
    {
        if (!HasTarget)
        {
            return;
        }

        SetTargetTopMost(true);
    }
}
