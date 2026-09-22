using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Vanara.PInvoke;
using static Vanara.PInvoke.User32;

namespace ScreenshotBooth.Services;

/// <summary>
/// The on-window indicators for a held target: an accent ring just outside its visible frame, and
/// a chip attached below the ring saying the window is held, with a Release pill.
///
/// Both are per-pixel-alpha layered Win32 windows painted with UpdateLayeredWindow (WinUI windows
/// cannot be transparent or border-less enough for this). They are two HWNDs on purpose: the ring
/// is fully click-through (WS_EX_TRANSPARENT) so it never steals the target's resize handles,
/// while the chip relies on layered-window hit testing - clicks land only on its opaque pixels -
/// so the Release pill is clickable without a WinUI window, its DWM border or its focus rules.
/// </summary>
public sealed class TargetFrameOverlay : IDisposable
{
    private const string RingClassName = "ScreenshotBooth.TargetFrame";
    private const string ChipClassName = "ScreenshotBooth.TargetChip";
    public const int Thickness = 3;
    private const int WindowCornerRadius = 8;

    // Chip metrics in DIPs (scaled by the display's DPI when drawn).
    private const float ChipFontDip = 12f;
    private const int ChipPaddingXDip = 16;
    private const int ChipPaddingYDip = 8;
    private const int ChipGapDip = 12;
    private const int PillPaddingXDip = 12;
    private const int PillPaddingYDip = 3;
    private const int PillRadiusDip = 8;
    // Bottom corners only: the top edge sits flush against the ring, and the radius matches Windows 11 windows.
    private const int ChipCornerRadiusDip = 8;

    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_TOPMOST = 0x00000008;
    private const uint WS_EX_TRANSPARENT = 0x00000020;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_LAYERED = 0x00080000;
    private const uint WS_EX_NOACTIVATE = 0x08000000;
    private const uint ULW_ALPHA = 0x00000002;

    private const uint WM_SETCURSOR = 0x0020;
    private const uint WM_MOUSEACTIVATE = 0x0021;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const int MA_NOACTIVATE = 3;
    private const int IDC_HAND = 32649;

    // Rooted for the windows' lifetime.
    private readonly WindowProc _ringWndProc;
    private readonly WindowProc _chipWndProc;
    private HWND _ringHwnd;
    private HWND _chipHwnd;

    // The Release pill, in the chip window's client coordinates (physical px).
    private Rectangle _pillRect;

    /// <summary>Raised (on the UI thread) when the Release pill is clicked.</summary>
    public event Action? ReleaseRequested;

    public TargetFrameOverlay()
    {
        var hInstance = (HINSTANCE)(IntPtr)Kernel32.GetModuleHandle(null);

        _ringWndProc = (hwnd, msg, wParam, lParam) => DefWindowProc(hwnd, msg, wParam, lParam);
        _chipWndProc = ChipWndProc;

        _ringHwnd = CreateLayeredWindow(hInstance, RingClassName, _ringWndProc, WS_EX_TRANSPARENT);
        _chipHwnd = CreateLayeredWindow(hInstance, ChipClassName, _chipWndProc, 0);
    }

    private static HWND CreateLayeredWindow(HINSTANCE hInstance, string className, WindowProc wndProc, uint extraExStyle)
    {
        var wndClass = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = wndProc,
            hInstance = hInstance,
            lpszClassName = className,
        };
        RegisterClassEx(wndClass);

        var hwnd = CreateWindowEx(
            (WindowStylesEx)(WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | extraExStyle),
            className, "", (WindowStyles)WS_POPUP, 0, 0, 0, 0, HWND.NULL, HMENU.NULL, hInstance, IntPtr.Zero);
        if (hwnd == HWND.NULL)
        {
            AppLog.Write($"TargetFrame: CreateWindowEx({className}) failed error={Marshal.GetLastWin32Error()}");
        }
        return hwnd;
    }

    /// <summary>Draws the ring around <paramref name="frame"/> (the target's visible bounds, physical px) and the chip under it, and shows both topmost.</summary>
    public void Show(Rectangle frame, double scale)
    {
        if (frame.Width <= 0 || frame.Height <= 0)
        {
            return;
        }

        var accent = AccentColor();
        var outer = Rectangle.Inflate(frame, Thickness, Thickness);

        using (var ring = new Bitmap(outer.Width, outer.Height, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(ring))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using var pen = new Pen(accent, Thickness) { Alignment = PenAlignment.Inset };
                using var path = RoundedRectangle(new Rectangle(0, 0, outer.Width, outer.Height), WindowCornerRadius + Thickness);
                g.DrawPath(pen, path);
            }
            Present(_ringHwnd, ring, outer.Location);
        }

        using (var chip = DrawChip(accent, scale, out var chipSize))
        {
            // Centered under the ring, overlapping its bottom edge by 1px so it reads as attached.
            var location = new Point(outer.X + (outer.Width - chipSize.Width) / 2, outer.Bottom - 1);
            Present(_chipHwnd, chip, location);
        }
    }

    public void Hide()
    {
        ShowWindow(_ringHwnd, ShowWindowCommand.SW_HIDE);
        ShowWindow(_chipHwnd, ShowWindowCommand.SW_HIDE);
    }

    public void Dispose()
    {
        if (_ringHwnd != HWND.NULL)
        {
            DestroyWindow(_ringHwnd);
            _ringHwnd = HWND.NULL;
        }
        if (_chipHwnd != HWND.NULL)
        {
            DestroyWindow(_chipHwnd);
            _chipHwnd = HWND.NULL;
        }
    }

    private Bitmap DrawChip(Color accent, double scale, out Size size)
    {
        var label = R.Get("ChipHeldLabel");
        var release = R.Get("ChipReleaseLabel");
        var glyph = "\uE718";   // pin (Segoe Fluent / MDL2)

        using var font = new Font(SystemFonts.MessageBoxFont?.FontFamily ?? System.Drawing.FontFamily.GenericSansSerif, (float)(ChipFontDip * scale), GraphicsUnit.Pixel);
        using var iconFont = new Font(IconFontFamily(), (float)(ChipFontDip * scale), GraphicsUnit.Pixel);

        var paddingX = (int)(ChipPaddingXDip * scale);
        var paddingY = (int)(ChipPaddingYDip * scale);
        var gap = (int)(ChipGapDip * scale);
        var pillPadX = (int)(PillPaddingXDip * scale);
        var pillPadY = (int)(PillPaddingYDip * scale);

        // Measure with a throwaway surface; StringFormat.GenericTypographic avoids padding surprises.
        using var format = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.MeasureTrailingSpaces };
        SizeF glyphSize, labelSize, releaseSize;
        using (var probe = Graphics.FromImage(new Bitmap(1, 1)))
        {
            glyphSize = probe.MeasureString(glyph, iconFont, int.MaxValue, format);
            labelSize = probe.MeasureString(label, font, int.MaxValue, format);
            releaseSize = probe.MeasureString(release, font, int.MaxValue, format);
        }

        var pillWidth = (int)Math.Ceiling(releaseSize.Width) + pillPadX * 2;
        var pillHeight = (int)Math.Ceiling(releaseSize.Height) + pillPadY * 2;
        var contentHeight = Math.Max(pillHeight, (int)Math.Ceiling(Math.Max(labelSize.Height, glyphSize.Height)));
        var width = paddingX + (int)Math.Ceiling(glyphSize.Width) + gap + (int)Math.Ceiling(labelSize.Width) + gap + pillWidth + paddingX;
        var height = paddingY * 2 + contentHeight;
        size = new Size(width, height);

        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // Grayscale AA: ClearType does not survive per-pixel alpha compositing.
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        using (var background = new SolidBrush(accent))
        using (var shape = BottomRoundedRectangle(new Rectangle(0, 0, width, height), (int)(ChipCornerRadiusDip * scale)))
        {
            g.FillPath(background, shape);
        }

        var x = paddingX;
        var centerY = height / 2f;
        using (var white = new SolidBrush(Color.White))
        {
            g.DrawString(glyph, iconFont, white, x, centerY - glyphSize.Height / 2f, format);
            x += (int)Math.Ceiling(glyphSize.Width) + gap;
            g.DrawString(label, font, white, x, centerY - labelSize.Height / 2f, format);
            x += (int)Math.Ceiling(labelSize.Width) + gap;
        }

        _pillRect = new Rectangle(x, (height - pillHeight) / 2, pillWidth, pillHeight);
        using (var pillFill = new SolidBrush(Color.FromArgb(235, 255, 255, 255)))
        using (var pillPath = RoundedRectangle(_pillRect, (int)(PillRadiusDip * scale)))
        {
            g.FillPath(pillFill, pillPath);
        }
        using (var pillText = new SolidBrush(Color.FromArgb(255, 32, 32, 32)))
        {
            g.DrawString(release, font, pillText, _pillRect.X + pillPadX, _pillRect.Y + (pillHeight - releaseSize.Height) / 2f, format);
        }

        return bitmap;
    }

    private IntPtr ChipWndProc(HWND hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_MOUSEACTIVATE:
                return (IntPtr)MA_NOACTIVATE;

            case WM_SETCURSOR:
            {
                var point = CursorInClient(hwnd);
                if (point is { } p && _pillRect.Contains(p))
                {
                    SetCursorNative(LoadCursorNative(IntPtr.Zero, (IntPtr)IDC_HAND));
                    return (IntPtr)1;
                }
                break;
            }

            case WM_LBUTTONDOWN:
            {
                var p = new Point((short)((long)lParam & 0xFFFF), (short)(((long)lParam >> 16) & 0xFFFF));
                if (_pillRect.Contains(p))
                {
                    ReleaseRequested?.Invoke();
                }
                return IntPtr.Zero;
            }
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private static Point? CursorInClient(HWND hwnd)
    {
        if (!GetCursorPos(out var screen))
        {
            return null;
        }
        var p = new POINT(screen.X, screen.Y);
        ScreenToClient(hwnd, ref p);
        return new Point(p.X, p.Y);
    }

    private static void Present(HWND hwnd, Bitmap bitmap, Point location)
    {
        if (hwnd == HWND.NULL)
        {
            return;
        }

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
            if (!UpdateLayeredWindow((IntPtr)hwnd, screenDc, ref destination, ref size, memoryDc, ref source, 0, ref blend, ULW_ALPHA))
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
        SetWindowPos(hwnd, HWND.HWND_TOPMOST, 0, 0, 0, 0,
            SetWindowPosFlags.SWP_NOMOVE | SetWindowPosFlags.SWP_NOSIZE | SetWindowPosFlags.SWP_NOACTIVATE | SetWindowPosFlags.SWP_SHOWWINDOW);
    }

    private static Color AccentColor()
    {
        var accent = new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent);
        return Color.FromArgb(accent.A, accent.R, accent.G, accent.B);
    }

    private static System.Drawing.FontFamily IconFontFamily()
    {
        foreach (var name in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
        {
            try
            {
                return new System.Drawing.FontFamily(name);
            }
            catch (ArgumentException)
            {
                // Not installed; try the next one.
            }
        }
        return System.Drawing.FontFamily.GenericSansSerif;
    }

    private static GraphicsPath BottomRoundedRectangle(Rectangle bounds, int radius)
    {
        var diameter = Math.Max(1, radius * 2);
        var path = new GraphicsPath();
        path.AddLine(bounds.Left, bounds.Top, bounds.Right, bounds.Top);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
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

    [DllImport("user32.dll", EntryPoint = "LoadCursorW")]
    private static extern IntPtr LoadCursorNative(IntPtr hInstance, IntPtr lpCursorName);

    [DllImport("user32.dll", EntryPoint = "SetCursor")]
    private static extern IntPtr SetCursorNative(IntPtr hCursor);

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
