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
///
/// RegisterHotKey/UnregisterHotKey must be called on the thread that owns the window (otherwise
/// ERROR_WINDOW_OF_OTHER_THREAD), so callers on the UI thread hand the request to the pump thread
/// with SendMessage, which runs the WndProc there synchronously and returns the result.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int HotkeyId = 0xB007;
    private const string WindowClassName = "ScreenshotBooth.HotkeyMessageWindow";

    private const uint WM_APP = 0x8000;
    private const uint RegisterMsg = WM_APP + 1;
    private const uint UnregisterMsg = WM_APP + 2;
    private const uint WM_HOTKEY = 0x0312;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_DESTROY = 0x0002;

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

        return SendMessage(_messageHwnd, RegisterMsg, (IntPtr)modifiers, (IntPtr)virtualKey) != IntPtr.Zero;
    }

    public void Unregister()
    {
        if (_messageHwnd != HWND.NULL)
        {
            SendMessage(_messageHwnd, UnregisterMsg, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private void RunMessageLoop()
    {
        // GetModuleHandle(null) is the process image base; Marshal.GetHINSTANCE on a managed
        // module is not a usable HINSTANCE on .NET Core.
        var hInstance = (HINSTANCE)(IntPtr)Kernel32.GetModuleHandle(null);
        _wndProc = WndProc;

        var wndClass = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = _wndProc,
            hInstance = hInstance,
            lpszClassName = WindowClassName,
        };
        var atom = RegisterClassEx(wndClass);
        if (atom.IsInvalid)
        {
            AppLog.Write($"Hotkey: RegisterClassEx failed error={Marshal.GetLastWin32Error()}");
        }

        _messageHwnd = CreateWindowEx(0, WindowClassName, "", 0, 0, 0, 0, 0,
            HWND.HWND_MESSAGE, HMENU.NULL, hInstance, IntPtr.Zero);
        if (_messageHwnd == HWND.NULL)
        {
            AppLog.Write($"Hotkey: CreateWindowEx failed error={Marshal.GetLastWin32Error()}");
        }

        _windowReady.Set();

        while (GetMessage(out var msg, HWND.NULL, 0, 0) > 0)
        {
            TranslateMessage(msg);
            DispatchMessage(msg);
        }
    }

    private IntPtr WndProc(HWND hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case RegisterMsg:
            {
                var modifiers = (uint)(long)wParam;
                var virtualKey = (uint)(long)lParam;
                UnregisterHotKey(hwnd, HotkeyId);
                var ok = RegisterHotKey(hwnd, HotkeyId, (HotKeyModifiers)modifiers, virtualKey);
                AppLog.Write(ok
                    ? $"Hotkey: registered modifiers=0x{modifiers:X} vk=0x{virtualKey:X}"
                    : $"Hotkey: RegisterHotKey failed modifiers=0x{modifiers:X} vk=0x{virtualKey:X} error={Marshal.GetLastWin32Error()}");
                return ok ? (IntPtr)1 : IntPtr.Zero;
            }

            case UnregisterMsg:
                UnregisterHotKey(hwnd, HotkeyId);
                return IntPtr.Zero;

            case WM_HOTKEY when (int)(long)wParam == HotkeyId:
                HotkeyPressed?.Invoke(this, EventArgs.Empty);
                return IntPtr.Zero;

            case WM_CLOSE:
                DestroyWindow(hwnd);
                return IntPtr.Zero;

            case WM_DESTROY:
                PostQuitMessage(0);
                return IntPtr.Zero;

            default:
                return DefWindowProc(hwnd, msg, wParam, lParam);
        }
    }

    public void Dispose()
    {
        Unregister();

        if (_messageHwnd != HWND.NULL)
        {
            PostMessage(_messageHwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }
    }
}
