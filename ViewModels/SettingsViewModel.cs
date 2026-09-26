using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Windowing;
using ScreenshotBooth.Models;
using ScreenshotBooth.Services;

namespace ScreenshotBooth.ViewModels;

/// <summary>
/// State for the settings window. Every change is applied immediately (there is no OK button):
/// persisted through <see cref="SettingsService"/> and pushed into the running booth through
/// <see cref="BoothViewModel"/>, which stays the single owner of the live values it already had
/// (countdown, restore-layout).
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly BoothViewModel _booth;
    private readonly AppSettings _settings;
    private readonly Func<HotkeyBinding, bool> _registerHotkey;
    private readonly bool _initialized;

    // The binding that is actually registered; the UI may show a rejected attempt until it is fixed.
    private HotkeyBinding _registeredHotkey;

    public SettingsViewModel(BoothViewModel booth, AppSettings settings, Func<HotkeyBinding, bool> registerHotkey)
    {
        _booth = booth;
        _settings = settings;
        _registerHotkey = registerHotkey;

        _registeredHotkey = new HotkeyBinding(settings.HotkeyModifiers, settings.HotkeyVirtualKey);
        HotkeyWin = _registeredHotkey.Win;
        HotkeyControl = _registeredHotkey.Control;
        HotkeyAlt = _registeredHotkey.Alt;
        HotkeyShift = _registeredHotkey.Shift;
        HotkeyKeyIndex = Math.Max(0, HotkeyKeys.IndexOf(HotkeyBinding.KeyName(_registeredHotkey.VirtualKey)));

        DisplayOptions = BuildDisplayOptions();
        DisplayIndex = settings.SelectedDisplayIndex >= 0 && settings.SelectedDisplayIndex < DisplayOptions.Count - 1
            ? settings.SelectedDisplayIndex + 1
            : 0;

        LoadPresetRows();
        MarginDip = settings.MarginDip;
        CountdownIndex = Array.IndexOf(CountdownChoices, booth.CountdownSeconds) is var i and >= 0 ? i : 0;
        CaptureEffectIndex = settings.CaptureEffect == AppSettings.CaptureEffectNone ? 1 : 0;
        SaveFormatIndex = string.Equals(settings.SaveFormat, ImageExport.Jpeg, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        JpegQuality = settings.JpegQuality;
        LanguageIndex = settings.Language switch { "ja" => 1, "en-US" => 2, _ => 0 };
        IsUpdateCheckEnabled = settings.UpdateCheckEnabled;

        _initialized = true;
    }

    // ── hotkey ─────────────────────────────────────────────────────────────

    public static readonly int[] CountdownChoices = [0, 3, 5, 10];

    public List<string> HotkeyKeys { get; } = HotkeyBinding.KeyCatalog.Select(k => k.Name).ToList();

    [ObservableProperty] public partial bool HotkeyWin { get; set; }
    [ObservableProperty] public partial bool HotkeyControl { get; set; }
    [ObservableProperty] public partial bool HotkeyAlt { get; set; }
    [ObservableProperty] public partial bool HotkeyShift { get; set; }
    [ObservableProperty] public partial int HotkeyKeyIndex { get; set; }

    /// <summary>The registered combination, as shown in the expander header.</summary>
    [ObservableProperty] public partial string HotkeyText { get; set; } = "";

    /// <summary>Why the combination shown could not be applied ("" when it was).</summary>
    [ObservableProperty] public partial string HotkeyError { get; set; } = "";

    public bool IsHotkeyErrorOpen => HotkeyError.Length > 0;

    partial void OnHotkeyErrorChanged(string value) => OnPropertyChanged(nameof(IsHotkeyErrorOpen));

    partial void OnHotkeyWinChanged(bool value) => ApplyHotkey();
    partial void OnHotkeyControlChanged(bool value) => ApplyHotkey();
    partial void OnHotkeyAltChanged(bool value) => ApplyHotkey();
    partial void OnHotkeyShiftChanged(bool value) => ApplyHotkey();
    partial void OnHotkeyKeyIndexChanged(int value) => ApplyHotkey();

    private void ApplyHotkey()
    {
        var binding = new HotkeyBinding(
            (HotkeyWin ? HotkeyBinding.ModWin : 0) | (HotkeyControl ? HotkeyBinding.ModControl : 0)
            | (HotkeyAlt ? HotkeyBinding.ModAlt : 0) | (HotkeyShift ? HotkeyBinding.ModShift : 0),
            HotkeyBinding.KeyCatalog[Math.Clamp(HotkeyKeyIndex, 0, HotkeyBinding.KeyCatalog.Count - 1)].VirtualKey);

        if (!_initialized)
        {
            HotkeyText = binding.ToString();
            return;
        }

        if (!binding.HasStrongModifier)
        {
            HotkeyError = R.Get("SettingsHotkeyNeedsModifier");
            return;
        }

        if (binding == _registeredHotkey)
        {
            HotkeyError = "";
            return;
        }

        if (!_registerHotkey(binding))
        {
            // The old binding is gone too (RegisterHotKey replaced it); put it back so the app keeps working.
            _registerHotkey(_registeredHotkey);
            HotkeyError = R.F("SettingsHotkeyInUse", binding);
            return;
        }

        _registeredHotkey = binding;
        _settings.HotkeyModifiers = binding.RegistrationModifiers;
        _settings.HotkeyVirtualKey = binding.VirtualKey;
        SettingsService.Save(_settings);
        HotkeyText = binding.ToString();
        HotkeyError = "";
        _booth.OnHotkeyChanged();
    }

    // ── display / margin / countdown / restore ─────────────────────────────

    public List<string> DisplayOptions { get; }

    [ObservableProperty] public partial int DisplayIndex { get; set; }

    partial void OnDisplayIndexChanged(int value)
    {
        if (!_initialized) return;
        _settings.SelectedDisplayIndex = value - 1;   // 0 = automatic (primary)
        SettingsService.Save(_settings);
        _booth.OnDisplayChanged();
    }

    private static List<string> BuildDisplayOptions()
    {
        var options = new List<string> { R.Get("SettingsDisplayAuto") };
        var all = DisplayArea.FindAll();
        for (var i = 0; i < all.Count; i++)
        {
            var b = all[i].OuterBounds;
            var label = R.F("SettingsDisplayOption", i + 1, b.Width, b.Height);
            if (all[i].IsPrimary)
            {
                label += " " + R.Get("SettingsDisplayPrimarySuffix");
            }
            options.Add(label);
        }
        return options;
    }

    [ObservableProperty] public partial double MarginDip { get; set; }

    partial void OnMarginDipChanged(double value)
    {
        if (!_initialized || double.IsNaN(value)) return;
        var margin = (int)Math.Clamp(value, 0, 200);
        _settings.MarginDip = margin;
        SettingsService.Save(_settings);
        _booth.OnMarginChanged(margin);
    }

    [ObservableProperty] public partial int CountdownIndex { get; set; }

    partial void OnCountdownIndexChanged(int value)
    {
        if (!_initialized || value < 0 || value >= CountdownChoices.Length) return;
        _booth.CountdownSeconds = CountdownChoices[value];
    }

    /// <summary>Lives in the booth view model (also toggled from the toolbar menu); exposed here for the card.</summary>
    public bool IsRestoreLayoutEnabled
    {
        get => _booth.IsRestoreLayoutEnabled;
        set
        {
            if (_booth.IsRestoreLayoutEnabled != value)
            {
                _booth.IsRestoreLayoutEnabled = value;
                OnPropertyChanged();
            }
        }
    }

    // ── size presets ───────────────────────────────────────────────────────

    public ObservableCollection<PresetRowViewModel> SizePresets { get; } = new();

    /// <summary>"7 sizes" for the expander header.</summary>
    public string PresetsSummary => R.F("SettingsPresetsCountFmt", SizePresets.Count);

    [ObservableProperty] public partial double NewPresetWidth { get; set; } = 1280;
    [ObservableProperty] public partial double NewPresetHeight { get; set; } = 960;

    /// <summary>Why the last add/edit was refused ("" = fine).</summary>
    [ObservableProperty] public partial string PresetError { get; set; } = "";

    public bool IsPresetErrorOpen => PresetError.Length > 0;

    partial void OnPresetErrorChanged(string value) => OnPropertyChanged(nameof(IsPresetErrorOpen));

    private bool _loadingPresets;

    private void LoadPresetRows()
    {
        _loadingPresets = true;
        SizePresets.Clear();
        foreach (var p in TargetSizePreset.FromSettings(_settings))
        {
            SizePresets.Add(new PresetRowViewModel(p.Width, p.Height, OnPresetRowChanged, RemovePreset));
        }
        UpdatePresetRowState();
        _loadingPresets = false;
    }

    private void UpdatePresetRowState()
    {
        foreach (var row in SizePresets)
        {
            row.CanRemove = SizePresets.Count > 1;
        }
        OnPropertyChanged(nameof(PresetsSummary));
    }

    private void OnPresetRowChanged(PresetRowViewModel row)
    {
        if (_loadingPresets) return;
        PresetError = "";
        if (!PresetSize.IsValid(row.WidthPx, row.HeightPx) || double.IsNaN(row.Width) || double.IsNaN(row.Height))
        {
            PresetError = R.Get("SettingsPresetRangeError");
        }
        PersistPresets();
    }

    [RelayCommand]
    private void AddPreset()
    {
        var w = double.IsNaN(NewPresetWidth) ? 0 : (int)NewPresetWidth;
        var h = double.IsNaN(NewPresetHeight) ? 0 : (int)NewPresetHeight;
        if (!PresetSize.IsValid(w, h))
        {
            PresetError = R.Get("SettingsPresetRangeError");
            return;
        }
        if (SizePresets.Any(r => r.WidthPx == w && r.HeightPx == h))
        {
            PresetError = R.Get("SettingsPresetDuplicateError");
            return;
        }

        PresetError = "";
        // Keep the list ordered by size, like the built-in one.
        var index = SizePresets.TakeWhile(r => (long)r.WidthPx * r.HeightPx <= (long)w * h).Count();
        SizePresets.Insert(index, new PresetRowViewModel(w, h, OnPresetRowChanged, RemovePreset));
        UpdatePresetRowState();
        PersistPresets();
    }

    private void RemovePreset(PresetRowViewModel row)
    {
        if (SizePresets.Count <= 1) return;
        SizePresets.Remove(row);
        PresetError = "";
        UpdatePresetRowState();
        PersistPresets();
    }

    [RelayCommand]
    private void ResetPresets()
    {
        _settings.SizePresets = null;
        SettingsService.Save(_settings);
        PresetError = "";
        LoadPresetRows();
        _booth.OnPresetsChanged();
    }

    private void PersistPresets()
    {
        _settings.SizePresets = SizePresets
            .Select(r => new PresetSize(r.WidthPx, r.HeightPx))
            .GroupBy(p => (p.Width, p.Height)).Select(g => g.First())
            .ToList();
        SettingsService.Save(_settings);
        _booth.OnPresetsChanged();
    }

    /// <summary>0 = print effect, 1 = none. A string in settings so more effects can be added later.</summary>
    [ObservableProperty] public partial int CaptureEffectIndex { get; set; }

    partial void OnCaptureEffectIndexChanged(int value)
    {
        if (!_initialized) return;
        _settings.CaptureEffect = value == 1 ? AppSettings.CaptureEffectNone : AppSettings.CaptureEffectPrint;
        SettingsService.Save(_settings);
    }

    // ── saving ─────────────────────────────────────────────────────────────

    [ObservableProperty] public partial int SaveFormatIndex { get; set; }

    public bool IsJpegSelected => SaveFormatIndex == 1;

    partial void OnSaveFormatIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsJpegSelected));
        if (!_initialized) return;
        _settings.SaveFormat = value == 1 ? ImageExport.Jpeg : ImageExport.Png;
        SettingsService.Save(_settings);
    }

    [ObservableProperty] public partial double JpegQuality { get; set; }

    partial void OnJpegQualityChanged(double value)
    {
        if (!_initialized || double.IsNaN(value)) return;
        _settings.JpegQuality = (int)Math.Clamp(value, 1, 100);
        SettingsService.Save(_settings);
    }

    // ── language ───────────────────────────────────────────────────────────

    [ObservableProperty] public partial int LanguageIndex { get; set; }

    [ObservableProperty] public partial bool ShowRestartHint { get; set; }

    partial void OnLanguageIndexChanged(int value)
    {
        if (!_initialized) return;
        var language = value switch { 1 => "ja", 2 => "en-US", _ => "" };
        _settings.Language = language;
        SettingsService.Save(_settings);
        Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = language;
        // Already-rendered UI keeps its strings; ask for a restart.
        ShowRestartHint = true;
    }

    // ── updates / about ────────────────────────────────────────────────────

    public bool IsUpdateSectionVisible => !PackageContext.IsPackaged;

    [ObservableProperty] public partial bool IsUpdateCheckEnabled { get; set; }

    partial void OnIsUpdateCheckEnabledChanged(bool value)
    {
        if (!_initialized) return;
        _settings.UpdateCheckEnabled = value;
        SettingsService.Save(_settings);
    }

    public string VersionText => R.F("SettingsVersionFormat", UpdateService.CurrentVersion.ToString(3), R.Get(PackageContext.CurrentChannel switch
    {
        InstallChannel.Packaged => "SettingsChannelPackaged",
        InstallChannel.Winget => "SettingsChannelWinget",
        _ => "SettingsChannelZip",
    }));

    public string RepositoryUrl => $"https://github.com/{UpdateService.RepoSlug}";
}
