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

    // Whether the target was already always-on-top before we held it; releasing must not take
    // that away from a window that had it on its own.
    private bool _targetWasTopMost;

    // Only one window is ever held topmost by this app. Process-level exit handlers reach the
    // live controller through this so the window is restored even on abnormal shutdown paths.
    private static BoothController? _current;

    // Where the target was last centered, so a user drag can be snapped back (the target is
    // effectively immovable while the booth holds it).
    private Point? _lastAreaCenter;
    private Rectangle? _lastAreaRect;

    private readonly TargetFrameOverlay _frame;

    // Where the booth was last placed (by us, or by a user resize): while a target is held, a
    // plain drag of the booth snaps back here so the booth and target cannot drift apart.
    private Windows.Graphics.PointInt32? _lockedBoothPosition;
    private Windows.Graphics.SizeInt32? _lockedBoothSize;

    /// <summary>Raised when the Release button on the target chip is clicked.</summary>
    public event Action? ReleaseRequested;

    /// <summary>Raised after the user finished resizing the held target (already clamped and re-centered).</summary>
    public event Action? TargetResized;

    /// <summary>Raised when the held target is minimized or closed by the user, so the session should end.</summary>
    public event Action<TargetLostReason>? TargetLost;
    private readonly WinEventDelegate _winEventProc;   // rooted for the hooks' lifetime
    private readonly IntPtr _moveSizeHook;
    private readonly IntPtr _foregroundHook;
    private readonly IntPtr _minimizeHook;
    private readonly IntPtr _destroyHook;
    private readonly IntPtr _locationHook;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;

    // Self-healing z-order: while a target is held, a light watchdog checks that the booth still
    // sits directly under the target and repairs the order only when something got in between.
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _orderWatchdog;

    public BoothController(nint boothWindowHandle)
    {
        _boothHwnd = (HWND)boothWindowHandle;

        // Derived purely from the HWND via Win32 interop rather than the Window.AppWindow
        // property, so this can be constructed safely before InitializeComponent() has run
        // (the underlying native window already exists once the Window base constructor returns).
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(boothWindowHandle);
        _boothAppWindow = AppWindow.GetFromWindowId(windowId);

        _current = this;
        _frame = new TargetFrameOverlay();
        _frame.ReleaseRequested += () => ReleaseRequested?.Invoke();

        // EVENT_SYSTEM_MOVESIZEEND (0x000B): fires on this (UI) thread when a user move/resize of
        // any window ends; we only react for the held target.
        _winEventProc = OnWinEvent;
        _moveSizeHook = SetWinEventHookNative(0x000B, 0x000B, IntPtr.Zero, _winEventProc, 0, 0, 0);

        // EVENT_SYSTEM_FOREGROUND (0x0003): activation reshuffles the topmost band, so the booth /
        // target / indicator order is re-applied whenever the foreground window changes.
        _foregroundHook = SetWinEventHookNative(0x0003, 0x0003, IntPtr.Zero, _winEventProc, 0, 0, 0);

        // EVENT_SYSTEM_MINIMIZESTART (0x0016) and EVENT_OBJECT_DESTROY (0x8001): a held target that
        // gets minimized or closed would otherwise leave the ring and chip hanging in the air.
        _minimizeHook = SetWinEventHookNative(0x0016, 0x0016, IntPtr.Zero, _winEventProc, 0, 0, 0);
        _destroyHook = SetWinEventHookNative(0x8001, 0x8001, IntPtr.Zero, _winEventProc, 0, 0, 0);

        // EVENT_OBJECT_LOCATIONCHANGE (0x800B): there is no dedicated maximize event, so a held
        // target that becomes maximized (button or snap) is caught here and released.
        _locationHook = SetWinEventHookNative(0x800B, 0x800B, IntPtr.Zero, _winEventProc, 0, 0, 0);
        _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _orderWatchdog = _dispatcher.CreateTimer();
        _orderWatchdog.Interval = TimeSpan.FromMilliseconds(250);
        _orderWatchdog.IsRepeating = true;
        _orderWatchdog.Tick += (_, _) =>
        {
            if (_isPinned && HasTarget && !IsZOrderIntact(out var intruder))
            {
                AppLog.Write($"ZOrder: repaired (intruder {intruder})");
                EnforceZOrder();
            }
        };
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

        // Switching targets restores the previous one first: never more than one window is held.
        var sameWindow = HasTarget && _targetHwnd == fg;
        if (HasTarget && !sameWindow)
        {
            SetTargetTopMost(false);
        }

        _targetHwnd = fg;
        // Re-acquiring the window we already hold must keep its ORIGINAL state: sampling it now would
        // read back the topmost flag we set ourselves and the window would never be released.
        if (!sameWindow)
        {
            _targetWasTopMost = (GetWindowLongPtr((IntPtr)fg, GWL_EXSTYLE).ToInt64() & WS_EX_TOPMOST) != 0;
        }
        var title = new System.Text.StringBuilder(256);
        GetWindowText(fg, title, title.Capacity);
        AppLog.Write($"Acquire: target=0x{(nint)fg:X} \"{title}\" bounds={GetTargetExtendedFrameBounds()}");
        return true;
    }

    /// <summary>Centers the target on <paramref name="display"/> at its current size and returns its actual bounds.</summary>
    public Rectangle CenterTarget(DisplayArea display)
    {
        // A maximized window cannot be centered and would be released again at once (maximize ends
        // a session), so bring it back to normal first.
        if (IsZoomed(_targetHwnd))
        {
            ShowWindow(_targetHwnd, ShowWindowCommand.SW_RESTORE);
        }

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

        // The window is a little bigger than its client area (invisible resize borders).
        var extraW = Math.Max(0, _boothAppWindow.Size.Width - _boothAppWindow.ClientSize.Width);
        var extraH = Math.Max(0, _boothAppWindow.Size.Height - _boothAppWindow.ClientSize.Height);
        return new Size(Math.Max(1, work.Width - extraW), Math.Max(1, work.Height - chromePx - extraH));
    }

    /// <summary>True once the booth has been positioned by us at least once (before that it has WinUI's default size).</summary>
    public bool HasLaidOut => _lastAreaRect is not null;

    /// <summary>Re-applies the last booth area size (e.g. after the chrome was measured for real), keeping the target centered.</summary>
    public void RelayoutWithLastArea(DisplayArea display)
    {
        if (_lastAreaRect is { } area)
        {
            LayoutBoothWithAreaSize(area.Width, area.Height, display, recordLayoutSize: true);
        }
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
            HideIndicators();
            return;
        }

        if (topMost || !_targetWasTopMost)
        {
            SetWindowPos(_targetHwnd, topMost ? HWND.HWND_TOPMOST : HWND.HWND_NOTOPMOST,
                0, 0, 0, 0,
                SetWindowPosFlags.SWP_NOMOVE | SetWindowPosFlags.SWP_NOSIZE | SetWindowPosFlags.SWP_NOACTIVATE);
        }

        // While holding, the booth joins the topmost band right under the target so no other window
        // can slip in between them; it leaves the band again on release.
        SetWindowPos(_boothHwnd, topMost ? HWND.HWND_TOPMOST : HWND.HWND_NOTOPMOST, 0, 0, 0, 0,
            SetWindowPosFlags.SWP_NOMOVE | SetWindowPosFlags.SWP_NOSIZE | SetWindowPosFlags.SWP_NOACTIVATE);

        // Persist which window we put on top, so a crash can be repaired on the next run.
        if (topMost && !_targetWasTopMost)
        {
            var title = new System.Text.StringBuilder(256);
            GetWindowText(_targetHwnd, title, title.Capacity);
            HeldTargetRecord.Save((nint)_targetHwnd, title.ToString());
        }
        else if (!topMost)
        {
            HeldTargetRecord.Clear();
        }

        if (topMost)
        {
            ShowIndicators();
            EnforceZOrder();
            _orderWatchdog.Start();
        }
        else
        {
            _orderWatchdog.Stop();
            HideIndicators();
        }
    }

    /// <summary>
    /// True when, walking down the z-order from the target, the first visible window that is not
    /// one of ours is the booth. <paramref name="intruder"/> describes what was found instead.
    /// </summary>
    private bool IsZOrderIntact(out string intruder)
    {
        intruder = "";
        GetWindowThreadProcessId(_boothHwnd, out var ourPid);
        var next = GetWindow(_targetHwnd, GetWindowCmd.GW_HWNDNEXT);
        while (next != HWND.NULL)
        {
            if (IsWindowVisible(next))
            {
                if (next == _boothHwnd)
                {
                    return true;
                }

                GetWindowThreadProcessId(next, out var pid);
                if (pid != ourPid)
                {
                    var title = new System.Text.StringBuilder(128);
                    GetWindowText(next, title, title.Capacity);
                    intruder = $"0x{(nint)next:X} \"{title}\" pid={pid}";
                    return false;
                }
            }
            next = GetWindow(next, GetWindowCmd.GW_HWNDNEXT);
        }

        intruder = "booth not found below the target";
        return false;
    }

    /// <summary>
    /// Within the topmost band: chip and ring on top, then the target, then the booth directly under
    /// it. Must be re-applied after anything that re-inserts a window into the band: activation
    /// (handled by the foreground hook) and showing/restoring the booth (callers do this explicitly).
    /// </summary>
    public void EnforceZOrder()
    {
        if (!_isPinned || !HasTarget)
        {
            return;
        }

        const SetWindowPosFlags flags = SetWindowPosFlags.SWP_NOMOVE | SetWindowPosFlags.SWP_NOSIZE | SetWindowPosFlags.SWP_NOACTIVATE;
        SetWindowPos(_targetHwnd, HWND.HWND_TOPMOST, 0, 0, 0, 0, flags);
        _frame.BringToTop();
        SetWindowPos(_boothHwnd, _targetHwnd, 0, 0, 0, 0, flags);
    }

    /// <summary>Lets go of the held window entirely (un-topmost, indicators hidden, nothing tracked).</summary>
    public void ReleaseHeldTarget()
    {
        if (HasTarget)
        {
            SetTargetTopMost(false);
        }
        else
        {
            // The window is already gone: just drop our side of it.
            _isPinned = false;
            HideIndicators();
            SetWindowPos(_boothHwnd, HWND.HWND_NOTOPMOST, 0, 0, 0, 0,
                SetWindowPosFlags.SWP_NOMOVE | SetWindowPosFlags.SWP_NOSIZE | SetWindowPosFlags.SWP_NOACTIVATE);
            HeldTargetRecord.Clear();
        }
        _targetHwnd = HWND.NULL;
        _isPinned = false;
    }

    /// <summary>For exit paths (ProcessExit, unhandled exceptions, window close): make sure no window is left topmost.</summary>
    public static void ReleaseHeldTargetOnExit()
    {
        try
        {
            _current?.ReleaseHeldTarget();
        }
        catch
        {
            // Best effort while shutting down.
        }
    }

    /// <summary>Hides the frame ring and chip without releasing the target - called right before a capture so neither is in the shot.</summary>
    public void HideTargetFrame() => HideIndicators();

    private void ShowIndicators() => _frame.Show(GetTargetExtendedFrameBounds(), DpiScale);

    private void HideIndicators() => _frame.Hide();

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint eventThread, uint eventTime)
    {
        if (!_isPinned)
        {
            return;
        }

        if (eventType == 0x800B)
        {
            if (hwnd == (IntPtr)_targetHwnd && idObject == 0 && IsZoomed(_targetHwnd))
            {
                _dispatcher.TryEnqueue(() =>
                {
                    if (_isPinned && HasTarget && IsZoomed(_targetHwnd))
                    {
                        ReleaseHeldTarget();
                        TargetLost?.Invoke(TargetLostReason.Maximized);
                    }
                });
            }
            return;
        }

        if (hwnd == (IntPtr)_targetHwnd && (eventType == 0x0016 || (eventType == 0x8001 && idObject == 0)))
        {
            var reason = eventType == 0x0016 ? TargetLostReason.Minimized : TargetLostReason.Closed;
            _dispatcher.TryEnqueue(() =>
            {
                if (_isPinned)
                {
                    ReleaseHeldTarget();
                    TargetLost?.Invoke(reason);
                }
            });
            return;
        }

        if (eventType == 0x0003)
        {
            // Foreground changed (target, booth or anything else): put the band back in order,
            // outside of the hook callback.
            _dispatcher.TryEnqueue(EnforceZOrder);
            return;
        }

        if (hwnd == (IntPtr)_targetHwnd && _lastAreaCenter is { } center)
        {
            // A user move snaps back; a user resize is clamped so the target never outgrows the area.
            ClampTargetToArea();
            CenterTargetAt(center.X, center.Y);
            TargetResized?.Invoke();
        }
        else if (hwnd == (IntPtr)_boothHwnd)
        {
            var size = _boothAppWindow.Size;
            var position = _boothAppWindow.Position;
            var sameSize = _lockedBoothSize is { } ls && ls.Width == size.Width && ls.Height == size.Height;
            if (sameSize && _lockedBoothPosition is { } lp && (lp.X != position.X || lp.Y != position.Y))
            {
                // A plain move: put the booth back.
                _boothAppWindow.Move(lp);
                return;
            }

            // A resize (allowed): accept the new geometry and keep the target centered in the area.
            _lockedBoothPosition = position;
            _lockedBoothSize = size;
            RecenterTargetInArea();
        }
    }

    /// <summary>Shrinks the target to the booth area minus the shadow margin if a user resize made it bigger than that.</summary>
    private void ClampTargetToArea()
    {
        if (!HasTarget || _lastAreaRect is not { } area)
        {
            return;
        }

        var marginPx = (int)(MarginDip * DpiScale);
        var maxWidth = Math.Max(1, area.Width - marginPx * 2);
        var maxHeight = Math.Max(1, area.Height - marginPx * 2);
        var frame = GetTargetExtendedFrameBounds();
        if (frame.Width <= maxWidth && frame.Height <= maxHeight)
        {
            return;
        }

        var width = Math.Min(frame.Width, maxWidth);
        var height = Math.Min(frame.Height, maxHeight);
        var insets = GetTargetFrameInsets();
        GetWindowRect(_targetHwnd, out RECT rect);
        SetWindowPos(_targetHwnd, HWND.NULL, rect.left, rect.top,
            width + insets.Left + insets.Right, height + insets.Top + insets.Bottom,
            SetWindowPosFlags.SWP_NOZORDER | SetWindowPosFlags.SWP_NOACTIVATE);
    }

    /// <summary>Centers the target in the booth area as it currently is on screen.</summary>
    private void RecenterTargetInArea()
    {
        var scale = DpiScale;
        var chromeTopPx = (int)(_chromeTopDip * scale);
        var chromeBottomPx = (int)(_chromeBottomDip * scale);
        var client = _boothAppWindow.ClientSize;
        var origin = new POINT(0, 0);
        ClientToScreen(_boothHwnd, ref origin);
        var areaH = Math.Max(1, client.Height - chromeTopPx - chromeBottomPx);
        _lastAreaCenter = new Point(origin.X + client.Width / 2, origin.Y + chromeTopPx + areaH / 2);
        CenterTargetAt(_lastAreaCenter.Value.X, _lastAreaCenter.Value.Y);
    }

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOPMOST = 0x00000008;

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

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
    public Rectangle FitTargetToArea(FrameworkElement boothArea) => FitTargetToRect(GetScreenRectOfElement(boothArea));

    /// <summary>Fits the target into the booth area as last laid out - usable before the XAML area has been rendered (first hotkey press).</summary>
    public Rectangle FitTargetToLayoutArea() => _lastAreaRect is { } area ? FitTargetToRect(area) : GetTargetExtendedFrameBounds();

    private Rectangle FitTargetToRect(Rectangle area)
    {
        // A maximized window ignores SetWindowPos sizing; bring it back to normal first.
        if (IsZoomed(_targetHwnd))
        {
            ShowWindow(_targetHwnd, ShowWindowCommand.SW_RESTORE);
        }

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
        // A minimized booth reports a bogus (-32000,-32000) geometry, which would send the target
        // off-screen; restore it first without activating it.
        if (IsIconic(_boothHwnd))
        {
            ShowWindow(_boothHwnd, ShowWindowCommand.SW_SHOWNOACTIVATE);
            EnforceZOrder();
        }

        var scale = DpiScale;
        var chromeTopPx = (int)(_chromeTopDip * scale);
        var chromeBottomPx = (int)(_chromeBottomDip * scale);
        var max = GetMaxAreaSize(display);
        areaW = Math.Clamp(areaW, 1, max.Width);
        areaH = Math.Clamp(areaH, 1, max.Height);

        var wantedClient = new Windows.Graphics.SizeInt32(areaW, areaH + chromeTopPx + chromeBottomPx);
        _boothAppWindow.ResizeClient(wantedClient);

        // ResizeClient adds the caption height even though the content extends into the title bar
        // (+31px here), so read back what we actually got and compensate once.
        var got = _boothAppWindow.ClientSize;
        if (got.Width != wantedClient.Width || got.Height != wantedClient.Height)
        {
            _boothAppWindow.ResizeClient(new Windows.Graphics.SizeInt32(
                wantedClient.Width * 2 - got.Width, wantedClient.Height * 2 - got.Height));
        }
        AppLog.Write($"Layout: area={areaW}x{areaH} chrome={chromeTopPx}+{chromeBottomPx} wanted={wantedClient.Width}x{wantedClient.Height} first={got.Width}x{got.Height} -> client={_boothAppWindow.ClientSize.Width}x{_boothAppWindow.ClientSize.Height}");

        var work = display.WorkArea;
        var size = _boothAppWindow.Size;
        var x = work.X + (work.Width - size.Width) / 2;
        var y = work.Y + (work.Height - size.Height) / 2;
        _boothAppWindow.Move(new Windows.Graphics.PointInt32(x, y));

        LastLayoutSize = recordLayoutSize ? size : null;
        _lockedBoothPosition = new Windows.Graphics.PointInt32(x, y);
        _lockedBoothSize = size;

        // Center the target in the booth AREA (not the window), so the margins are even.
        var origin = new POINT(0, 0);
        ClientToScreen(_boothHwnd, ref origin);
        _lastAreaRect = new Rectangle(origin.X, origin.Y + chromeTopPx, areaW, areaH);
        _lastAreaCenter = new Point(origin.X + areaW / 2, origin.Y + chromeTopPx + areaH / 2);
        CenterTargetAt(_lastAreaCenter.Value.X, _lastAreaCenter.Value.Y);

        // AppWindow.ResizeClient/Move re-insert the booth at the top of its band (above the target).
        EnforceZOrder();

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
            ShowIndicators();
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

public enum TargetLostReason
{
    Minimized,
    Maximized,
    Closed,
}
