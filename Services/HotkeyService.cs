using System.Runtime.InteropServices;
using Vanara.PInvoke;
using static Vanara.PInvoke.User32;

namespace ScreenshotBooth.Services;

/// <summary>
/// Registers a global hotkey (default Win+Shift+B) that fires even while ScreenshotBooth isn't
/// the foreground app.
///
/// This uses a dedicated hidden message-only window (HWND_MESSAGE) on its own background thread
/// with its own GetMessage pump, rather than subclassing the booth window's own HWND. Classic
/// SetWindowLong(GWL_WNDPROC) subclassing of a WinUI 3 window is unsafe: WinUI 3's window is
/// composition/input-site backed with its own internal message routing, and replacing its WNDPROC
/// crashes the app (confirmed while building this - Microsoft.UI.Xaml.dll faulted with
/// STATUS_INVALID_CRUNTIME_PARAMETER as soon as the hotkey fired). A fully separate message-only
/// window sidesteps the framework's window entirely.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int HotkeyId = 0xB007;
    private const string WindowClassName = "ScreenshotBooth.HotkeyMessageWindow";

    private readonly Thread _pumpThread;
    private readonly ManualResetEventSlim _windowReady = new(initialState: false);

    // Keeps the callback delegate rooted for the pump thread's lifetime.
    private WindowProc? _wndProc;

    private HWND _messageHwnd = HWND.NULL;

    public event EventHandler? HotkeyPressed;

    public HotkeyService()
    {
        _pumpThread = new Thread(RunMessageLoop) { IsBackground = true, Name = "ScreenshotBooth-HotkeyPump" };
        _pumpThread.SetApartmentState(ApartmentState.STA);
        _pumpThread.Start();
        _windowReady.Wait();
    }

    /// <summary>Registers (or re-registers, replacing any previous binding) the global hotkey.</summary>
    public bool Register(uint modifiers, uint virtualKey)
    {
        if (_messageHwnd == HWND.NULL)
        {
            return false;
        }

        UnregisterHotKey(_messageHwnd, HotkeyId);
        return RegisterHotKey(_messageHwnd, HotkeyId, (HotKeyModifiers)modifiers, virtualKey);
    }

    private void RunMessageLoop()
    {
        var hInstance = (HINSTANCE)Marshal.GetHINSTANCE(typeof(HotkeyService).Module);
        _wndProc = WndProc;

        var wndClass = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = _wndProc,
            hInstance = hInstance,
            lpszClassName = WindowClassName,
        };
        RegisterClassEx(wndClass);

        _messageHwnd = CreateWindowEx(0, WindowClassName, "", 0, 0, 0, 0, 0,
            HWND.HWND_MESSAGE, HMENU.NULL, hInstance, IntPtr.Zero);

        _windowReady.Set();

        while (GetMessage(out var msg, HWND.NULL, 0, 0) > 0)
        {
            TranslateMessage(msg);
            DispatchMessage(msg);
        }
    }

    private IntPtr WndProc(HWND hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch ((WindowMessage)msg)
        {
            case WindowMessage.WM_HOTKEY when (int)wParam == HotkeyId:
                HotkeyPressed?.Invoke(this, EventArgs.Empty);
                return IntPtr.Zero;

            case WindowMessage.WM_CLOSE:
                DestroyWindow(hwnd);
                return IntPtr.Zero;

            case WindowMessage.WM_DESTROY:
                PostQuitMessage(0);
                return IntPtr.Zero;

            default:
                return DefWindowProc(hwnd, msg, wParam, lParam);
        }
    }

    public void Unregister()
    {
        if (_messageHwnd != HWND.NULL)
        {
            UnregisterHotKey(_messageHwnd, HotkeyId);
        }
    }

    public void Dispose()
    {
        Unregister();

        if (_messageHwnd != HWND.NULL)
        {
            PostMessage(_messageHwnd, (uint)WindowMessage.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }
    }
}
