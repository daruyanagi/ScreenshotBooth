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

    // TODO (follow-up): wire this to a real countdown overlay. For this pass it's UI-only.
    public ObservableCollection<string> CountdownOptions { get; } = new(
    [
        R.Get("CountdownOff"),
        R.F("CountdownSeconds", 3),
        R.F("CountdownSeconds", 5),
        R.F("CountdownSeconds", 10),
        R.Get("CountdownCustom"),
    ]);

    [ObservableProperty]
    public partial TargetSizePreset SelectedPreset { get; set; }

    // double to match NumberBox.Value's type for a direct TwoWay x:Bind (no converter needed).
    [ObservableProperty]
    public partial double CustomWidth { get; set; }

    [ObservableProperty]
    public partial double CustomHeight { get; set; }

    [ObservableProperty]
    public partial bool IsCustomSizeVisible { get; set; }

    [ObservableProperty]
    public partial string SelectedCountdown { get; set; } = R.Get("CountdownOff");

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
        var matchingPreset = Presets.FirstOrDefault(p =>
            !p.IsCustom && p.Width == settings.DefaultTargetWidth && p.Height == settings.DefaultTargetHeight);
        SelectedPreset = matchingPreset ?? Presets[^1]; // last entry is always "Custom"
        CustomWidth = settings.DefaultTargetWidth;
        CustomHeight = settings.DefaultTargetHeight;
        IsCustomSizeVisible = SelectedPreset.IsCustom;
        IsFitToBoothEnabled = settings.FitToBooth;

        _isApplyingPresetProgrammatically = false;
    }

    partial void OnSelectedPresetChanged(TargetSizePreset value)
    {
        // The ComboBox pushes null through the two-way binding while Presets is being rebuilt.
        if (value is null)
        {
            return;
        }

        // Programmatic selection (startup, preset refresh, syncing to the target's real size) must
        // only update the controls - never resize the target.
        var userInitiated = !_isApplyingPresetProgrammatically;
        IsCustomSizeVisible = value.IsCustom;

        if (!value.IsCustom)
        {
            _isApplyingPresetProgrammatically = true;
            CustomWidth = value.Width;
            CustomHeight = value.Height;
            _isApplyingPresetProgrammatically = !userInitiated;
        }

        if (userInitiated)
        {
            ApplyTargetSize();
        }
    }

    partial void OnCustomWidthChanged(double value)
    {
        if (!_isApplyingPresetProgrammatically && SelectedPreset.IsCustom)
        {
            ApplyTargetSize();
        }
    }

    partial void OnCustomHeightChanged(double value)
    {
        if (!_isApplyingPresetProgrammatically && SelectedPreset.IsCustom)
        {
            ApplyTargetSize();
        }
    }

    /// <summary>Resizes/recenters the live target window (graceful no-op if there is none yet) and persists the chosen size.</summary>
    private void ApplyTargetSize()
    {
        if (double.IsNaN(CustomWidth) || double.IsNaN(CustomHeight) || CustomWidth <= 0 || CustomHeight <= 0)
        {
            return;
        }

        var width = (int)Math.Round(CustomWidth);
        var height = (int)Math.Round(CustomHeight);

        var display = DisplayService.GetSelectedDisplay(_settings);
        var max = _controller.GetMaxTargetSize(display);
        if (width > max.Width || height > max.Height)
        {
            width = Math.Min(width, max.Width);
            height = Math.Min(height, max.Height);
            _isApplyingPresetProgrammatically = true;
            CustomWidth = width;
            CustomHeight = height;
            _isApplyingPresetProgrammatically = false;
            StatusMessage = R.F("StatusClamped", width, height);
        }

        _settings.DefaultTargetWidth = width;
        _settings.DefaultTargetHeight = height;
        SettingsService.Save(_settings);

        if (!_controller.HasTarget)
        {
            return;
        }

        var actualBounds = _controller.ResizeAndCenterTarget(width, height, display);
        _controller.LayoutBoothWindowAroundTarget(actualBounds, display);
    }

    /// <summary>
    /// Rebuilds <see cref="Presets"/> to only the built-in sizes the booth can host on the selected
    /// display (the 4:3 booth is clamped to the work area, so bigger targets would overflow it).
    /// Keeps the current selection when it still fits, otherwise falls back to the largest that does.
    /// </summary>
    private void RefreshPresets()
    {
        var display = DisplayService.GetSelectedDisplay(_settings);
        var max = _controller.GetMaxTargetSize(display);
        var fitting = TargetSizePreset.BuiltIn
            .Where(p => p.IsCustom || (p.Width <= max.Width && p.Height <= max.Height))
            .ToList();

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

        if (previous is not null)
        {
            SelectedPreset = Presets.FirstOrDefault(p => p == previous)
                ?? Presets.LastOrDefault(p => !p.IsCustom)
                ?? Presets[^1];
        }
        _isApplyingPresetProgrammatically = wasProgrammatic;
    }

    /// <summary>Reflects the target's actual size in the size controls without touching the target.</summary>
    private void SyncSizeControlsToTarget(System.Drawing.Rectangle bounds)
    {
        var wasProgrammatic = _isApplyingPresetProgrammatically;
        _isApplyingPresetProgrammatically = true;
        SelectedPreset = Presets.FirstOrDefault(p => !p.IsCustom && p.Width == bounds.Width && p.Height == bounds.Height)
            ?? Presets[^1];
        CustomWidth = bounds.Width;
        CustomHeight = bounds.Height;
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

        RefreshPresets();

        // Respect the window's own size at acquire time (fixed-size dialogs in particular); the
        // size controls and the fit-to-booth button can enlarge it afterwards.
        var display = DisplayService.GetSelectedDisplay(_settings);
        var bounds = _controller.CenterTarget(display);
        _controller.LayoutBoothWindowAroundTarget(bounds, display);
        _controller.SetTargetTopMost(true);
        IsTargetPinned = true;
        SyncSizeControlsToTarget(bounds);

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

    partial void OnIsFitToBoothEnabledChanged(bool value)
    {
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
        SyncSizeControlsToTarget(bounds);
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

        // Only a user resize (not our own layout) makes the target follow the booth.
        if (IsFitToBoothEnabled && !_controller.IsBoothAtLayoutSize)
        {
            FitTargetToBooth();
        }
    }

    /// <summary>Called by MainWindow when the booth's position changes; a user drag counts as cancelling the session.</summary>
    public void OnBoothPositionChanged(Windows.Graphics.PointInt32 position)
    {
        if (!IsTargetPinned || position == _controller.LastLayoutPosition)
        {
            return;
        }

        _controller.SetTargetTopMost(false);
        IsTargetPinned = false;
        StatusMessage = R.Get("StatusMovedReleased");
        ShowNotice(R.Get("NoticeCancelledTitle"), R.Get("NoticeMovedMessage"));
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
