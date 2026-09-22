using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Vanara.PInvoke;
using static Vanara.PInvoke.User32;

namespace ScreenshotBooth.Services;

/// <summary>
/// Snipping-Tool-style window picker: a dimmed, per-pixel-alpha layered window over the whole
/// virtual screen. Moving the mouse highlights the top-level window under it; a left click picks
/// it, Esc or a right click cancels. The dim has a small alpha everywhere so the overlay receives
/// the clicks (alpha-0 pixels would pass them through to the windows below).
/// </summary>
public sealed class WindowPickerOverlay : IDisposable
{
    private const string ClassName = "ScreenshotBooth.WindowPicker";
    private const int HighlightThickness = 4;
    private const byte DimAlpha = 96;
    private const byte HoverAlpha = 12;

    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_TOPMOST = 0x00000008;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_LAYERED = 0x00080000;
    private const uint ULW_ALPHA = 0x00000002;

    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_SETCURSOR = 0x0020;
    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_RBUTTONUP = 0x0205;
    private const int VK_ESCAPE = 0x1B;
    private const int IDC_CROSS = 32515;
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    private readonly WindowProc _wndProc;   // rooted
    private HWND _hwnd;
    private Rectangle _screen;
    private HWND _hovered;
    private readonly HashSet<nint> _excluded = new();
    private uint _ownPid;
    private bool _active;

    /// <summary>A top-level window was clicked.</summary>
    public event Action<nint>? WindowPicked;

    /// <summary>The picker was dismissed without a choice.</summary>
    public event Action? Cancelled;

    /// <summary>The click landed where there is no pickable window (desktop, taskbar, our own windows).</summary>
    public event Action? NothingPicked;

    public WindowPickerOverlay()
    {
        var hInstance = (HINSTANCE)(IntPtr)Kernel32.GetModuleHandle(null);
        _wndProc = WndProc;

        var wndClass = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = _wndProc,
            hInstance = hInstance,
            lpszClassName = ClassName,
        };
        RegisterClassEx(wndClass);

        _hwnd = CreateWindowEx((WindowStylesEx)(WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TOOLWINDOW),
            ClassName, "", (WindowStyles)WS_POPUP, 0, 0, 0, 0, HWND.NULL, HMENU.NULL, hInstance, IntPtr.Zero);
        if (_hwnd == HWND.NULL)
        {
            AppLog.Write($"WindowPicker: CreateWindowEx failed error={Marshal.GetLastWin32Error()}");
        }

        GetWindowThreadProcessId(_hwnd, out _ownPid);
    }

    public bool IsActive => _active;

    /// <summary>Covers the virtual screen and starts tracking the mouse. Windows in <paramref name="exclude"/> are never offered.</summary>
    public void Start(IEnumerable<nint> exclude)
    {
        _excluded.Clear();
        foreach (var h in exclude)
        {
            _excluded.Add(h);
        }

        _screen = new Rectangle(
            GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
            GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN));
        _hovered = HWND.NULL;
        _active = true;

        Render();
        SetWindowPos(_hwnd, HWND.HWND_TOPMOST, 0, 0, 0, 0,
            SetWindowPosFlags.SWP_NOMOVE | SetWindowPosFlags.SWP_NOSIZE | SetWindowPosFlags.SWP_SHOWWINDOW);
        SetForegroundWindow(_hwnd);   // so Esc reaches us
        UpdateHover();
    }

    public void Cancel()
    {
        if (!_active)
        {
            return;
        }
        Finish();
        Cancelled?.Invoke();
    }

    private void Finish()
    {
        _active = false;
        ShowWindow(_hwnd, ShowWindowCommand.SW_HIDE);
    }

    private void UpdateHover()
    {
        if (!GetCursorPos(out var p))
        {
            return;
        }

        var hit = TopLevelWindowAt(new Point(p.X, p.Y));
        if (hit != _hovered)
        {
            _hovered = hit;
            Render();
        }
    }

    /// <summary>The topmost visible, uncloaked top-level window (not ours, not the shell) under the point.</summary>
    private HWND TopLevelWindowAt(Point point)
    {
        HWND found = HWND.NULL;
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd) || _excluded.Contains((nint)hwnd))
            {
                return true;
            }

            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == _ownPid)
            {
                return true;
            }

            var className = new System.Text.StringBuilder(64);
            GetClassName(hwnd, className, className.Capacity);
            var cls = className.ToString();
            if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            {
                return true;
            }

            if (IsCloaked(hwnd))
            {
                return true;
            }

            var bounds = VisibleBounds(hwnd);
            if (bounds.Width <= 0 || bounds.Height <= 0 || !bounds.Contains(point))
            {
                return true;
            }

            found = hwnd;
            return false;   // first hit in z-order wins
        }, IntPtr.Zero);
        return found;
    }

    private static bool IsCloaked(HWND hwnd)
    {
        return DwmGetWindowAttribute((IntPtr)hwnd, 14 /* DWMWA_CLOAKED */, out int cloaked, sizeof(int)) == 0 && cloaked != 0;
    }

    private static Rectangle VisibleBounds(HWND hwnd)
    {
        if (DwmGetWindowAttribute((IntPtr)hwnd, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out RECT frame, Marshal.SizeOf<RECT>()) == 0)
        {
            return frame;
        }
        GetWindowRect(hwnd, out RECT rect);
        return rect;
    }

    private void Render()
    {
        if (_hwnd == HWND.NULL || _screen.Width <= 0 || _screen.Height <= 0)
        {
            return;
        }

        using var bitmap = new Bitmap(_screen.Width, _screen.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.FromArgb(DimAlpha, 0, 0, 0));

            if (_hovered != HWND.NULL)
            {
                var bounds = VisibleBounds(_hovered);
                bounds.Offset(-_screen.X, -_screen.Y);

                // Nearly clear over the hovered window (still hit-testable), accent frame around it.
                g.CompositingMode = CompositingMode.SourceCopy;
                using (var clear = new SolidBrush(Color.FromArgb(HoverAlpha, 0, 0, 0)))
                {
                    g.FillRectangle(clear, bounds);
                }
                g.CompositingMode = CompositingMode.SourceOver;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using var pen = new Pen(AccentColor(), HighlightThickness) { Alignment = PenAlignment.Inset };
                using var path = RoundedRectangle(Rectangle.Inflate(bounds, HighlightThickness, HighlightThickness), 8 + HighlightThickness);
                g.DrawPath(pen, path);
            }

            DrawHint(g);
        }

        Present(bitmap, _screen.Location);
    }

    private void DrawHint(Graphics g)
    {
        var text = R.Get("PickerHint");
        using var font = new Font(SystemFonts.MessageBoxFont?.FontFamily ?? System.Drawing.FontFamily.GenericSansSerif, 18f, GraphicsUnit.Pixel);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        var size = g.MeasureString(text, font);
        var panel = new RectangleF((_screen.Width - size.Width) / 2 - 20, 40, size.Width + 40, size.Height + 20);
        using var back = new SolidBrush(Color.FromArgb(200, 24, 24, 24));
        using var shape = RoundedRectangle(Rectangle.Round(panel), 10);
        g.FillPath(back, shape);
        using var white = new SolidBrush(Color.White);
        g.DrawString(text, font, white, panel.X + 20, panel.Y + 10);
    }

    private IntPtr WndProc(HWND hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_MOUSEMOVE:
                if (_active)
                {
                    UpdateHover();
                }
                return IntPtr.Zero;

            case WM_LBUTTONUP:
                if (_active)
                {
                    UpdateHover();
                    var picked = _hovered;
                    Finish();
                    if (picked != HWND.NULL)
                    {
                        WindowPicked?.Invoke((nint)picked);
                    }
                    else
                    {
                        NothingPicked?.Invoke();
                    }
                }
                return IntPtr.Zero;

            case WM_RBUTTONUP:
                Cancel();
                return IntPtr.Zero;

            case WM_KEYDOWN:
                if ((int)(long)wParam == VK_ESCAPE)
                {
                    Cancel();
                }
                return IntPtr.Zero;

            case WM_SETCURSOR:
                SetCursorNative(LoadCursorNative(IntPtr.Zero, (IntPtr)IDC_CROSS));
                return (IntPtr)1;
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void Present(Bitmap bitmap, Point location)
    {
        var screenDc = GetDC(IntPtr.Zero);
        var memoryDc = CreateCompatibleDC(screenDc);
        var hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
        var previous = SelectObject(memoryDc, hBitmap);
        try
        {
            var destination = location;
            var size = bitmap.Size;
            var source = new Point(0, 0);
            var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
            if (!UpdateLayeredWindow((IntPtr)_hwnd, screenDc, ref destination, ref size, memoryDc, ref source, 0, ref blend, ULW_ALPHA))
            {
                AppLog.Write($"WindowPicker: UpdateLayeredWindow failed error={Marshal.GetLastWin32Error()}");
            }
        }
        finally
        {
            SelectObject(memoryDc, previous);
            DeleteObject(hBitmap);
            DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
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
        var diameter = Math.Max(1, radius * 2);
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

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", EntryPoint = "LoadCursorW")]
    private static extern IntPtr LoadCursorNative(IntPtr hInstance, IntPtr lpCursorName);

    [DllImport("user32.dll", EntryPoint = "SetCursor")]
    private static extern IntPtr SetCursorNative(IntPtr hCursor);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr h);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);
}
