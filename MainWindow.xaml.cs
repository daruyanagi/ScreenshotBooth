using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using ScreenshotBooth.Models;
using ScreenshotBooth.Services;
using ScreenshotBooth.ViewModels;

namespace ScreenshotBooth;

/// <summary>
/// The application window: booth client area + toolbar, and nothing else. Tray-resident: closing
/// hides the window, the global hotkey (or the tray menu) brings it back, and the process only
/// exits from the tray menu. Owns the Win32-level wiring (global hotkey, target-window interop via
/// <see cref="BoothController"/>); UI state and command logic live in <see cref="BoothViewModel"/>.
/// </summary>
public sealed partial class MainWindow : Window
{
    public BoothViewModel ViewModel { get; }

    private readonly AppSettings _settings;
    private readonly HotkeyService _hotkeyService;
    private bool _exitRequested;

    public MainWindow()
    {
        // Resolved and wired up BEFORE InitializeComponent() so the compiled x:Bind bindings
        // (refreshed at the end of InitializeComponent) see a fully-constructed, non-null
        // ViewModel on their first pass. The native HWND already exists at this point, since the
        // base Window() constructor has already run by the time this constructor body executes.
        _settings = SettingsService.Load();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var controller = new BoothController(hwnd);
        ViewModel = new BoothViewModel(controller, _settings);
        controller.ReleaseRequested += () => ViewModel.ReleaseCommand.Execute(null);
        controller.TargetLost += reason => ViewModel.OnTargetLost(reason);
        controller.TargetResized += () => ViewModel.OnTargetResized();
        ViewModel.Captured += () => CaptureEffect.Begin();

        InitializeComponent();
        Root.DataContext = ViewModel;
        FitToBoothToggle.DataContext = ViewModel;   // CommandBar items do not always inherit it

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));

        BoothArea.Loaded += (_, _) => ViewModel.BoothAreaElement = BoothArea;
        BoothArea.SizeChanged += (_, _) => ViewModel.OnBoothAreaLayoutUpdated();

        _hotkeyService = new HotkeyService();
        _hotkeyService.HotkeyPressed += OnHotkeyPressed;
        if (!_hotkeyService.Register(_settings.HotkeyModifiers, _settings.HotkeyVirtualKey))
        {
            ViewModel.NotifyHotkeyFailed();
        }

        // The tray menu (SecondWindow mode) runs on its own thread, so every window operation
        // triggered from it is marshalled back through DispatcherQueue.
        TrayIcon.LeftClickCommand = new RelayCommand(ShowBooth);
        TrayIcon.ForceCreate();

        AppWindow.Closing += OnAppWindowClosing;
        Closed += OnWindowClosed;

        // Minimizing the booth while a target is held would leave the target pinned with the booth gone.
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BoothViewModel.IsTargetPinned) && AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsMinimizable = !ViewModel.IsTargetPinned;
                presenter.IsMaximizable = !ViewModel.IsTargetPinned;
            }
        };

        // A previous run may have died while holding a window on top; repair it and own up to it.
        if (HeldTargetRecord.TryRecover() is { } recoveredTitle)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                // Exceptional enough to come to the front: the user should see the apology.
                ViewModel.EnsureBoothLaidOut();
                AppWindow.Show();
                Activate();
                ViewModel.NotifyRecoveredStuckTopMost(recoveredTitle);
            });
        }
    }

    private void OnHotkeyPressed(object? sender, EventArgs e)
    {
        AppLog.Write("Hotkey: pressed");
        var queued = DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                // Acquire the foreground window BEFORE showing ourselves, otherwise the booth would
                // become the foreground window and capture itself.
                ViewModel.AcquireTargetFromForeground();
                AppLog.Write($"Hotkey: acquired; pinned={ViewModel.IsTargetPinned} visible={AppWindow.IsVisible}");
                if (!AppWindow.IsVisible)
                {
                    AppWindow.Show(activateWindow: false);
                    AppLog.Write($"Hotkey: shown; visible={AppWindow.IsVisible}");
                }

                // Showing (or restoring) the booth inserts it at the top of its band - above the
                // target - so the intended order has to be applied after it.
                ViewModel.EnforceZOrder();
            }
            catch (Exception ex)
            {
                AppLog.Write($"Hotkey: handler failed {ex}");
            }
        });
        if (!queued)
        {
            AppLog.Write("Hotkey: TryEnqueue returned false");
        }
    }

    private void ShowBooth()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ViewModel.EnsureBoothLaidOut();
            AppWindow.Show();
            Activate();
        });
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_exitRequested)
        {
            return;
        }

        args.Cancel = true;
        ViewModel.OnBoothHidden();
        AppWindow.Hide();
    }

    private void OnTrayOpenClick(object sender, RoutedEventArgs e) => ShowBooth();

    private void OnTrayExitClick(object sender, RoutedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _exitRequested = true;
            ViewModel.OnBoothHidden();
            TrayIcon.Dispose();
            Application.Current.Exit();
        });
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _hotkeyService.HotkeyPressed -= OnHotkeyPressed;
        _hotkeyService.Dispose();
    }

    private void OnCountdownItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && int.TryParse(tag, out var seconds))
        {
            ViewModel.CountdownSeconds = seconds;
        }
    }

    /// <summary>x:Bind helper for RadioMenuFlyoutItem.IsChecked.</summary>
    public static bool IntEquals(int a, int b) => a == b;

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape && ViewModel.IsCountingDown)
        {
            ViewModel.CancelCountdown();
            e.Handled = true;
        }
    }

    /// <summary>x:Bind helper: false -&gt; Visible (for "off" badges).</summary>
    public static Visibility InvertedBoolToVisibility(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>x:Bind helper: bool -&gt; Visibility (WinUI 3 has no built-in bool/Visibility converter).</summary>
    public static Visibility BoolToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
}
