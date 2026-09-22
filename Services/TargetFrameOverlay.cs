using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Vanara.PInvoke;
using static Vanara.PInvoke.User32;

namespace ScreenshotBooth.Services;

/// <summary>
/// An accent-colored ring drawn just outside the target window's visible frame while the booth
/// holds it, so "this window is captured" is visible on the window itself rather than in the
/// toolbar. It is a click-through, non-activating, per-pixel-alpha layered Win32 window, painted
/// with UpdateLayeredWindow - WinUI windows cannot be made transparent this way.
/// </summary>
public sealed class TargetFrameOverlay : IDisposable
{
    private const string ClassName = "ScreenshotBooth.TargetFrame";
    private const int Thickness = 3;
    private const int WindowCornerRadius = 8;

    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_TOPMOST = 0x00000008;
    private const uint WS_EX_TRANSPARENT = 0x00000020;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_LAYERED = 0x00080000;
    private const uint WS_EX_NOACTIVATE = 0x08000000;
    private const uint ULW_ALPHA = 0x00000002;

    // Rooted for the window's lifetime.
    private readonly WindowProc _wndProc;
    private HWND _hwnd;

    public TargetFrameOverlay()
    {
        var hInstance = (HINSTANCE)(IntPtr)Kernel32.GetModuleHandle(null);
        _wndProc = (hwnd, msg, wParam, lParam) => DefWindowProc(hwnd, msg, wParam, lParam);

        var wndClass = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = _wndProc,
            hInstance = hInstance,
            lpszClassName = ClassName,
        };
        RegisterClassEx(wndClass);

        _hwnd = CreateWindowEx(
            (WindowStylesEx)(WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE),
            ClassName, "", (WindowStyles)WS_POPUP, 0, 0, 0, 0, HWND.NULL, HMENU.NULL, hInstance, IntPtr.Zero);
        if (_hwnd == HWND.NULL)
        {
            AppLog.Write($"TargetFrame: CreateWindowEx failed error={Marshal.GetLastWin32Error()}");
        }
    }

    /// <summary>Draws the ring around <paramref name="frame"/> (the target's visible bounds, physical px) and shows it topmost.</summary>
    public void Show(Rectangle frame)
    {
        if (_hwnd == HWND.NULL || frame.Width <= 0 || frame.Height <= 0)
        {
            return;
        }

        var outer = Rectangle.Inflate(frame, Thickness, Thickness);

        using var bitmap = new Bitmap(outer.Width, outer.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(AccentColor(), Thickness) { Alignment = PenAlignment.Inset };
            using var path = RoundedRectangle(new Rectangle(0, 0, outer.Width, outer.Height), WindowCornerRadius + Thickness);
            g.DrawPath(pen, path);
        }

        var screenDc = GetDC(IntPtr.Zero);
        var memoryDc = CreateCompatibleDC(screenDc);
        var hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
        var previous = SelectObject(memoryDc, hBitmap);
        try
        {
            var destination = new Point(outer.X, outer.Y);
            var size = new Size(outer.Width, outer.Height);
            var source = new Point(0, 0);
            var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
            if (!UpdateLayeredWindow((IntPtr)_hwnd, screenDc, ref destination, ref size, memoryDc, ref source, 0, ref blend, ULW_ALPHA))
            {
                AppLog.Write($"TargetFrame: UpdateLayeredWindow failed error={Marshal.GetLastWin32Error()}");
            }
        }
        finally
        {
            SelectObject(memoryDc, previous);
            DeleteObject(hBitmap);
            DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }

        // Above the (also topmost) target, without taking focus from it.
        SetWindowPos(_hwnd, HWND.HWND_TOPMOST, 0, 0, 0, 0,
            SetWindowPosFlags.SWP_NOMOVE | SetWindowPosFlags.SWP_NOSIZE | SetWindowPosFlags.SWP_NOACTIVATE | SetWindowPosFlags.SWP_SHOWWINDOW);
    }

    public void Hide()
    {
        if (_hwnd != HWND.NULL)
        {
            ShowWindow(_hwnd, ShowWindowCommand.SW_HIDE);
        }
    }

    public void Dispose()
    {
        if (_hwnd != HWND.NULL)
        {
            DestroyWindow(_hwnd);
            _hwnd = HWND.NULL;
        }
    }

    private static Color AccentColor()
    {
        var accent = new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent);
        return Color.FromArgb(accent.A, accent.R, accent.G, accent.B);
    }

    private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref Point pptDst, ref Size psize,
        IntPtr hdcSrc, ref Point pptSrc, uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr h);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);
}
