using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Vanara.PInvoke;
using static Vanara.PInvoke.User32;

namespace ScreenshotBooth;

/// <summary>
/// The small accent "chip" shown under the held target's frame: says the window is being held
/// and offers the Release button right there, instead of in the booth's toolbar. A borderless,
/// always-on-top tool window that never takes focus from the target.
/// </summary>
public sealed partial class TargetChipWindow : Window
{
    private const int FallbackWidthDip = 280;
    private const int FallbackHeightDip = 34;
    private const int SafetyDip = 4;

    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_NOACTIVATE = 0x08000000;
    private const int GWL_EXSTYLE = -20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWCP_DONOTROUND = 1;
    private const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);

    private readonly HWND _hwnd;
    private System.Drawing.Rectangle _frame;
    private int _gapPx;
    private bool _placed;

    public event Action? ReleaseRequested;

    public TargetChipWindow()
    {
        InitializeComponent();

        _hwnd = (HWND)WinRT.Interop.WindowNative.GetWindowHandle(this);

        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMinimizable = false;
        presenter.IsMaximizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);

        var exStyle = GetWindowLongPtr((IntPtr)_hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr((IntPtr)_hwnd, GWL_EXSTYLE, (IntPtr)(exStyle | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));

        // Square corners and no DWM border line: the chip hangs off the frame ring like a tab.
        var corner = DWMWCP_DONOTROUND;
        DwmSetWindowAttribute((IntPtr)_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        var borderColor = DWMWA_COLOR_NONE;
        DwmSetWindowAttribute((IntPtr)_hwnd, DWMWA_BORDER_COLOR, ref borderColor, sizeof(int));

        // Before the first show, control templates are not applied yet and Measure() under-reports
        // (the Button comes out at 0). Once the content is really laid out, size the window to it.
        Chip.SizeChanged += (_, _) =>
        {
            if (_placed && !WindowFitsContent())
            {
                Place();
            }
        };
    }

    /// <summary>Places the chip centered and attached to the bottom of the ring around <paramref name="frame"/> (physical px), topmost, without activating it.</summary>
    public void ShowBelow(System.Drawing.Rectangle frame, int gapPx)
    {
        _frame = frame;
        _gapPx = gapPx;
        _placed = true;
        Place();

        if (!AppWindow.IsVisible)
        {
            AppWindow.Show(activateWindow: false);
        }

        SetWindowPos(_hwnd, HWND.HWND_TOPMOST, 0, 0, 0, 0,
            SetWindowPosFlags.SWP_NOMOVE | SetWindowPosFlags.SWP_NOSIZE | SetWindowPosFlags.SWP_NOACTIVATE);
    }

    public void HideChip()
    {
        if (AppWindow.IsVisible)
        {
            AppWindow.Hide();
        }
    }

    private double Scale => GetDpiForWindow(_hwnd) / 96.0;

    private Windows.Foundation.Size NaturalSizeDip()
    {
        Chip.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var desired = Chip.DesiredSize;
        return new Windows.Foundation.Size(
            (desired.Width > 0 ? desired.Width : FallbackWidthDip) + SafetyDip,
            desired.Height > 0 ? desired.Height : FallbackHeightDip);
    }

    private bool WindowFitsContent()
    {
        var natural = NaturalSizeDip();
        var client = AppWindow.ClientSize;
        var scale = Scale;
        return natural.Width * scale <= client.Width + 1 && natural.Height * scale <= client.Height + 1;
    }

    private void Place()
    {
        var natural = NaturalSizeDip();
        var scale = Scale;
        var width = (int)Math.Ceiling(natural.Width * scale);
        var height = (int)Math.Ceiling(natural.Height * scale);

        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            _frame.X + (_frame.Width - width) / 2, _frame.Bottom + _gapPx, width, height));
    }

    private void OnReleaseClick(object sender, RoutedEventArgs e) => ReleaseRequested?.Invoke();

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
