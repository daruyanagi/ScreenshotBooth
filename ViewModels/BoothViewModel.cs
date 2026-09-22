using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using ScreenshotBooth.Models;
using ScreenshotBooth.Services;

namespace ScreenshotBooth.ViewModels;

/// <summary>
/// UI state and commands for the single-window booth: target-size selection, shutter/retake,
/// and (stubbed) save/share. Actual Win32/DWM/screen-capture work is delegated to
/// <see cref="BoothController"/>, injected by MainWindow, so this class stays UI-focused.
/// </summary>
public partial class BoothViewModel : ObservableObject
{
    private readonly BoothController _controller;
    private readonly AppSettings _settings;

    private byte[]? _lastCapturePngBytes;
    private bool _isApplyingPresetProgrammatically;

    /// <summary>Built-in presets that fit the 4:3 booth on the selected display, plus "Custom". See <see cref="RefreshPresets"/>.</summary>
    public ObservableCollection<TargetSizePreset> Presets { get; } = new();

    /// <summary>The preset matching the booth's current size, or null when it matches none (the ComboBox then shows <see cref="CurrentSizeText"/>).</summary>
    [ObservableProperty]
    public partial TargetSizePreset? SelectedPreset { get; set; }

    /// <summary>The booth area's current size as "WxH", shown as the size picker's placeholder.</summary>
    [ObservableProperty]
    public partial string CurrentSizeText { get; set; } = "";

    // The 4:3 size the booth was given when the target was acquired; offered as the "Custom" preset.
    private System.Drawing.Size? _initialArea;

    /// <summary>Seconds to count down before capturing; 0 captures immediately. TODO: countdown overlay not implemented yet.</summary>
    [ObservableProperty]
    public partial int CountdownSeconds { get; set; }

    /// <summary>Explains the current countdown setting (shown as the button's tooltip).</summary>
    public string CountdownToolTip => CountdownSeconds == 0
        ? R.Get("CountdownTipOff")
        : R.F("CountdownTipSeconds", CountdownSeconds);

    partial void OnCountdownSecondsChanged(int value)
    {
        OnPropertyChanged(nameof(CountdownToolTip));
        _settings.DefaultCountdownSeconds = value;
        SettingsService.Save(_settings);
    }

    [ObservableProperty]
    public partial BitmapImage? PreviewImage { get; set; }

    [ObservableProperty]
    public partial bool IsPreviewShown { get; set; }

    /// <summary>True while the target window is being held always-on-top by the booth.</summary>
    [ObservableProperty]
    public partial bool IsTargetPinned { get; set; }

    /// <summary>Fit mode: while on, resizing the booth resizes the target to fill it.</summary>
    [ObservableProperty]
    public partial bool IsFitToBoothEnabled { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = R.Get("StatusIdle");

    [ObservableProperty]
    public partial bool IsNoticeOpen { get; set; }

    [ObservableProperty]
    public partial string NoticeTitle { get; set; } = "";

    [ObservableProperty]
    public partial string NoticeMessage { get; set; } = "";

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
        _noticeTimer.Interval = TimeSpan.FromSeconds(6);
        _noticeTimer.IsRepeating = false;
        _noticeTimer.Tick += (_, _) => IsNoticeOpen = false;

        // Guarded: the [ObservableProperty] setters below fire OnXxxChanged synchronously, and
        // those partials read SelectedPreset - which would otherwise still be null (its default,
        // despite the non-nullable annotation) until the SelectedPreset assignment itself runs.
        _isApplyingPresetProgrammatically = true;

        RefreshPresets();
        SelectedPreset = Presets.FirstOrDefault(p => p.Width == settings.BoothWidth && p.Height == settings.BoothHeight);
        IsFitToBoothEnabled = settings.FitToBooth;
        CountdownSeconds = settings.DefaultCountdownSeconds;

        _isApplyingPresetProgrammatically = false;
    }

    partial void OnSelectedPresetChanged(TargetSizePreset? value)
    {
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
    /// Rebuilds <see cref="Presets"/> to only the built-in sizes the booth can host on the selected
    /// display (the 4:3 booth is clamped to the work area, so bigger targets would overflow it).
    /// Keeps the current selection when it still fits, otherwise falls back to the largest that does.
    /// </summary>
    private void RefreshPresets()
    {
        var display = DisplayService.GetSelectedDisplay(_settings);
        var max = _controller.GetMaxAreaSize(display);
        var fitting = TargetSizePreset.BuiltIn
            .Where(p => p.Width <= max.Width && p.Height <= max.Height)
            .ToList();
        if (_initialArea is { } initial)
        {
            fitting.Add(TargetSizePreset.Custom(initial.Width, initial.Height));
        }

        if (Presets.SequenceEqual(fitting))
        {
            return;
        }

        var previous = SelectedPreset;
        var wasProgrammatic = _isApplyingPresetProgrammatically;
        _isApplyingPresetProgrammatically = true;
        Presets.Clear();
        foreach (var preset in fitting)
        {
            Presets.Add(preset);
        }

        SelectedPreset = previous is null ? null : Presets.FirstOrDefault(p => p == previous);
        _isApplyingPresetProgrammatically = wasProgrammatic;
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
    public void AcquireTargetFromForeground()
    {
        if (!_controller.TryAcquireForegroundAsTarget())
        {
            StatusMessage = R.Get("StatusNoForeground");
            AppLog.Write("Acquire: no suitable foreground window");
            return;
        }

        // Respect the window's own size at acquire time (fixed-size dialogs in particular): the booth
        // is laid out 4:3 around it, and only fit mode or the size controls change things afterwards.
        var display = DisplayService.GetSelectedDisplay(_settings);
        var bounds = _controller.CenterTarget(display);
        var area = _controller.LayoutBoothWindowAroundTarget(bounds, display);
        _controller.SetTargetTopMost(true);
        IsTargetPinned = true;

        _initialArea = area;
        RefreshPresets();
        SyncSizeControlsToArea(area);

        PreviewImage = null;
        IsPreviewShown = false;
        StatusMessage = R.Get("StatusLive");
    }

    /// <summary>Called when the booth window is hidden to the tray: the target must not stay always-on-top.</summary>
    public void OnBoothHidden()
    {
        if (_controller.HasTarget)
        {
            _controller.SetTargetTopMost(false);
        }
        IsTargetPinned = false;
    }

    /// <summary>Explains the current fit-mode state (shown as the switch's tooltip).</summary>
    public string FitToBoothToolTip => R.Get(IsFitToBoothEnabled ? "FitToBoothTipOn" : "FitToBoothTipOff");

    partial void OnIsFitToBoothEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(FitToBoothToolTip));
        _settings.FitToBooth = value;
        SettingsService.Save(_settings);

        if (value)
        {
            FitTargetToBooth();
        }
    }

    /// <summary>Resizes the target to fill the booth area as currently shown (minus the shadow margin).</summary>
    private void FitTargetToBooth()
    {
        if (!_controller.HasTarget || !IsTargetPinned || BoothAreaElement is null || BoothAreaElement.ActualHeight <= 0)
        {
            return;
        }

        var bounds = _controller.FitTargetToArea(BoothAreaElement);
        StatusMessage = R.F("StatusResized", bounds.Width, bounds.Height);
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
                var display = DisplayService.GetSelectedDisplay(_settings);
                _controller.LayoutBoothWindowAroundTarget(_controller.GetTargetExtendedFrameBounds(), display);
            }
            return;
        }

        // Our own acquire-time layout already synced the controls and must not trigger fit mode.
        if (_controller.IsBoothAtLayoutSize)
        {
            return;
        }

        // A user resize or a size-control change: mirror the new size, then let the target follow in fit mode.
        SyncSizeControlsToArea(_controller.GetAreaSizePx(BoothAreaElement));
        if (IsFitToBoothEnabled)
        {
            FitTargetToBooth();
        }
    }

    private void ShowNotice(string title, string message)
    {
        NoticeTitle = title;
        NoticeMessage = message;
        IsNoticeOpen = true;
        _noticeTimer.Stop();
        _noticeTimer.Start();
    }

    /// <summary>Lets the target go without capturing: the escape hatch for a hotkey pressed by mistake.</summary>
    [RelayCommand]
    private void Release()
    {
        _controller.SetTargetTopMost(false);
        IsTargetPinned = false;
        StatusMessage = R.Get("StatusReleased");
        ShowNotice(R.Get("NoticeCancelledTitle"), R.Get("NoticeReleasedMessage"));
    }

    [RelayCommand]
    private async Task CaptureAsync()
    {
        if (!_controller.HasTarget || BoothAreaElement is null)
        {
            StatusMessage = R.Get("StatusNoTargetYet");
            return;
        }

        IsNoticeOpen = false;
        StatusMessage = R.Get("StatusCapturing");

        // Restore the target's active/focused visual state before grabbing pixels: clicking our
        // own shutter button steals focus and would otherwise capture a dimmed/inactive window.
        await _controller.RestoreTargetFocusAsync();

        var result = await _controller.CaptureAsync(BoothAreaElement);
        _controller.SetTargetTopMost(false);
        IsTargetPinned = false;
        await ClipboardService.CopyPngAsync(result.PngBytes);

        _lastCapturePngBytes = result.PngBytes;
        PreviewImage = result.Preview;
        IsPreviewShown = true;
        StatusMessage = R.Get("StatusCaptured");
    }

    [RelayCommand]
    private void Retake()
    {
        PreviewImage = null;
        IsPreviewShown = false;
        _controller.ReturnToLiveState();
        IsTargetPinned = _controller.HasTarget;
        StatusMessage = _controller.HasTarget
            ? R.Get("StatusLive")
            : R.Get("StatusIdle");
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
        StatusMessage = R.Get("StatusCopied");
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        // Minimal first cut: PNG-only, no format choice UI yet. TODO (follow-up): JPEG option, quality slider.
        if (_lastCapturePngBytes is null || BoothAreaElement?.XamlRoot is null)
        {
            return;
        }

        var picker = new FileSavePicker { SuggestedFileName = "Screenshot" };
        picker.FileTypeChoices.Add("PNG Image", new List<string> { ".png" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);

        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        await FileIO.WriteBytesAsync(file, _lastCapturePngBytes);
        StatusMessage = R.F("StatusSaved", file.Path);
    }

    [RelayCommand]
    private void Share()
    {
        // TODO (follow-up): DataTransferManager share flow. Stub for this pass.
        StatusMessage = R.Get("StatusShareNotImplemented");
    }
}
