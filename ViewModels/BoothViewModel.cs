using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    public ObservableCollection<TargetSizePreset> Presets { get; } = new(TargetSizePreset.BuiltIn);

    // TODO (follow-up): wire this to a real countdown overlay. For this pass it's UI-only.
    public ObservableCollection<string> CountdownOptions { get; } = new(["Off", "3s", "5s", "10s", "Custom"]);

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
    public partial string SelectedCountdown { get; set; } = "Off";

    [ObservableProperty]
    public partial BitmapImage? PreviewImage { get; set; }

    [ObservableProperty]
    public partial bool IsPreviewShown { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = "Press Win+Shift+B over a window to begin.";

    /// <summary>
    /// The XAML element for the white booth backdrop. The view sets this after Loaded so
    /// BoothController can compute its exact on-screen rect at capture time.
    /// </summary>
    public FrameworkElement? BoothAreaElement { get; set; }

    public BoothViewModel(BoothController controller, AppSettings settings)
    {
        _controller = controller;
        _settings = settings;

        // Guarded: the [ObservableProperty] setters below fire OnXxxChanged synchronously, and
        // those partials read SelectedPreset - which would otherwise still be null (its default,
        // despite the non-nullable annotation) until the SelectedPreset assignment itself runs.
        _isApplyingPresetProgrammatically = true;

        var matchingPreset = Presets.FirstOrDefault(p =>
            !p.IsCustom && p.Width == settings.DefaultTargetWidth && p.Height == settings.DefaultTargetHeight);
        SelectedPreset = matchingPreset ?? Presets[^1]; // last entry is always "Custom"
        CustomWidth = settings.DefaultTargetWidth;
        CustomHeight = settings.DefaultTargetHeight;
        IsCustomSizeVisible = SelectedPreset.IsCustom;

        _isApplyingPresetProgrammatically = false;
    }

    partial void OnSelectedPresetChanged(TargetSizePreset value)
    {
        IsCustomSizeVisible = value.IsCustom;

        if (!value.IsCustom)
        {
            _isApplyingPresetProgrammatically = true;
            CustomWidth = value.Width;
            CustomHeight = value.Height;
            _isApplyingPresetProgrammatically = false;
        }

        ApplyTargetSize();
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

        _settings.DefaultTargetWidth = width;
        _settings.DefaultTargetHeight = height;
        SettingsService.Save(_settings);

        if (!_controller.HasTarget)
        {
            return;
        }

        var display = DisplayService.GetSelectedDisplay(_settings);
        var actualBounds = _controller.ResizeAndCenterTarget(width, height, display);
        _controller.LayoutBoothWindowAroundTarget(actualBounds, display);
    }

    /// <summary>Called by MainWindow when the global hotkey fires: grabs the foreground window as the new target.</summary>
    public void AcquireTargetFromForeground()
    {
        if (!_controller.TryAcquireForegroundAsTarget())
        {
            StatusMessage = "No suitable foreground window found.";
            return;
        }

        var display = DisplayService.GetSelectedDisplay(_settings);
        var bounds = _controller.ResizeAndCenterTarget((int)Math.Round(CustomWidth), (int)Math.Round(CustomHeight), display);
        _controller.LayoutBoothWindowAroundTarget(bounds, display);
        _controller.SetTargetTopMost(true);

        PreviewImage = null;
        IsPreviewShown = false;
        StatusMessage = "Live - press the shutter to capture.";
    }

    [RelayCommand]
    private async Task CaptureAsync()
    {
        if (!_controller.HasTarget || BoothAreaElement is null)
        {
            StatusMessage = "No target window - use Win+Shift+B first.";
            return;
        }

        StatusMessage = "Capturing...";

        // Restore the target's active/focused visual state before grabbing pixels: clicking our
        // own shutter button steals focus and would otherwise capture a dimmed/inactive window.
        await _controller.RestoreTargetFocusAsync();

        var result = await _controller.CaptureAsync(BoothAreaElement);
        _controller.SetTargetTopMost(false);
        await ClipboardService.CopyPngAsync(result.PngBytes);

        _lastCapturePngBytes = result.PngBytes;
        PreviewImage = result.Preview;
        IsPreviewShown = true;
        StatusMessage = "Captured - copied to clipboard.";
    }

    [RelayCommand]
    private void Retake()
    {
        PreviewImage = null;
        IsPreviewShown = false;
        _controller.ReturnToLiveState();
        StatusMessage = _controller.HasTarget
            ? "Live - press the shutter to capture."
            : "Press Win+Shift+B over a window to begin.";
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
        StatusMessage = $"Saved to {file.Path}";
    }

    [RelayCommand]
    private void Share()
    {
        // TODO (follow-up): DataTransferManager share flow. Stub for this pass.
        StatusMessage = "Share isn't implemented yet.";
    }
}
