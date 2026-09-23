using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using ScreenshotBooth.Services;
using ScreenshotBooth.ViewModels;

namespace ScreenshotBooth;

/// <summary>
/// The settings page, shown by MainWindow as an overlay over the booth (never while a target is
/// held). Plain preference cards bind to <see cref="SettingsViewModel"/>; the update section
/// (check / download / restart) is driven from here because it swaps the InfoBar's action button.
/// </summary>
public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; }

    /// <summary>The back button was pressed.</summary>
    public event Action? BackRequested;

    private readonly Action _exitForUpdate;
    private readonly bool _useWinget;
    private readonly bool _canSelfUpdate;

    private readonly Button _updateButton = new();
    private readonly Button _cancelButton = new();
    private readonly HyperlinkButton _releaseLink = new();
    private CancellationTokenSource? _cts;

    public SettingsPage(SettingsViewModel viewModel, Action exitForUpdate)
    {
        ViewModel = viewModel;
        _exitForUpdate = exitForUpdate;

        InitializeComponent();
        Root.DataContext = ViewModel;

        if (!ViewModel.IsUpdateSectionVisible)
        {
            return;
        }

        _useWinget = PackageContext.CurrentChannel == InstallChannel.Winget && UpdateService.FindWinget() is not null;
        var eligibility = ZipUpdateRunner.CheckEligibility(PackageContext.CurrentChannel, PackageContext.InstallDirectory);
        _canSelfUpdate = !_useWinget && eligibility == ZipUpdateRunner.Eligibility.Ok;

        _updateButton.Content = R.Get(_useWinget ? "UpdateViaWinget" : _canSelfUpdate ? "UpdateRestartAndUpdate" : "UpdateOpenReleasePage");
        _updateButton.Click += OnUpdateActionClick;
        _cancelButton.Content = R.Get("UpdateCancelButton");
        _cancelButton.Click += (_, _) => _cts?.Cancel();
        _releaseLink.Content = R.Get("UpdateOpenReleasePage");
        _releaseLink.Click += (_, _) => OpenReleasePage();

        ShowInitialState();
        RefreshLastChecked();

        Loaded += (_, _) =>
        {
            UpdateService.AvailabilityChanged += OnAvailabilityChanged;
            OnAvailabilityChanged();
        };
        Unloaded += (_, _) => UpdateService.AvailabilityChanged -= OnAvailabilityChanged;
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => BackRequested?.Invoke();

    private void OnRepositoryClick(object sender, RoutedEventArgs e) =>
        _ = Windows.System.Launcher.LaunchUriAsync(new Uri(ViewModel.RepositoryUrl));

    // ── update section ─────────────────────────────────────────────────────

    private void OnAvailabilityChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        if (_cts is not null)
        {
            return;   // don't clobber the download UI with a background check result
        }
        ShowInitialState();
        RefreshLastChecked();
    });

    private void ShowInitialState()
    {
        if (UpdateService.AvailableTag is { } tag)
        {
            SetState(InfoBarSeverity.Warning, R.F("UpdateAvailableFmt", tag), _updateButton);
        }
        else if (SettingsService.Load().LastUpdateCheck is not null)
        {
            SetState(InfoBarSeverity.Success, R.Get("UpdateLatest"), _releaseLink);
        }
        else
        {
            SetState(InfoBarSeverity.Informational, R.Get("UpdateNotChecked"), _releaseLink);
        }
    }

    private void RefreshLastChecked() =>
        UpdateCard.Description = SettingsService.Load().LastUpdateCheck is { } checkedAt
            ? R.F("UpdateLastCheckedFmt", checkedAt.LocalDateTime.ToString("g"))
            : null!;   // null = no description row

    private void SetState(InfoBarSeverity severity, string message, ButtonBase? action, double? progress = null)
    {
        UpdateBar.Severity = severity;
        UpdateBar.Message = message;
        UpdateBar.ActionButton = action;
        DownloadProgress.Visibility = progress is null ? Visibility.Collapsed : Visibility.Visible;
        if (progress is { } value)
        {
            DownloadProgress.Value = value;
        }
    }

    private void OpenReleasePage() =>
        _ = Windows.System.Launcher.LaunchUriAsync(new Uri(UpdateService.ReleasesPageUrl));

    private async void OnCheckClick(object sender, RoutedEventArgs e)
    {
        CheckButton.IsEnabled = false;
        SetState(InfoBarSeverity.Informational, R.Get("UpdateChecking"), null);
        try
        {
            if (await UpdateService.CheckOnceAsync() is null)
            {
                SetState(InfoBarSeverity.Error, R.Get("UpdateErrorText"), _releaseLink);
                return;
            }
            ShowInitialState();
        }
        finally
        {
            RefreshLastChecked();
            CheckButton.IsEnabled = true;
        }
    }

    private async void OnUpdateActionClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_useWinget)
            {
                if (await ConfirmAsync(R.Get("UpdateWingetConfirmTitle"), R.Get("UpdateWingetConfirmBody")))
                {
                    UpdateService.LaunchDetachedWingetUpgrade();
                    _exitForUpdate();
                }
            }
            else if (_canSelfUpdate)
            {
                await RunSelfUpdateAsync();
            }
            else
            {
                OpenReleasePage();
            }
        }
        catch (Exception ex)
        {
            UpdateService.Trace($"SettingsPage: update action failed {ex}");
            SetState(InfoBarSeverity.Error, R.Get("UpdateErrorText"), _releaseLink);
        }
    }

    private async Task RunSelfUpdateAsync()
    {
        if (!await ConfirmAsync(R.Get("UpdateRestartConfirmTitle"), R.Get("UpdateRestartConfirmBody")))
        {
            return;
        }

        CheckButton.IsEnabled = false;
        _cts = new CancellationTokenSource();
        SetState(InfoBarSeverity.Informational, R.Get("UpdateDownloading"), _cancelButton, 0);

        try
        {
            var result = await ZipUpdateRunner.RunAsync(
                UpdateService.DownloadHttp,
                PackageContext.InstallDirectory,
                new Progress<double>(v => SetState(InfoBarSeverity.Informational, R.Get("UpdateDownloading"), _cancelButton, v)),
                _cts.Token);

            switch (result)
            {
                case ZipUpdateRunner.RunResult.ReadyToRestart:
                    // From here on the finisher (the new version) does the work; this process must
                    // exit or it keeps the files it is about to be replaced with locked.
                    _exitForUpdate();
                    break;
                case ZipUpdateRunner.RunResult.NotSupported:
                    SetState(InfoBarSeverity.Warning, R.F("UpdateSelfUpdateUnavailableFmt", UpdateService.AvailableTag ?? "?"), _releaseLink);
                    break;
                case ZipUpdateRunner.RunResult.Canceled:
                    ShowInitialState();
                    break;
                default:
                    SetState(InfoBarSeverity.Error, R.Get("UpdateErrorText"), _releaseLink);
                    break;
            }
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            CheckButton.IsEnabled = true;
        }
    }

    private async Task<bool> ConfirmAsync(string title, string body)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = R.Get("UpdateConfirmButton"),
            CloseButtonText = R.Get("UpdateCancelButton"),
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
