using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScreenshotBooth.Models;

namespace ScreenshotBooth.ViewModels;

/// <summary>One editable row of the size-preset list on the settings page.</summary>
public partial class PresetRowViewModel : ObservableObject
{
    private readonly Action<PresetRowViewModel> _changed;
    private readonly Action<PresetRowViewModel> _remove;

    public PresetRowViewModel(int width, int height, Action<PresetRowViewModel> changed, Action<PresetRowViewModel> remove)
    {
        _changed = changed;
        _remove = remove;
        Width = width;
        Height = height;
    }

    [ObservableProperty] public partial double Width { get; set; }
    [ObservableProperty] public partial double Height { get; set; }

    /// <summary>False while this is the only preset left: the list must not become empty.</summary>
    [ObservableProperty] public partial bool CanRemove { get; set; } = true;

    public int WidthPx => (int)Math.Clamp(double.IsNaN(Width) ? PresetSize.Min : Width, PresetSize.Min, PresetSize.Max);
    public int HeightPx => (int)Math.Clamp(double.IsNaN(Height) ? PresetSize.Min : Height, PresetSize.Min, PresetSize.Max);

    /// <summary>"4:3"-style ratio for the row, from the same snapping the size picker uses.</summary>
    public string AspectText => new TargetSizePreset("", WidthPx, HeightPx).AspectRatio;

    partial void OnWidthChanged(double value) => OnEdited();
    partial void OnHeightChanged(double value) => OnEdited();

    private void OnEdited()
    {
        OnPropertyChanged(nameof(AspectText));
        _changed(this);
    }

    [RelayCommand]
    private void Remove() => _remove(this);
}
