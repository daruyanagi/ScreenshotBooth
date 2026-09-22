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

    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_NOACTIVATE = 0x08000000;
    private const int GWL_EXSTYLE = -20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUNDSMALL = 3;

    private readonly HWND _hwnd;

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

        var corner = DWMWCP_ROUNDSMALL;
        DwmSetWindowAttribute((IntPtr)_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
    }

    /// <summary>Places the chip centered under <paramref name="frame"/> (physical px) and shows it topmost without activating it.</summary>
    public void ShowBelow(System.Drawing.Rectangle frame, int gapPx)
    {
        var scale = GetDpiForWindow(_hwnd) / 96.0;

        Chip.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var desired = Chip.DesiredSize;
        var widthDip = desired.Width > 0 ? desired.Width : FallbackWidthDip;
        var heightDip = desired.Height > 0 ? desired.Height : FallbackHeightDip;
        var width = (int)Math.Ceiling(widthDip * scale);
        var height = (int)Math.Ceiling(heightDip * scale);

        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            frame.X + (frame.Width - width) / 2, frame.Bottom + gapPx, width, height));

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

    private void OnReleaseClick(object sender, RoutedEventArgs e) => ReleaseRequested?.Invoke();

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
