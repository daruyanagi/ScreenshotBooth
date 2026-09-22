using Microsoft.UI.Xaml;
using ScreenshotBooth.Models;
using ScreenshotBooth.Services;
using ScreenshotBooth.ViewModels;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace ScreenshotBooth;

/// <summary>
/// The application window: booth client area + toolbar, and nothing else. Owns the Win32-level
/// wiring (global hotkey, target-window interop via <see cref="BoothController"/>) that a pure
/// ViewModel shouldn't reach into directly; UI state and command logic live in
/// <see cref="BoothViewModel"/>.
/// </summary>
public sealed partial class MainWindow : Window
{
    public BoothViewModel ViewModel { get; }

    private readonly AppSettings _settings;
    private readonly HotkeyService _hotkeyService;

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

        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");

        BoothArea.Loaded += (_, _) => ViewModel.BoothAreaElement = BoothArea;

        _hotkeyService = new HotkeyService();
        _hotkeyService.HotkeyPressed += OnHotkeyPressed;
        if (!_hotkeyService.Register(_settings.HotkeyModifiers, _settings.HotkeyVirtualKey))
        {
            ViewModel.StatusMessage = "Couldn't register the global hotkey (it may be in use by another app).";
        }

        Closed += OnWindowClosed;
    }

    private void OnHotkeyPressed(object? sender, EventArgs e)
    {
        // WM_HOTKEY arrives on this window's own message pump (the UI thread), but marshal via
        // the dispatcher anyway so this stays correct if the subclassing ever moves off-thread.
        DispatcherQueue.TryEnqueue(() => ViewModel.AcquireTargetFromForeground());
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _hotkeyService.HotkeyPressed -= OnHotkeyPressed;
        _hotkeyService.Dispose();
    }

    /// <summary>x:Bind helper: bool -&gt; Visibility (WinUI 3 has no built-in bool/Visibility converter).</summary>
    public static Visibility BoolToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
}
