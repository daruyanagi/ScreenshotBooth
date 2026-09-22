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

    public BoothController(nint boothWindowHandle)
    {
        _boothHwnd = (HWND)boothWindowHandle;

        // Derived purely from the HWND via Win32 interop rather than the Window.AppWindow
        // property, so this can be constructed safely before InitializeComponent() has run
        // (the underlying native window already exists once the Window base constructor returns).
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(boothWindowHandle);
        _boothAppWindow = AppWindow.GetFromWindowId(windowId);
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
        return true;
    }

    /// <summary>
    /// Resizes and centers the target window on <paramref name="display"/>. If the target refuses
    /// the requested size (e.g. a fixed-size dialog clamps it via WM_GETMINMAXINFO), we re-measure
    /// its actual post-move bounds and center using that instead of forcing the issue.
    /// </summary>
    public Rectangle ResizeAndCenterTarget(int width, int height, DisplayArea display)
    {
        var work = display.WorkArea;
        var x = work.X + (work.Width - width) / 2;
        var y = work.Y + (work.Height - height) / 2;

        SetWindowPos(_targetHwnd, HWND.NULL, x, y, width, height,
            SetWindowPosFlags.SWP_NOZORDER | SetWindowPosFlags.SWP_NOACTIVATE);

        // Re-measure: the window may have clamped the size (fixed-size dialogs, min/max constraints).
        return CenterTarget(display);
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

    /// <summary>
    /// The largest target size (physical px) the 4:3 booth can host on <paramref name="display"/>:
    /// the booth is clamped to the work area, so anything bigger would overflow it.
    /// </summary>
    public Size GetMaxTargetSize(DisplayArea display)
    {
        var scale = DpiScale;
        var marginPx = (int)(MarginDip * scale);
        var chromePx = (int)((_chromeTopDip + _chromeBottomDip) * scale);
        var work = display.WorkArea;

        const double ratio = 4.0 / 3.0;
        double clientH = work.Height - chromePx;
        double clientW = clientH * ratio;
        if (clientW > work.Width)
        {
            clientW = work.Width;
            clientH = clientW / ratio;
        }

        return new Size(Math.Max(0, (int)clientW - marginPx * 2), Math.Max(0, (int)clientH - marginPx * 2));
    }

    /// <summary>Sets or clears WS_EX_TOPMOST on the target window.</summary>
    public void SetTargetTopMost(bool topMost)
    {
        if (!HasTarget)
        {
            return;
        }

        SetWindowPos(_targetHwnd, topMost ? HWND.HWND_TOPMOST : HWND.HWND_NOTOPMOST,
            0, 0, 0, 0,
            SetWindowPosFlags.SWP_NOMOVE | SetWindowPosFlags.SWP_NOSIZE | SetWindowPosFlags.SWP_NOACTIVATE);
    }

    /// <summary>
    /// Sizes and positions the booth window as a white 4:3 client area around the target's bounds
    /// (plus shadow margin) with a fixed-height toolbar row, centered on <paramref name="display"/>.
    /// </summary>
    /// <summary>Where the booth was last placed programmatically, so user drags can be told apart from layout.</summary>
    public Windows.Graphics.PointInt32? LastLayoutPosition { get; private set; }

    /// <summary>
    /// The target size (physical px) that fills the booth area as it is on screen right now, minus
    /// the shadow margin - used to grow a target that was acquired at its natural size.
    /// </summary>
    public Size GetFitToBoothTargetSize(FrameworkElement boothArea)
    {
        var scale = DpiScale;
        var marginPx = (int)(MarginDip * scale);
        return new Size(
            Math.Max(0, (int)(boothArea.ActualWidth * scale) - marginPx * 2),
            Math.Max(0, (int)(boothArea.ActualHeight * scale) - marginPx * 2));
    }

    public void LayoutBoothWindowAroundTarget(Rectangle targetBounds, DisplayArea display)
    {
        var scale = DpiScale;
        var marginPx = (int)(MarginDip * scale);
        var chromeTopPx = (int)(_chromeTopDip * scale);
        var chromeBottomPx = (int)(_chromeBottomDip * scale);

        double rawW = targetBounds.Width + marginPx * 2;
        double rawH = targetBounds.Height + marginPx * 2;

        const double ratio = 4.0 / 3.0;
        double clientW, clientH;
        if (rawW / rawH > ratio)
        {
            clientW = rawW;
            clientH = rawW / ratio;
        }
        else
        {
            clientH = rawH;
            clientW = rawH * ratio;
        }

        var work = display.WorkArea;

        // Clamp to the work area (minus chrome) while preserving the 4:3 ratio.
        var maxClientH = work.Height - chromeTopPx - chromeBottomPx;
        if (clientH > maxClientH)
        {
            clientH = maxClientH;
            clientW = clientH * ratio;
        }
        if (clientW > work.Width)
        {
            clientW = work.Width;
            clientH = clientW / ratio;
        }

        var totalW = (int)clientW;
        var totalH = (int)clientH + chromeTopPx + chromeBottomPx;

        var x = work.X + (work.Width - totalW) / 2;
        var y = work.Y + (work.Height - totalH) / 2;

        LastLayoutPosition = new Windows.Graphics.PointInt32(x, y);
        _boothAppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, totalW, totalH));

        // Center the target in the booth AREA (not the window), so the margins are even.
        CenterTargetAt(x + totalW / 2, y + chromeTopPx + (int)clientH / 2);
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

    private void CenterTargetAt(int centerX, int centerY)
    {
        if (!HasTarget)
        {
            return;
        }

        var actual = GetTargetExtendedFrameBounds();
        var x = centerX - actual.Width / 2;
        var y = centerY - actual.Height / 2;
        if (x != actual.X || y != actual.Y)
        {
            SetWindowPos(_targetHwnd, HWND.NULL, x, y, 0, 0,
                SetWindowPosFlags.SWP_NOSIZE | SetWindowPosFlags.SWP_NOZORDER | SetWindowPosFlags.SWP_NOACTIVATE);
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
