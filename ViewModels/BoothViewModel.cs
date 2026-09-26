using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using ScreenshotBooth.Models;
using ScreenshotBooth.Services;

namespace ScreenshotBooth.ViewModels;

/// <summary>
/// UI state and commands for the single-window booth: booth-size selection, shutter/retake,
/// copy/save/share and in-app notices. Actual Win32/DWM/screen-capture work is delegated to
/// <see cref="BoothController"/>, injected by MainWindow, so this class stays UI-focused.
/// </summary>
public partial class BoothViewModel : ObservableObject
{
    private readonly BoothController _controller;
    private readonly AppSettings _settings;

    private byte[]? _lastCapturePngBytes;
    private DateTime _lastCaptureTime;
    private bool _isApplyingPresetProgrammatically;

    /// <summary>Built-in presets that fit on the selected display, plus "Custom" (the acquire-time size). See <see cref="RefreshPresets"/>.</summary>
    public ObservableCollection<TargetSizePreset> Presets { get; } = new();

    /// <summary>The preset matching the booth's current size, or null when it matches none (the ComboBox then shows <see cref="CurrentSizeText"/>).</summary>
    [ObservableProperty]
    public partial TargetSizePreset? SelectedPreset { get; set; }

    /// <summary>The booth area's current size as "WxH", shown when it matches no preset.</summary>
    [ObservableProperty]
    public partial string CurrentSizeText { get; set; } = "";

    /// <summary>What the size picker's face shows: the selected preset, or the raw current size.</summary>
    public string SizeLabel => SelectedPreset?.ToString() ?? CurrentSizeText;

    partial void OnCurrentSizeTextChanged(string value) => OnPropertyChanged(nameof(SizeLabel));

    /// <summary>User picked a size from the menu.</summary>
    public void SelectPreset(TargetSizePreset preset)
    {
        var wasProgrammatic = _isApplyingPresetProgrammatically;
        _isApplyingPresetProgrammatically = false;
        SelectedPreset = preset;
        _isApplyingPresetProgrammatically = wasProgrammatic;
    }

    // The 4:3 size the booth was given when the target was acquired; offered as the "Custom" preset.
    private System.Drawing.Size? _initialArea;

    /// <summary>Seconds to count down before capturing; 0 captures immediately.</summary>
    [ObservableProperty]
    public partial int CountdownSeconds { get; set; }

    /// <summary>The seconds shown as a badge on the countdown button ("" when off).</summary>
    public string CountdownBadge => CountdownSeconds > 0 ? CountdownSeconds.ToString() : "";

    public bool HasCountdown => CountdownSeconds > 0;

    /// <summary>True while the pre-capture countdown is running; the shutter then shows the remaining seconds and acts as Cancel.</summary>
    [ObservableProperty]
    public partial bool IsCountingDown { get; set; }

    [ObservableProperty]
    public partial int CountdownRemaining { get; set; }

    private CancellationTokenSource? _countdownCts;

    /// <summary>Explains the current countdown setting (shown as the button's tooltip).</summary>
    public string CountdownToolTip => CountdownSeconds == 0
        ? R.Get("CountdownTipOff")
        : R.F("CountdownTipSeconds", CountdownSeconds);

    partial void OnCountdownSecondsChanged(int value)
    {
        OnPropertyChanged(nameof(CountdownToolTip));
        OnPropertyChanged(nameof(CountdownBadge));
        OnPropertyChanged(nameof(HasCountdown));
        _settings.DefaultCountdownSeconds = value;
        SettingsService.Save(_settings);
    }

    /// <summary>Raised right after a capture is shown, for the view to play its "photo taken" effect.</summary>
    public event Action? Captured;

    [ObservableProperty]
    public partial BitmapImage? PreviewImage { get; set; }

    [ObservableProperty]
    public partial bool IsPreviewShown { get; set; }

    /// <summary>True while the target window is being held always-on-top (and framed) by the booth.</summary>
    [ObservableProperty]
    public partial bool IsTargetPinned { get; set; }

    /// <summary>The booth is empty: no target held and no capture shown, so tell the user how to start.</summary>
    public bool IsIdleHintVisible => !IsPreviewShown && !IsTargetPinned;

    partial void OnIsTargetPinnedChanged(bool value) => OnPropertyChanged(nameof(IsIdleHintVisible));

    partial void OnIsPreviewShownChanged(bool value) => OnPropertyChanged(nameof(IsIdleHintVisible));

    /// <summary>Put the target back where it was once the booth lets go of it (persisted).</summary>
    [ObservableProperty]
    public partial bool IsRestoreLayoutEnabled { get; set; }

    partial void OnIsRestoreLayoutEnabledChanged(bool value)
    {
        _controller.RestoreTargetLayout = value;
        _settings.RestoreTargetLayout = value;
        SettingsService.Save(_settings);
    }

    /// <summary>True while the window picker overlay is up.</summary>
    [ObservableProperty]
    public partial bool IsPicking { get; set; }

    /// <summary>Fit mode: while on, resizing the booth resizes the target to fill it. Per session, off by default.</summary>
    [ObservableProperty]
    public partial bool IsFitToBoothEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsNoticeOpen { get; set; }

    [ObservableProperty]
    public partial string NoticeTitle { get; set; } = "";

    [ObservableProperty]
    public partial string NoticeMessage { get; set; } = "";

    [ObservableProperty]
    public partial InfoBarSeverity NoticeSeverity { get; set; } = InfoBarSeverity.Informational;

    private readonly DispatcherQueueTimer _noticeTimer;

    /// <summary>
    /// The XAML element for the white booth backdrop. The view sets this after Loaded so
    /// BoothController can compute its exact on-screen rect at capture time.
    /// </summary>
    public FrameworkElement? BoothAreaElement { get; set; }

    public BoothViewModel(BoothController controller, AppSettings settings)
    {
        _controller = controller;
        _settings = settings;

        _noticeTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _noticeTimer.Interval = TimeSpan.FromSeconds(5);
        _noticeTimer.IsRepeating = false;
        _noticeTimer.Tick += (_, _) => IsNoticeOpen = false;

        // Programmatic: the setters below must only initialize the controls, never resize anything.
        _isApplyingPresetProgrammatically = true;

        RefreshPresets();
        SelectedPreset = Presets.FirstOrDefault(p => p.Width == settings.BoothWidth && p.Height == settings.BoothHeight);
        CountdownSeconds = settings.DefaultCountdownSeconds;
        IsRestoreLayoutEnabled = settings.RestoreTargetLayout;
        _controller.RestoreTargetLayout = settings.RestoreTargetLayout;
        _controller.MarginDip = settings.MarginDip;

        _isApplyingPresetProgrammatically = false;
    }

    partial void OnSelectedPresetChanged(TargetSizePreset? value)
    {
        OnPropertyChanged(nameof(SizeLabel));
        // Null: the ComboBox clearing itself while Presets is rebuilt, or a size matching no preset.
        // Programmatic selection (startup, preset refresh, syncing to the real size) must only update
        // the control - never resize the booth.
        if (value is null || _isApplyingPresetProgrammatically)
        {
            return;
        }

        ApplyBoothSize(value.Width, value.Height);
    }

    /// <summary>Applies a booth-area size and persists it. The target only follows in fit mode.</summary>
    private void ApplyBoothSize(int width, int height)
    {
        _settings.BoothWidth = width;
        _settings.BoothHeight = height;
        SettingsService.Save(_settings);

        // Not "our" layout: the booth-area layout handler then syncs the controls and applies fit mode.
        var display = DisplayService.GetSelectedDisplay(_settings);
        _controller.LayoutBoothWithAreaSize(width, height, display, recordLayoutSize: false);
    }

    /// <summary>
    /// Rebuilds <see cref="Presets"/> to the built-in sizes that fit the selected display plus the
    /// acquire-time "Custom" size. Keeps the current selection when it is still listed.
    /// </summary>
    /// <returns>True when the list was rebuilt (the ComboBox then needs a layout pass before a new selection sticks).</returns>
    private bool RefreshPresets()
    {
        var display = DisplayService.GetSelectedDisplay(_settings);
        var max = _controller.GetMaxAreaSize(display);
        var fitting = TargetSizePreset.FromSettings(_settings)
            .Where(p => p.Width <= max.Width && p.Height <= max.Height)
            .ToList();
        if (_initialArea is { } initial && !fitting.Any(p => p.Width == initial.Width && p.Height == initial.Height))
        {
            fitting.Add(TargetSizePreset.Custom(initial.Width, initial.Height));
        }

        // With fit off, a size smaller than the held target (plus margin) would leave it sticking out
        // of the booth: keep it selectable, but flag it.
        if (IsTargetPinned && !IsFitToBoothEnabled && _controller.GetMinAreaForTarget() is { } min)
        {
            fitting = fitting
                .Select(p => p with { Overflows = p.Width < min.Width || p.Height < min.Height })
                .ToList();
        }

        if (Presets.SequenceEqual(fitting))
        {
            return false;
        }

        var previous = SelectedPreset;
        var wasProgrammatic = _isApplyingPresetProgrammatically;
        _isApplyingPresetProgrammatically = true;
        Presets.Clear();
        foreach (var preset in fitting)
        {
            Presets.Add(preset);
        }

        SelectedPreset = previous is null ? null : Presets.FirstOrDefault(p => p.SameSizeAs(previous));
        _isApplyingPresetProgrammatically = wasProgrammatic;
        return true;
    }

    /// <summary>Reflects the booth area's actual size in the size picker without changing anything.</summary>
    private void SyncSizeControlsToArea(System.Drawing.Size area)
    {
        var wasProgrammatic = _isApplyingPresetProgrammatically;
        _isApplyingPresetProgrammatically = true;
        SelectedPreset = Presets.FirstOrDefault(p => p.Width == area.Width && p.Height == area.Height);
        CurrentSizeText = $"{area.Width}x{area.Height}";
        _isApplyingPresetProgrammatically = wasProgrammatic;
    }

    /// <summary>Called by MainWindow when the global hotkey fires: grabs the foreground window as the new target.</summary>
    public void AcquireTargetFromForeground() => AcquireTarget(_controller.TryAcquireForegroundAsTarget);

    /// <summary>Called when the window picker chose a window.</summary>
    public void OnWindowPicked(nint hwnd)
    {
        IsPicking = false;
        AcquireTarget(() => _controller.TryAcquireTarget(hwnd));
    }

    public void OnPickerCancelled() => IsPicking = false;

    /// <summary>The picker click hit the desktop, the taskbar or something else that cannot be captured.</summary>
    public void OnPickerNothingPicked()
    {
        IsPicking = false;
        ShowNotice(InfoBarSeverity.Warning, "", R.Get("NoticePickerNothingMessage"));
    }

    /// <summary>Opens the window picker (or closes it when it is already up).</summary>
    [RelayCommand]
    private void PickWindow()
    {
        if (IsPicking)
        {
            _controller.CancelPicker();
            return;
        }

        IsNoticeOpen = false;
        IsPicking = true;
        _controller.StartPicker();
    }

    private void AcquireTarget(Func<bool> acquire)
    {
        // A new session always starts from a clean booth, even if no window could be acquired.
        PreviewImage = null;
        IsPreviewShown = false;
        _lastCapturePngBytes = null;

        // Fit mode is per session and off by default: a small window must be captured as it is.
        // Only the auto-fit of an oversized target (below) turns it on.
        IsFitToBoothEnabled = false;

        if (!acquire())
        {
            if (_controller.LastAcquireFailure == AcquireFailure.Elevated)
            {
                ShowNotice(InfoBarSeverity.Warning, R.Get("NoticeTargetElevatedTitle"), R.Get("NoticeTargetElevatedMessage"), autoClose: false);
                SetNoticeAction(R.Get("NoticeRestartElevatedAction"), () => RestartElevatedRequested?.Invoke());
                return;
            }

            AppLog.Write("Acquire: no suitable window");
            ShowNotice(InfoBarSeverity.Warning, "", R.Get("NoticeNoForegroundMessage"));
            return;
        }

        IsNoticeOpen = false;

        // Respect the window's own size at acquire time (fixed-size dialogs in particular): the booth
        // is laid out 4:3 around it, and only fit mode or the size picker change things afterwards.
        // Exception: a target too big for any booth on this display (e.g. maximized) would cover
        // the toolbar and make the booth unusable, so it is shrunk into the largest preset instead.
        var display = DisplayService.GetSelectedDisplay(_settings);
        var bounds = _controller.CenterTarget(display);
        var max = _controller.GetMaxAreaSize(display);
        var min = _controller.GetMinAreaForTarget();
        var largest = TargetSizePreset.BuiltIn
            .Where(p => p.Width <= max.Width && p.Height <= max.Height)
            .OrderByDescending(p => p.Width * p.Height)
            .FirstOrDefault();
        var autoFitted = false;
        System.Drawing.Size area;
        if (largest is not null && min is { } needed && (needed.Width > largest.Width || needed.Height > largest.Height))
        {
            // "Too big" = bigger than the largest preset offered: the booth takes that size, the
            // target is shrunk into it, and fit mode stays on so the two keep matching.
            area = _controller.LayoutBoothWithAreaSize(largest.Width, largest.Height, display, recordLayoutSize: true);
            var fitted = _controller.FitTargetToLayoutArea();
            autoFitted = fitted.Size != bounds.Size;
            IsFitToBoothEnabled = true;
        }
        else
        {
            area = _controller.LayoutBoothWindowAroundTarget(bounds, display);
        }
        _controller.SetTargetTopMost(true);
        IsTargetPinned = true;

        _initialArea = area;
        if (RefreshPresets())
        {
            // Selecting an item of a freshly rebuilt list in the same pass leaves the ComboBox blank;
            // let it realize the new items first.
            DispatcherQueue.GetForCurrentThread().TryEnqueue(() => SyncSizeControlsToArea(area));
        }
        else
        {
            SyncSizeControlsToArea(area);
        }

        PreviewImage = null;
        IsPreviewShown = false;

        if (autoFitted)
        {
            ShowNotice(InfoBarSeverity.Informational, R.Get("NoticeAutoFitTitle"), R.F("NoticeAutoFitMessage", area.Width, area.Height));
        }
    }

    /// <summary>Re-applies the booth/target/indicator z-order (see BoothController.EnforceZOrder).</summary>
    public void EnforceZOrder() => _controller.EnforceZOrder();

    /// <summary>Called when the booth window is hidden to the tray: the target must not stay always-on-top.</summary>
    public void OnBoothHidden()
    {
        CancelCountdown();
        if (_controller.HasTarget)
        {
            _controller.SetTargetTopMost(false);
        }
        IsTargetPinned = false;
    }

    /// <summary>Gives a never-laid-out booth its remembered size, centered, so it does not appear at WinUI's default size.</summary>
    public void EnsureBoothLaidOut()
    {
        if (_controller.HasLaidOut)
        {
            return;
        }

        var display = DisplayService.GetSelectedDisplay(_settings);
        var area = _controller.LayoutBoothWithAreaSize(_settings.BoothWidth, _settings.BoothHeight, display, recordLayoutSize: true);
        SyncSizeControlsToArea(area);
    }

    /// <summary>Called after the user resized the held target: the overflow marks on the presets depend on its size.</summary>
    public void OnTargetResized() => RefreshPresets();

    /// <summary>Called when the held target was minimized or closed: the controller has already let go of it.</summary>
    public void OnTargetLost(TargetLostReason reason)
    {
        IsTargetPinned = false;
        var key = reason switch
        {
            TargetLostReason.Minimized => "NoticeTargetMinimizedMessage",
            TargetLostReason.Maximized => "NoticeTargetMaximizedMessage",
            _ => "NoticeTargetClosedMessage",
        };
        ShowNotice(InfoBarSeverity.Informational, R.Get("NoticeCancelledTitle"), R.Get(key));
    }

    /// <summary>The global hotkey as text ("Win + Shift + B"), for hints and notices.</summary>
    public string HotkeyText => new HotkeyBinding(_settings.HotkeyModifiers, _settings.HotkeyVirtualKey).ToString();

    /// <summary>What the empty booth tells the user to do (the view composes the runs; this just changes with the hotkey).</summary>
    public string IdleHintText => HotkeyText;

    /// <summary>The settings window changed (and registered) the hotkey.</summary>
    public void OnHotkeyChanged()
    {
        OnPropertyChanged(nameof(HotkeyText));
        OnPropertyChanged(nameof(IdleHintText));
    }

    /// <summary>The settings window picked another display: move the booth (and the held target) there.</summary>
    public void OnDisplayChanged()
    {
        if (_controller.HasLaidOut)
        {
            _controller.RelayoutWithLastArea(DisplayService.GetSelectedDisplay(_settings));
        }
        RefreshPresets();
    }

    /// <summary>The settings page edited the size presets: rebuild the menu and re-sync its face.</summary>
    public void OnPresetsChanged()
    {
        RefreshPresets();
        if (BoothAreaElement is { ActualHeight: > 0 })
        {
            SyncSizeControlsToArea(_controller.GetAreaSizePx(BoothAreaElement));
        }
    }

    /// <summary>The settings window changed the shadow margin.</summary>
    public void OnMarginChanged(int marginDip)
    {
        _controller.MarginDip = marginDip;
        if (IsFitToBoothEnabled)
        {
            FitTargetToBooth();
        }
        RefreshPresets();
    }

    /// <summary>Raised when a notice's action asks for the settings page (update available).</summary>
    public event Action? SettingsRequested;

    /// <summary>Raised when the user asks to relaunch the app as administrator (to hold an elevated window).</summary>
    public event Action? RestartElevatedRequested;

    /// <summary>Label of the notice's action button ("" = no button).</summary>
    [ObservableProperty]
    public partial string NoticeActionLabel { get; set; } = "";

    private Action? _noticeAction;

    /// <summary>Gives the current notice an action button.</summary>
    private void SetNoticeAction(string label, Action action)
    {
        _noticeAction = action;
        NoticeActionLabel = label;
    }

    [RelayCommand]
    private void NoticeAction() => _noticeAction?.Invoke();

    /// <summary>Settings were asked for while a target is held (the page is unavailable then).</summary>
    public void NotifySettingsBlocked() =>
        ShowNotice(InfoBarSeverity.Warning, "", R.Get("NoticeSettingsBlockedMessage"));

    /// <summary>A newer release was found by the background check.</summary>
    public void NotifyUpdateAvailable(string tag)
    {
        ShowNotice(InfoBarSeverity.Informational, "", R.F("UpdateAvailableFmt", tag), autoClose: false);
        SetNoticeAction(R.Get("NoticeOpenSettingsAction"), () => SettingsRequested?.Invoke());
    }

    /// <summary>Called at startup when a window left on top by a crashed previous run was just released.</summary>
    public void NotifyRecoveredStuckTopMost(string title) =>
        ShowNotice(InfoBarSeverity.Warning, R.Get("NoticeRecoveredTitle"), R.F("NoticeRecoveredMessage", title), autoClose: false);

    /// <summary>Called by MainWindow when the global hotkey could not be registered.</summary>
    public void NotifyHotkeyFailed() =>
        ShowNotice(InfoBarSeverity.Error, R.Get("NoticeHotkeyFailedTitle"), R.F("NoticeHotkeyFailedMessage", HotkeyText), autoClose: false);

    /// <summary>Explains the current fit-mode state (shown as the switch's tooltip).</summary>
    public string FitToBoothToolTip => R.Get(IsFitToBoothEnabled ? "FitToBoothTipOn" : "FitToBoothTipOff");

    partial void OnIsFitToBoothEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(FitToBoothToolTip));

        if (value)
        {
            FitTargetToBooth();
        }
        RefreshPresets();
    }

    /// <summary>Resizes the target to fill the booth area as currently shown (minus the shadow margin).</summary>
    private void FitTargetToBooth()
    {
        if (!_controller.HasTarget || !IsTargetPinned || BoothAreaElement is null || BoothAreaElement.ActualHeight <= 0)
        {
            return;
        }

        _controller.FitTargetToArea(BoothAreaElement);
    }

    /// <summary>
    /// Called by MainWindow whenever the booth area is (re)laid out. The first real layout tells us
    /// how much window chrome sits above/below the area, so the target can be centered in it exactly.
    /// </summary>
    public void OnBoothAreaLayoutUpdated()
    {
        if (BoothAreaElement is null)
        {
            return;
        }

        if (_controller.MeasureChrome(BoothAreaElement))
        {
            if (_controller.HasTarget && IsTargetPinned)
            {
                // Same area size as before, just with the real chrome - never the 4:3 auto layout,
                // which would throw away an auto-fitted preset size.
                _controller.RelayoutWithLastArea(DisplayService.GetSelectedDisplay(_settings));
            }
            return;
        }

        // Our own acquire-time layout already synced the controls and must not trigger fit mode.
        if (_controller.IsBoothAtLayoutSize)
        {
            return;
        }

        // A user resize or a size-picker change: mirror the new size, then let the target follow in fit mode.
        SyncSizeControlsToArea(_controller.GetAreaSizePx(BoothAreaElement));
        if (IsFitToBoothEnabled)
        {
            FitTargetToBooth();
        }
    }

    private void ShowNotice(InfoBarSeverity severity, string title, string message, bool autoClose = true)
    {
        NoticeSeverity = severity;
        NoticeTitle = title;
        NoticeMessage = message;
        NoticeActionLabel = "";
        _noticeAction = null;
        IsNoticeOpen = true;
        _noticeTimer.Stop();
        if (autoClose)
        {
            _noticeTimer.Start();
        }
    }

    /// <summary>Lets the target go without capturing: the escape hatch for a hotkey pressed by mistake.</summary>
    [RelayCommand]
    private void Release()
    {
        _controller.SetTargetTopMost(false);
        IsTargetPinned = false;
        ShowNotice(InfoBarSeverity.Informational, R.Get("NoticeCancelledTitle"), R.F("NoticeReleasedMessage", HotkeyText));
    }

    // Concurrent executions allowed on purpose: a second press during the countdown must reach
    // this method to cancel it (the generated command would otherwise disable the button).
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task CaptureAsync()
    {
        try
        {
            await CaptureCoreAsync();
        }
        catch (Exception ex)
        {
            AppLog.Write($"Capture: failed {ex}");
            ShowNotice(InfoBarSeverity.Error, "", ex.Message, autoClose: false);
        }
    }

    private async Task CaptureCoreAsync()
    {
        AppLog.Write($"Capture: requested countingDown={IsCountingDown} hasTarget={_controller.HasTarget} area={(BoothAreaElement is null ? "null" : "ok")}");
        // A second press while counting down cancels it.
        if (IsCountingDown)
        {
            CancelCountdown();
            return;
        }

        if (!_controller.HasTarget || BoothAreaElement is null)
        {
            ShowNotice(InfoBarSeverity.Warning, "", R.F("NoticeNoTargetMessage", HotkeyText));
            return;
        }

        IsNoticeOpen = false;

        // The click that got us here may have re-inserted the booth above the target.
        _controller.EnforceZOrder();

        if (CountdownSeconds > 0 && !await RunCountdownAsync())
        {
            ShowNotice(InfoBarSeverity.Informational, "", R.Get("NoticeCountdownCancelledMessage"));
            return;
        }

        if (!_controller.HasTarget)
        {
            // The target went away during the countdown (closed, minimized...).
            return;
        }

        // Nothing of ours may be in the shot: the notice overlays the booth area and the frame
        // ring sits around the target.
        _controller.HideTargetFrame();

        // Restore the target's active/focused visual state before grabbing pixels: clicking our
        // own shutter button steals focus and would otherwise capture a dimmed/inactive window.
        await _controller.RestoreTargetFocusAsync();

        var result = await _controller.CaptureAsync(BoothAreaElement);
        _controller.SetTargetTopMost(false);
        _lastCapturePngBytes = result.PngBytes;
        _lastCaptureTime = DateTime.Now;
        PreviewImage = result.Preview;
        IsPreviewShown = true;
        IsTargetPinned = false;
        Captured?.Invoke();
        await ClipboardService.CopyPngAsync(result.PngBytes);
    }

    /// <summary>Counts down on the overlay; false when cancelled.</summary>
    private async Task<bool> RunCountdownAsync()
    {
        IsCountingDown = true;
        _countdownCts = new CancellationTokenSource();
        try
        {
            for (var remaining = CountdownSeconds; remaining > 0; remaining--)
            {
                if (!_controller.HasTarget)
                {
                    return false;
                }
                CountdownRemaining = remaining;
                // The target must stay usable (menus, hover effects) and visible during the countdown.
                _controller.EnforceZOrder();
                await Task.Delay(1000, _countdownCts.Token);
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            _countdownCts.Dispose();
            _countdownCts = null;
            IsCountingDown = false;
        }
    }

    public void CancelCountdown() => _countdownCts?.Cancel();

    [RelayCommand]
    private void Retake()
    {
        IsNoticeOpen = false;
        PreviewImage = null;
        IsPreviewShown = false;
        _controller.ReturnToLiveState();
        IsTargetPinned = _controller.HasTarget;
    }

    /// <summary>Puts the last capture on the clipboard again (it is copied automatically at capture time).</summary>
    [RelayCommand]
    private async Task CopyAsync()
    {
        if (_lastCapturePngBytes is null)
        {
            return;
        }

        await ClipboardService.CopyPngAsync(_lastCapturePngBytes);
        ShowNotice(InfoBarSeverity.Success, "", R.Get("NoticeCopiedMessage"));
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_lastCapturePngBytes is null || BoothAreaElement?.XamlRoot is null)
        {
            return;
        }

        // The format last used (settings) is listed first, which is what the dialog preselects.
        var picker = new FileSavePicker
        {
            SuggestedFileName = ImageExport.SuggestedFileName(_lastCaptureTime),
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            SettingsIdentifier = "SaveCapture",
        };
        var preferJpeg = string.Equals(_settings.SaveFormat, ImageExport.Jpeg, StringComparison.OrdinalIgnoreCase);
        var png = (R.Get("SaveTypePng"), new List<string> { ".png" });
        var jpeg = (R.Get("SaveTypeJpeg"), new List<string> { ".jpg", ".jpeg" });
        foreach (var (label, extensions) in preferJpeg ? new[] { jpeg, png } : new[] { png, jpeg })
        {
            picker.FileTypeChoices.Add(label, extensions);
        }
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);

        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        var isJpeg = file.FileType is ".jpg" or ".jpeg";
        var bytes = ImageExport.Encode(_lastCapturePngBytes, isJpeg ? ImageExport.Jpeg : ImageExport.Png, _settings.JpegQuality);
        await FileIO.WriteBytesAsync(file, bytes);

        if (isJpeg != preferJpeg)
        {
            _settings.SaveFormat = isJpeg ? ImageExport.Jpeg : ImageExport.Png;
            SettingsService.Save(_settings);
        }
        ShowNotice(InfoBarSeverity.Success, "", R.F("NoticeSavedMessage", file.Path));
    }

    [RelayCommand]
    private async Task ShareAsync()
    {
        if (_lastCapturePngBytes is null)
        {
            return;
        }

        try
        {
            await ShareService.ShareAsync(App.WindowHandle, _lastCapturePngBytes, ImageExport.SuggestedFileName(_lastCaptureTime));
        }
        catch (Exception ex)
        {
            AppLog.Write($"Share: failed {ex}");
            ShowNotice(InfoBarSeverity.Error, "", R.Get("NoticeShareFailedMessage"));
        }
    }
}
