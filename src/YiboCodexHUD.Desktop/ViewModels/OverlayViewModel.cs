using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.IO;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using YiboCodexHUD.Core.Abstractions;
using YiboCodexHUD.Core.Models;
using YiboCodexHUD.Infrastructure.Services;

namespace YiboCodexHUD.Desktop.ViewModels;

public partial class OverlayViewModel : ObservableObject
{
    private const int PositionNudgeStepPx = 2;
    private const int MaxPositionOffsetPx = 240;
    private const string DefaultForegroundColorHex = "#FF6A6A6A";
    private const string DefaultHighRemainingColorHex = "#FF6A6A6A";
    private const string DefaultMediumRemainingColorHex = "#FFC28A22";
    private const string DefaultLowRemainingColorHex = "#FFC24A3A";
    private const double DefaultSettingsWindowWidth = 620d;
    private const double DefaultSettingsWindowHeight = 820d;

    private static readonly JsonSerializerOptions ImportExportSerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IRateLimitService _rateLimitService;
    private readonly IHudSettingsStore _hudSettingsStore;
    private readonly ICodexWindowTracker _windowTracker;
    private readonly CodexDesktopLauncher _codexDesktopLauncher;
    private readonly ILogger<OverlayViewModel> _logger;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SemaphoreSlim _settingsGate = new(1, 1);
    private HudSettings _settings = new();
    private UsageSnapshot? _latestSnapshot;
    private bool _hasSnapshot;
    private bool _refreshLoopStarted;

    [ObservableProperty]
    private string _displayText = "正在读取用量...";

    [ObservableProperty]
    private string _compactDisplayText = "正在读取用量...";

    [ObservableProperty]
    private string _minimalDisplayText = "正在读取用量...";

    [ObservableProperty]
    private bool _isHudHovered;

    [ObservableProperty]
    private bool _isInteractionDotActive;

    [ObservableProperty]
    private bool _isCodexWindowAvailable = true;

    [ObservableProperty]
    private Brush _displayForegroundBrush = SystemColors.GrayTextBrush;

    [ObservableProperty]
    private string _baseForegroundColorInput = DefaultForegroundColorHex;

    [ObservableProperty]
    private string _dynamicHighRemainingColorInput = DefaultHighRemainingColorHex;

    [ObservableProperty]
    private string _dynamicMediumRemainingColorInput = DefaultMediumRemainingColorHex;

    [ObservableProperty]
    private string _dynamicLowRemainingColorInput = DefaultLowRemainingColorHex;

    [ObservableProperty]
    private string _dynamicMediumThresholdPercentInput = "50";

    [ObservableProperty]
    private string _dynamicLowThresholdPercentInput = "20";

    [ObservableProperty]
    private int _textOpacityEditorPercent = 100;

    private bool _isSyncingEditorInputs;

    public bool ShowShortWindow => _settings.ShowShortWindow;
    public bool ShowLongWindow => _settings.ShowLongWindow;
    public bool ShowResetCredits => _settings.ShowResetCredits;
    public bool CanToggleShortWindow => !ShowShortWindow || GetEnabledDisplayItemCount() > 1;
    public bool CanToggleLongWindow => !ShowLongWindow || GetEnabledDisplayItemCount() > 1;
    public bool CanToggleResetCredits => !ShowResetCredits || GetEnabledDisplayItemCount() > 1;
    public bool IsOnlyOneDisplayItemEnabled => GetEnabledDisplayItemCount() == 1;
    public string DisplayItemsHintText => IsOnlyOneDisplayItemEnabled
        ? "至少保留一个显示项，当前最后一项不可关闭。"
        : "控制 HUD 文本里展示哪些统计项。";
    public bool AutoLaunchCodexOnStartup => _settings.AutoLaunchCodexOnStartup;
    public bool AutoRefreshEnabled => _settings.AutoRefreshEnabled;
    public bool IsDisplayModeAuto => _settings.DisplayMode == HudDisplayMode.Auto;
    public bool IsDisplayModeFull => _settings.DisplayMode == HudDisplayMode.Full;
    public bool IsDisplayModeCompact => _settings.DisplayMode == HudDisplayMode.Compact;
    public bool IsRefreshEvery20Seconds => _settings.RefreshIntervalSeconds == 20;
    public bool IsRefreshEvery60Seconds => _settings.RefreshIntervalSeconds == 60;
    public bool IsRefreshEvery300Seconds => _settings.RefreshIntervalSeconds == 300;
    public bool IsFontSize14 => _settings.FontSize == 14;
    public bool IsFontSize16 => _settings.FontSize == 16;
    public bool IsFontSize18 => _settings.FontSize == 18;
    public bool IsFontSize20 => _settings.FontSize == 20;
    public bool IsFontSize22 => _settings.FontSize == 22;
    public bool IsFontSize24 => _settings.FontSize == 24;
    public bool HideWhenCodexUnavailable => _settings.HideWhenCodexUnavailable;
    public double DisplayFontSize => _settings.FontSize;
    public int PositionOffsetX => _settings.PositionOffsetX;
    public int PositionOffsetY => _settings.PositionOffsetY;
    public string PositionOffsetSummary => $"X {FormatSignedOffset(PositionOffsetX)} px  ·  Y {FormatSignedOffset(PositionOffsetY)} px";
    public bool IsColorModeDefault => _settings.ColorMode == HudColorMode.Default;
    public bool IsColorModeCustom => _settings.ColorMode == HudColorMode.Custom;
    public bool IsColorModeDynamic => _settings.ColorMode == HudColorMode.DynamicByRemainingPercent;
    public string ColorModeHintText => _settings.ColorMode switch
    {
        HudColorMode.Custom => "使用统一自定义颜色显示 HUD 文本。",
        HudColorMode.DynamicByRemainingPercent => "按剩余百分比动态切换颜色，越低越醒目。",
        _ => "使用默认灰色显示 HUD 文本。"
    };
    public Brush BaseForegroundPreviewBrush => CreateBrushFromHex(_settings.BaseForegroundColorHex, DefaultForegroundColorHex);
    public Brush DynamicHighRemainingPreviewBrush => CreateBrushFromHex(_settings.DynamicHighRemainingColorHex, DefaultHighRemainingColorHex);
    public Brush DynamicMediumRemainingPreviewBrush => CreateBrushFromHex(_settings.DynamicMediumRemainingColorHex, DefaultMediumRemainingColorHex);
    public Brush DynamicLowRemainingPreviewBrush => CreateBrushFromHex(_settings.DynamicLowRemainingColorHex, DefaultLowRemainingColorHex);
    public int DynamicMediumRemainingThresholdPercent => _settings.DynamicMediumRemainingThresholdPercent;
    public int DynamicLowRemainingThresholdPercent => _settings.DynamicLowRemainingThresholdPercent;
    public string ThresholdHintText => $"高余量 >= {DynamicMediumRemainingThresholdPercent}%  ·  中余量 {DynamicLowRemainingThresholdPercent}% - {DynamicMediumRemainingThresholdPercent - 1}%  ·  低余量 < {DynamicLowRemainingThresholdPercent}%";
    public int TextOpacityPercent => _settings.TextOpacityPercent;
    public double DisplayOpacity => _settings.TextOpacityPercent / 100d;
    public string TextOpacitySummary => $"{_settings.TextOpacityPercent}%";
    public bool ShowShortWindowLabel => _settings.ShowShortWindowLabel;
    public bool ShowShortRemainingPercent => _settings.ShowShortRemainingPercent;
    public bool ShowShortResetTime => _settings.ShowShortResetTime;
    public bool ShowLongWindowLabel => _settings.ShowLongWindowLabel;
    public bool ShowLongRemainingPercent => _settings.ShowLongRemainingPercent;
    public bool ShowLongResetTime => _settings.ShowLongResetTime;
    public bool ShowResetCreditsLabel => _settings.ShowResetCreditsLabel;
    public bool ShowSeparatorDots => _settings.ShowSeparatorDots;
    public int ShortWindowOrder => _settings.ShortWindowOrder;
    public int LongWindowOrder => _settings.LongWindowOrder;
    public int ResetCreditsOrder => _settings.ResetCreditsOrder;
    public string ShortWindowOrderSummary => BuildDisplayOrderSummary(HudDisplayItem.ShortWindow, "短周期");
    public string LongWindowOrderSummary => BuildDisplayOrderSummary(HudDisplayItem.LongWindow, "长周期");
    public string ResetCreditsOrderSummary => BuildDisplayOrderSummary(HudDisplayItem.ResetCredits, "重置次数");
    public bool CanMoveShortWindowUp => CanMoveDisplayItem(HudDisplayItem.ShortWindow, moveUp: true);
    public bool CanMoveShortWindowDown => CanMoveDisplayItem(HudDisplayItem.ShortWindow, moveUp: false);
    public bool CanMoveLongWindowUp => CanMoveDisplayItem(HudDisplayItem.LongWindow, moveUp: true);
    public bool CanMoveLongWindowDown => CanMoveDisplayItem(HudDisplayItem.LongWindow, moveUp: false);
    public bool CanMoveResetCreditsUp => CanMoveDisplayItem(HudDisplayItem.ResetCredits, moveUp: true);
    public bool CanMoveResetCreditsDown => CanMoveDisplayItem(HudDisplayItem.ResetCredits, moveUp: false);
    public IReadOnlyList<DisplayOrderRow> DisplayOrderRows => BuildDisplayOrderRows();
    public string AutoRefreshHintText => _settings.AutoRefreshEnabled
        ? "控制后台拉取频率和手动刷新行为。"
        : "自动刷新已关闭，频率选项暂不可用。";
    public string ShortWindowMenuText => BuildWindowMenuText(_latestSnapshot?.ShortWindowMinutes, "短周期");
    public string LongWindowMenuText => BuildWindowMenuText(_latestSnapshot?.LongWindowMinutes, "长周期");

    public OverlayViewModel(
        IRateLimitService rateLimitService,
        IHudSettingsStore hudSettingsStore,
        ICodexWindowTracker windowTracker,
        CodexDesktopLauncher codexDesktopLauncher,
        ILogger<OverlayViewModel> logger)
    {
        _rateLimitService = rateLimitService;
        _hudSettingsStore = hudSettingsStore;
        _windowTracker = windowTracker;
        _codexDesktopLauncher = codexDesktopLauncher;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _settings = NormalizeSettings(await _hudSettingsStore.LoadAsync(cancellationToken));
        ApplySettings();
        await TryLaunchCodexOnStartupAsync(cancellationToken);
        await RefreshAsync(cancellationToken);

        if (_refreshLoopStarted)
        {
            return;
        }

        _refreshLoopStarted = true;
        _ = Task.Run(RunRefreshLoopAsync);
    }

    public void ApplySettingsWindowBounds(Window window)
    {
        window.Width = _settings.SettingsWindowWidth > 0d ? _settings.SettingsWindowWidth : DefaultSettingsWindowWidth;
        window.Height = _settings.SettingsWindowHeight > 0d ? _settings.SettingsWindowHeight : DefaultSettingsWindowHeight;

        if (_settings.SettingsWindowLeft.HasValue)
        {
            window.Left = _settings.SettingsWindowLeft.Value;
        }

        if (_settings.SettingsWindowTop.HasValue)
        {
            window.Top = _settings.SettingsWindowTop.Value;
        }
    }

    public Task PersistSettingsWindowBoundsAsync(Window window, CancellationToken cancellationToken = default)
    {
        return UpdateSettingsAsync(
            _settings with
            {
                SettingsWindowLeft = window.Left,
                SettingsWindowTop = window.Top,
                SettingsWindowWidth = window.Width,
                SettingsWindowHeight = window.Height
            },
            cancellationToken);
    }

    private async Task RunRefreshLoopAsync()
    {
        while (true)
        {
            try
            {
                var delay = _settings.AutoRefreshEnabled
                    ? TimeSpan.FromSeconds(Math.Clamp(_settings.RefreshIntervalSeconds, 5, 3600))
                    : TimeSpan.FromMilliseconds(500);

                await Task.Delay(delay);

                if (!_settings.AutoRefreshEnabled)
                {
                    continue;
                }

                await RefreshAsync();
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "Background refresh loop iteration failed.");
            }
        }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            var trackedWindow = _windowTracker.GetTrackedWindow();
            if (trackedWindow is null)
            {
                IsCodexWindowAvailable = false;
                if (!_settings.HideWhenCodexUnavailable)
                {
                    DisplayText = "等待 Codex 窗口...";
                    CompactDisplayText = DisplayText;
                    MinimalDisplayText = DisplayText;
                }

                UpdateDisplayForegroundBrush();
                return;
            }

            IsCodexWindowAvailable = true;
            var snapshot = await _rateLimitService.GetLatestSnapshotAsync(cancellationToken);
            _latestSnapshot = snapshot;
            ApplySnapshot(snapshot);
            _hasSnapshot = true;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to refresh overlay view model.");
            if (!_hasSnapshot)
            {
                DisplayText = FormatErrorMessage(exception);
                CompactDisplayText = DisplayText;
                MinimalDisplayText = DisplayText;
            }

            UpdateDisplayForegroundBrush();
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private void ApplySnapshot(UsageSnapshot snapshot)
    {
        var fullSegments = new List<string>();
        var compactSegments = new List<string>();

        var shortWindowSegments = BuildUsageWindowSegments(
            snapshot.ShortWindowMinutes,
            snapshot.ShortWindowUsedPercent,
            FormatResetTime(snapshot.ShortWindowResetsAt),
            FormatResetTime(snapshot.ShortWindowResetsAt),
            _settings.ShowShortWindowLabel,
            _settings.ShowShortRemainingPercent,
            _settings.ShowShortResetTime);

        var longWindowSegments = BuildUsageWindowSegments(
            snapshot.LongWindowMinutes,
            snapshot.LongWindowUsedPercent,
            FormatResetDate(snapshot.LongWindowResetsAt),
            FormatCompactResetDate(snapshot.LongWindowResetsAt),
            _settings.ShowLongWindowLabel,
            _settings.ShowLongRemainingPercent,
            _settings.ShowLongResetTime);

        foreach (var displayItem in GetOrderedDisplayItems())
        {
            switch (displayItem)
            {
                case HudDisplayItem.ShortWindow when _settings.ShowShortWindow && !string.IsNullOrWhiteSpace(shortWindowSegments.Full):
                    fullSegments.Add(shortWindowSegments.Full);
                    compactSegments.Add(shortWindowSegments.Compact);
                    break;
                case HudDisplayItem.LongWindow when _settings.ShowLongWindow && !string.IsNullOrWhiteSpace(longWindowSegments.Full):
                    fullSegments.Add(longWindowSegments.Full);
                    compactSegments.Add(longWindowSegments.Compact);
                    break;
                case HudDisplayItem.ResetCredits when _settings.ShowResetCredits:
                    var resetCreditsFull = BuildResetCreditsSegment(snapshot.ResetCreditsAvailable, compact: false);
                    var resetCreditsCompact = BuildResetCreditsSegment(snapshot.ResetCreditsAvailable, compact: true);
                    if (!string.IsNullOrWhiteSpace(resetCreditsFull))
                    {
                        fullSegments.Add(resetCreditsFull);
                        compactSegments.Add(resetCreditsCompact);
                    }
                    break;
            }
        }

        var separator = _settings.ShowSeparatorDots ? "  ·  " : "   ";
        DisplayText = fullSegments.Count > 0 ? string.Join(separator, fullSegments) : "HUD 已隐藏";
        CompactDisplayText = compactSegments.Count > 0 ? string.Join(separator, compactSegments) : DisplayText;
        MinimalDisplayText = BuildMinimalDisplayText(shortWindowSegments.Compact, compactSegments);

        OnPropertyChanged(nameof(ShortWindowMenuText));
        OnPropertyChanged(nameof(LongWindowMenuText));
        UpdateDisplayForegroundBrush();
    }

    [RelayCommand]
    private Task RefreshNowAsync(CancellationToken cancellationToken) => RefreshAsync(cancellationToken);

    [RelayCommand]
    private Task ToggleShortWindowAsync(CancellationToken cancellationToken) =>
        UpdateSettingsAsync(_settings with { ShowShortWindow = !_settings.ShowShortWindow }, cancellationToken);

    [RelayCommand]
    private Task ToggleLongWindowAsync(CancellationToken cancellationToken) =>
        UpdateSettingsAsync(_settings with { ShowLongWindow = !_settings.ShowLongWindow }, cancellationToken);

    [RelayCommand]
    private Task ToggleResetCreditsAsync(CancellationToken cancellationToken) =>
        UpdateSettingsAsync(_settings with { ShowResetCredits = !_settings.ShowResetCredits }, cancellationToken);

    [RelayCommand]
    private Task ToggleAutoLaunchCodexOnStartupAsync(CancellationToken cancellationToken) =>
        UpdateSettingsAsync(_settings with { AutoLaunchCodexOnStartup = !_settings.AutoLaunchCodexOnStartup }, cancellationToken);

    [RelayCommand]
    private Task ToggleAutoRefreshAsync(CancellationToken cancellationToken) =>
        UpdateSettingsAsync(_settings with { AutoRefreshEnabled = !_settings.AutoRefreshEnabled }, cancellationToken);

    [RelayCommand]
    private Task SetDisplayModeAutoAsync(CancellationToken cancellationToken) =>
        UpdateSettingsAsync(_settings with { DisplayMode = HudDisplayMode.Auto }, cancellationToken);

    [RelayCommand]
    private Task SetDisplayModeFullAsync(CancellationToken cancellationToken) =>
        UpdateSettingsAsync(_settings with { DisplayMode = HudDisplayMode.Full }, cancellationToken);

    [RelayCommand]
    private Task SetDisplayModeCompactAsync(CancellationToken cancellationToken) =>
        UpdateSettingsAsync(_settings with { DisplayMode = HudDisplayMode.Compact }, cancellationToken);

    [RelayCommand]
    private Task SetRefreshEvery20SecondsAsync(CancellationToken cancellationToken) =>
        UpdateSettingsAsync(_settings with { RefreshIntervalSeconds = 20 }, cancellationToken);

    [RelayCommand]
    private Task SetRefreshEvery60SecondsAsync(CancellationToken cancellationToken) =>
        UpdateSettingsAsync(_settings with { RefreshIntervalSeconds = 60 }, cancellationToken);

    [RelayCommand]
    private Task SetRefreshEvery300SecondsAsync(CancellationToken cancellationToken) =>
        UpdateSettingsAsync(_settings with { RefreshIntervalSeconds = 300 }, cancellationToken);

    [RelayCommand]
    private Task SetFontSize14Async(CancellationToken cancellationToken) =>
        UpdateSettingsAsync(_settings with { FontSize = 14 }, cancellationToken);

    [RelayCommand]
    private Task SetFontSize16Async(CancellationToken cancellationToken) =>
        UpdateSettingsAsync(_settings with { FontSize = 16 }, cancellationToken);

    [RelayCommand]
    private Task SetFontSize18Async(CancellationToken cancellationToken) =>
        UpdateSettingsAsync(_settings with { FontSize = 18 }, cancellationToken);

    [RelayCommand]
    private Task SetFontSize20Async(CancellationToken cancellationToken) =>
        UpdateSettingsAsync(_settings with { FontSize = 20 }, cancellationToken);

    [RelayCommand]
    private Task SetFontSize22Async(CancellationToken cancellationToken) =>
        UpdateSettingsAsync(_settings with { FontSize = 22 }, cancellationToken);

    [RelayCommand]
    private Task SetFontSize24Async(CancellationToken cancellationToken) =>
        UpdateSettingsAsync(_settings with { FontSize = 24 }, cancellationToken);

    [RelayCommand]
    private Task ToggleHideWhenCodexUnavailableAsync(CancellationToken cancellationToken) =>
        UpdateSettingsAsync(_settings with { HideWhenCodexUnavailable = !_settings.HideWhenCodexUnavailable }, cancellationToken);

    [RelayCommand]
    private Task NudgePositionLeftAsync(CancellationToken cancellationToken) =>
        UpdateSettingsAsync(_settings with { PositionOffsetX = _settings.PositionOffsetX - PositionNudgeStepPx }, cancellationToken);

    [RelayCommand]
    private Task NudgePositionRightAsync(CancellationToken cancellationToken) =>
        UpdateSettingsAsync(_settings with { PositionOffsetX = _settings.PositionOffsetX + PositionNudgeStepPx }, cancellationToken);

    [RelayCommand]
    private Task NudgePositionUpAsync(CancellationToken cancellationToken) =>
        UpdateSettingsAsync(_settings with { PositionOffsetY = _settings.PositionOffsetY - PositionNudgeStepPx }, cancellationToken);

    [RelayCommand]
    private Task NudgePositionDownAsync(CancellationToken cancellationToken) =>
        UpdateSettingsAsync(_settings with { PositionOffsetY = _settings.PositionOffsetY + PositionNudgeStepPx }, cancellationToken);

    [RelayCommand]
    private Task ResetPositionAsync(CancellationToken cancellationToken) =>
        UpdateSettingsAsync(_settings with { PositionOffsetX = 0, PositionOffsetY = 0 }, cancellationToken);

    [RelayCommand]
    private Task SetColorModeDefaultAsync(CancellationToken cancellationToken) =>
        UpdateSettingsAsync(_settings with { ColorMode = HudColorMode.Default }, cancellationToken);

    [RelayCommand]
    private Task SetColorModeCustomAsync(CancellationToken cancellationToken) =>
        UpdateSettingsAsync(_settings with { ColorMode = HudColorMode.Custom }, cancellationToken);

    [RelayCommand]
    private Task SetColorModeDynamicAsync(CancellationToken cancellationToken) =>
        UpdateSettingsAsync(_settings with { ColorMode = HudColorMode.DynamicByRemainingPercent }, cancellationToken);

    [RelayCommand]
    private Task ApplyBaseForegroundColorAsync(CancellationToken cancellationToken) =>
        UpdateSettingsAsync(_settings with { BaseForegroundColorHex = BaseForegroundColorInput }, cancellationToken);

    [RelayCommand]
    private Task ApplyDynamicHighRemainingColorAsync(CancellationToken cancellationToken) =>
        UpdateSettingsAsync(_settings with { DynamicHighRemainingColorHex = DynamicHighRemainingColorInput }, cancellationToken);

    [RelayCommand]
    private Task ApplyDynamicMediumRemainingColorAsync(CancellationToken cancellationToken) =>
        UpdateSettingsAsync(_settings with { DynamicMediumRemainingColorHex = DynamicMediumRemainingColorInput }, cancellationToken);

    [RelayCommand]
    private Task ApplyDynamicLowRemainingColorAsync(CancellationToken cancellationToken) =>
        UpdateSettingsAsync(_settings with { DynamicLowRemainingColorHex = DynamicLowRemainingColorInput }, cancellationToken);

    [RelayCommand]
    private Task ResetColorsToDefaultAsync(CancellationToken cancellationToken) =>
        UpdateSettingsAsync(
            _settings with
            {
                BaseForegroundColorHex = DefaultForegroundColorHex,
                DynamicHighRemainingColorHex = DefaultHighRemainingColorHex,
                DynamicMediumRemainingColorHex = DefaultMediumRemainingColorHex,
                DynamicLowRemainingColorHex = DefaultLowRemainingColorHex
            },
            cancellationToken);

    [RelayCommand]
    private Task ApplyDynamicThresholdsAsync(CancellationToken cancellationToken)
    {
        var lowThreshold = ParsePercentInput(DynamicLowThresholdPercentInput, _settings.DynamicLowRemainingThresholdPercent);
        var mediumThreshold = ParsePercentInput(DynamicMediumThresholdPercentInput, _settings.DynamicMediumRemainingThresholdPercent);
        mediumThreshold = Math.Clamp(mediumThreshold, 1, 100);
        lowThreshold = Math.Clamp(lowThreshold, 0, mediumThreshold - 1);

        return UpdateSettingsAsync(
            _settings with
            {
                DynamicLowRemainingThresholdPercent = lowThreshold,
                DynamicMediumRemainingThresholdPercent = mediumThreshold
            },
            cancellationToken);
    }

    [RelayCommand]
    private async Task ExportSettingsAsync(CancellationToken cancellationToken)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出 HUD 设置",
            Filter = "JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*",
            FileName = $"YiboCodexHUD-settings-{DateTime.Now:yyyyMMdd-HHmmss}.json"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var json = JsonSerializer.Serialize(_settings, ImportExportSerializerOptions);
        await File.WriteAllTextAsync(dialog.FileName, json, cancellationToken);
    }

    [RelayCommand]
    private async Task ImportSettingsAsync(CancellationToken cancellationToken)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入 HUD 设置",
            Filter = "JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await using var stream = File.OpenRead(dialog.FileName);
        var imported = await JsonSerializer.DeserializeAsync<HudSettings>(stream, ImportExportSerializerOptions, cancellationToken);
        if (imported is null)
        {
            return;
        }

        await UpdateSettingsAsync(imported, cancellationToken);
    }

    [RelayCommand]
    private Task ToggleShowShortWindowLabelAsync(CancellationToken cancellationToken) =>
        UpdateSettingsAsync(_settings with { ShowShortWindowLabel = !_settings.ShowShortWindowLabel }, cancellationToken);

    [RelayCommand]
    private Task ToggleShowShortRemainingPercentAsync(CancellationToken cancellationToken) =>
        UpdateSettingsAsync(_settings with { ShowShortRemainingPercent = !_settings.ShowShortRemainingPercent }, cancellationToken);

    [RelayCommand]
    private Task ToggleShowShortResetTimeAsync(CancellationToken cancellationToken) =>
        UpdateSettingsAsync(_settings with { ShowShortResetTime = !_settings.ShowShortResetTime }, cancellationToken);

    [RelayCommand]
    private Task ToggleShowLongWindowLabelAsync(CancellationToken cancellationToken) =>
        UpdateSettingsAsync(_settings with { ShowLongWindowLabel = !_settings.ShowLongWindowLabel }, cancellationToken);

    [RelayCommand]
    private Task ToggleShowLongRemainingPercentAsync(CancellationToken cancellationToken) =>
        UpdateSettingsAsync(_settings with { ShowLongRemainingPercent = !_settings.ShowLongRemainingPercent }, cancellationToken);

    [RelayCommand]
    private Task ToggleShowLongResetTimeAsync(CancellationToken cancellationToken) =>
        UpdateSettingsAsync(_settings with { ShowLongResetTime = !_settings.ShowLongResetTime }, cancellationToken);

    [RelayCommand]
    private Task ToggleShowResetCreditsLabelAsync(CancellationToken cancellationToken) =>
        UpdateSettingsAsync(_settings with { ShowResetCreditsLabel = !_settings.ShowResetCreditsLabel }, cancellationToken);

    [RelayCommand]
    private Task ToggleShowSeparatorDotsAsync(CancellationToken cancellationToken) =>
        UpdateSettingsAsync(_settings with { ShowSeparatorDots = !_settings.ShowSeparatorDots }, cancellationToken);

    [RelayCommand]
    private Task MoveShortWindowUpAsync(CancellationToken cancellationToken) =>
        MoveDisplayItemAsync(HudDisplayItem.ShortWindow, moveUp: true, cancellationToken);

    [RelayCommand]
    private Task MoveShortWindowDownAsync(CancellationToken cancellationToken) =>
        MoveDisplayItemAsync(HudDisplayItem.ShortWindow, moveUp: false, cancellationToken);

    [RelayCommand]
    private Task MoveLongWindowUpAsync(CancellationToken cancellationToken) =>
        MoveDisplayItemAsync(HudDisplayItem.LongWindow, moveUp: true, cancellationToken);

    [RelayCommand]
    private Task MoveLongWindowDownAsync(CancellationToken cancellationToken) =>
        MoveDisplayItemAsync(HudDisplayItem.LongWindow, moveUp: false, cancellationToken);

    [RelayCommand]
    private Task MoveResetCreditsUpAsync(CancellationToken cancellationToken) =>
        MoveDisplayItemAsync(HudDisplayItem.ResetCredits, moveUp: true, cancellationToken);

    [RelayCommand]
    private Task MoveResetCreditsDownAsync(CancellationToken cancellationToken) =>
        MoveDisplayItemAsync(HudDisplayItem.ResetCredits, moveUp: false, cancellationToken);

    private async Task UpdateSettingsAsync(HudSettings settings, CancellationToken cancellationToken)
    {
        await _settingsGate.WaitAsync(cancellationToken);
        try
        {
            _settings = NormalizeSettings(settings);
            await _hudSettingsStore.SaveAsync(_settings, cancellationToken);
            ApplySettings();

            if (_latestSnapshot is not null)
            {
                ApplySnapshot(_latestSnapshot);
            }
            else
            {
                UpdateDisplayForegroundBrush();
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to update HUD settings.");
        }
        finally
        {
            _settingsGate.Release();
        }
    }

    private void ApplySettings()
    {
        SyncEditorInputs();

        OnPropertyChanged(nameof(ShowShortWindow));
        OnPropertyChanged(nameof(ShowLongWindow));
        OnPropertyChanged(nameof(ShowResetCredits));
        OnPropertyChanged(nameof(CanToggleShortWindow));
        OnPropertyChanged(nameof(CanToggleLongWindow));
        OnPropertyChanged(nameof(CanToggleResetCredits));
        OnPropertyChanged(nameof(IsOnlyOneDisplayItemEnabled));
        OnPropertyChanged(nameof(DisplayItemsHintText));
        OnPropertyChanged(nameof(AutoLaunchCodexOnStartup));
        OnPropertyChanged(nameof(AutoRefreshEnabled));
        OnPropertyChanged(nameof(IsDisplayModeAuto));
        OnPropertyChanged(nameof(IsDisplayModeFull));
        OnPropertyChanged(nameof(IsDisplayModeCompact));
        OnPropertyChanged(nameof(IsRefreshEvery20Seconds));
        OnPropertyChanged(nameof(IsRefreshEvery60Seconds));
        OnPropertyChanged(nameof(IsRefreshEvery300Seconds));
        OnPropertyChanged(nameof(IsFontSize14));
        OnPropertyChanged(nameof(IsFontSize16));
        OnPropertyChanged(nameof(IsFontSize18));
        OnPropertyChanged(nameof(IsFontSize20));
        OnPropertyChanged(nameof(IsFontSize22));
        OnPropertyChanged(nameof(IsFontSize24));
        OnPropertyChanged(nameof(HideWhenCodexUnavailable));
        OnPropertyChanged(nameof(DisplayFontSize));
        OnPropertyChanged(nameof(PositionOffsetX));
        OnPropertyChanged(nameof(PositionOffsetY));
        OnPropertyChanged(nameof(PositionOffsetSummary));
        OnPropertyChanged(nameof(IsColorModeDefault));
        OnPropertyChanged(nameof(IsColorModeCustom));
        OnPropertyChanged(nameof(IsColorModeDynamic));
        OnPropertyChanged(nameof(ColorModeHintText));
        OnPropertyChanged(nameof(BaseForegroundPreviewBrush));
        OnPropertyChanged(nameof(DynamicHighRemainingPreviewBrush));
        OnPropertyChanged(nameof(DynamicMediumRemainingPreviewBrush));
        OnPropertyChanged(nameof(DynamicLowRemainingPreviewBrush));
        OnPropertyChanged(nameof(DynamicMediumRemainingThresholdPercent));
        OnPropertyChanged(nameof(DynamicLowRemainingThresholdPercent));
        OnPropertyChanged(nameof(ThresholdHintText));
        OnPropertyChanged(nameof(TextOpacityPercent));
        OnPropertyChanged(nameof(DisplayOpacity));
        OnPropertyChanged(nameof(TextOpacitySummary));
        OnPropertyChanged(nameof(ShowShortWindowLabel));
        OnPropertyChanged(nameof(ShowShortRemainingPercent));
        OnPropertyChanged(nameof(ShowShortResetTime));
        OnPropertyChanged(nameof(ShowLongWindowLabel));
        OnPropertyChanged(nameof(ShowLongRemainingPercent));
        OnPropertyChanged(nameof(ShowLongResetTime));
        OnPropertyChanged(nameof(ShowResetCreditsLabel));
        OnPropertyChanged(nameof(ShowSeparatorDots));
        OnPropertyChanged(nameof(ShortWindowOrder));
        OnPropertyChanged(nameof(LongWindowOrder));
        OnPropertyChanged(nameof(ResetCreditsOrder));
        OnPropertyChanged(nameof(ShortWindowOrderSummary));
        OnPropertyChanged(nameof(LongWindowOrderSummary));
        OnPropertyChanged(nameof(ResetCreditsOrderSummary));
        OnPropertyChanged(nameof(DisplayOrderRows));
        OnPropertyChanged(nameof(CanMoveShortWindowUp));
        OnPropertyChanged(nameof(CanMoveShortWindowDown));
        OnPropertyChanged(nameof(CanMoveLongWindowUp));
        OnPropertyChanged(nameof(CanMoveLongWindowDown));
        OnPropertyChanged(nameof(CanMoveResetCreditsUp));
        OnPropertyChanged(nameof(CanMoveResetCreditsDown));
        OnPropertyChanged(nameof(AutoRefreshHintText));
        UpdateDisplayForegroundBrush();
    }

    public string SelectDisplayText(double contentWidthPx)
    {
        if (!IsCodexWindowAvailable && _settings.HideWhenCodexUnavailable)
        {
            return string.Empty;
        }

        return _settings.DisplayMode switch
        {
            HudDisplayMode.Full => DisplayText,
            HudDisplayMode.Compact => CompactDisplayText,
            _ when contentWidthPx >= 620d => DisplayText,
            _ when contentWidthPx >= 420d => CompactDisplayText,
            _ => string.Empty
        };
    }

    public IReadOnlyList<string> GetAutoDisplayCandidates()
    {
        var candidates = new List<string>();
        AddCandidate(candidates, DisplayText);
        AddCandidate(candidates, CompactDisplayText);
        AddCandidate(candidates, MinimalDisplayText);
        AddCandidate(candidates, string.Empty);
        return candidates;
    }

    private void UpdateDisplayForegroundBrush()
    {
        DisplayForegroundBrush = ResolveDisplayForegroundBrush();
    }

    private Brush ResolveDisplayForegroundBrush()
    {
        Brush baseBrush = _settings.ColorMode switch
        {
            HudColorMode.Custom => CreateBrushFromHex(_settings.BaseForegroundColorHex, DefaultForegroundColorHex),
            HudColorMode.DynamicByRemainingPercent => ResolveDynamicForegroundBrush(),
            _ => SystemColors.GrayTextBrush
        };

        return ApplyOpacityToBrush(baseBrush, DisplayOpacity);
    }

    private Brush ResolveDynamicForegroundBrush()
    {
        var remainingPercent = GetMostUrgentRemainingPercent();
        if (!remainingPercent.HasValue)
        {
            return CreateBrushFromHex(_settings.DynamicHighRemainingColorHex, DefaultHighRemainingColorHex);
        }

        if (remainingPercent.Value < _settings.DynamicLowRemainingThresholdPercent)
        {
            return CreateBrushFromHex(_settings.DynamicLowRemainingColorHex, DefaultLowRemainingColorHex);
        }

        if (remainingPercent.Value < _settings.DynamicMediumRemainingThresholdPercent)
        {
            return CreateBrushFromHex(_settings.DynamicMediumRemainingColorHex, DefaultMediumRemainingColorHex);
        }

        return CreateBrushFromHex(_settings.DynamicHighRemainingColorHex, DefaultHighRemainingColorHex);
    }

    private double? GetMostUrgentRemainingPercent()
    {
        var candidates = new List<double>();

        if (_settings.ShowShortWindow && _settings.ShowShortRemainingPercent)
        {
            AddRemainingPercentCandidate(candidates, _latestSnapshot?.ShortWindowUsedPercent);
        }

        if (_settings.ShowLongWindow && _settings.ShowLongRemainingPercent)
        {
            AddRemainingPercentCandidate(candidates, _latestSnapshot?.LongWindowUsedPercent);
        }

        return candidates.Count == 0 ? null : candidates.Min();
    }

    private static void AddRemainingPercentCandidate(ICollection<double> candidates, double? usedPercent)
    {
        if (!usedPercent.HasValue)
        {
            return;
        }

        candidates.Add(Math.Clamp(100d - usedPercent.Value, 0d, 100d));
    }

    private (string Full, string Compact) BuildUsageWindowSegments(
        int? minutes,
        double? usedPercent,
        string fullResetAt,
        string compactResetAt,
        bool showWindowLabel,
        bool showRemainingPercent,
        bool showResetAt)
    {
        var fullParts = new List<string>();
        var compactParts = new List<string>();

        if (showWindowLabel)
        {
            var label = FormatWindow(minutes);
            fullParts.Add(label);
            compactParts.Add(label);
        }

        if (showRemainingPercent)
        {
            var remainingPercent = FormatRemainingPercent(usedPercent);
            fullParts.Add($"剩余 {remainingPercent}");
            compactParts.Add(remainingPercent);
        }

        if (showResetAt)
        {
            fullParts.Add($"重置 {fullResetAt}");
            compactParts.Add(compactResetAt);
        }

        return (string.Join(" ", fullParts), string.Join(" ", compactParts));
    }

    private string BuildResetCreditsSegment(int? value, bool compact)
    {
        var credits = FormatCredits(value);
        return _settings.ShowResetCreditsLabel
            ? compact ? $"重置 {credits}" : $"重置次数 {credits}"
            : credits;
    }

    private string BuildMinimalDisplayText(string shortWindowCompact, IReadOnlyList<string> compactSegments)
    {
        if (_settings.ShowShortWindow && !string.IsNullOrWhiteSpace(shortWindowCompact))
        {
            return shortWindowCompact;
        }

        return compactSegments.FirstOrDefault(static item => !string.IsNullOrWhiteSpace(item)) ?? DisplayText;
    }

    private static string FormatRemainingPercent(double? usedPercent)
    {
        if (!usedPercent.HasValue)
        {
            return "--";
        }

        var remaining = Math.Clamp(100d - usedPercent.Value, 0d, 100d);
        return $"{remaining:0.#}%";
    }

    private static string FormatWindow(int? minutes) => minutes switch
    {
        null => "--",
        >= 10080 and var totalMinutes => $"{totalMinutes / 10080} 周",
        >= 1440 and var totalMinutes => $"{totalMinutes / 1440} 天",
        >= 60 and var totalMinutes => $"{totalMinutes / 60} 小时",
        var totalMinutes => $"{totalMinutes} 分钟"
    };

    private static string FormatCredits(int? value)
    {
        if (!value.HasValue)
        {
            return "--";
        }

        return value.Value == int.MaxValue ? "不限" : value.Value.ToString();
    }

    private static string FormatResetTime(DateTimeOffset? value)
    {
        if (!value.HasValue)
        {
            return "--";
        }

        return value.Value.ToLocalTime().ToString("HH:mm");
    }

    private static string FormatResetDate(DateTimeOffset? value)
    {
        if (!value.HasValue)
        {
            return "--";
        }

        var local = value.Value.ToLocalTime();
        return $"{local.Month}月{local.Day}日";
    }

    private static string FormatCompactResetDate(DateTimeOffset? value)
    {
        if (!value.HasValue)
        {
            return "--";
        }

        var local = value.Value.ToLocalTime();
        return $"{local.Month}/{local.Day}";
    }

    private static string FormatErrorMessage(Exception exception)
    {
        if (exception is TimeoutException)
        {
            return "用量读取失败: 请求超时";
        }

        if (exception is OperationCanceledException)
        {
            return "用量读取失败: 读取被取消";
        }

        var message = exception.Message;
        if (message.Contains("failed to fetch codex rate limits", StringComparison.OrdinalIgnoreCase)
            || message.Contains("error sending request for url", StringComparison.OrdinalIgnoreCase))
        {
            return "用量读取失败: Codex 服务暂不可用";
        }

        if (message.Contains("app-server", StringComparison.OrdinalIgnoreCase))
        {
            return "用量读取失败: Codex 服务异常";
        }

        return "用量读取失败";
    }

    private static string BuildWindowMenuText(int? minutes, string fallbackName)
    {
        var label = minutes.HasValue ? FormatWindow(minutes) : fallbackName;
        return $"显示 {label} 额度";
    }

    private string BuildDisplayOrderSummary(HudDisplayItem item, string label)
    {
        var order = GetDisplayOrder(item) + 1;
        return $"第 {order} 位: {label}";
    }

    private IReadOnlyList<DisplayOrderRow> BuildDisplayOrderRows()
    {
        return GetOrderedDisplayItems()
            .Select(item => new DisplayOrderRow(
                BuildDisplayOrderSummary(item, GetDisplayItemLabel(item)),
                GetMoveUpCommand(item),
                GetMoveDownCommand(item),
                CanMoveDisplayItem(item, moveUp: true),
                CanMoveDisplayItem(item, moveUp: false)))
            .ToArray();
    }

    private IReadOnlyList<HudDisplayItem> GetOrderedDisplayItems()
    {
        return new[]
        {
            HudDisplayItem.ShortWindow,
            HudDisplayItem.LongWindow,
            HudDisplayItem.ResetCredits
        }
        .OrderBy(GetDisplayOrder)
        .ToArray();
    }

    private int GetDisplayOrder(HudDisplayItem item) => item switch
    {
        HudDisplayItem.ShortWindow => _settings.ShortWindowOrder,
        HudDisplayItem.LongWindow => _settings.LongWindowOrder,
        _ => _settings.ResetCreditsOrder
    };

    private bool CanMoveDisplayItem(HudDisplayItem item, bool moveUp)
    {
        var order = GetDisplayOrder(item);
        return moveUp ? order > 0 : order < 2;
    }

    private Task MoveDisplayItemAsync(HudDisplayItem item, bool moveUp, CancellationToken cancellationToken)
    {
        if (!CanMoveDisplayItem(item, moveUp))
        {
            return Task.CompletedTask;
        }

        var ordered = GetOrderedDisplayItems().ToList();
        var index = ordered.IndexOf(item);
        var swapIndex = moveUp ? index - 1 : index + 1;
        (ordered[index], ordered[swapIndex]) = (ordered[swapIndex], ordered[index]);

        return UpdateSettingsAsync(
            _settings with
            {
                ShortWindowOrder = ordered.IndexOf(HudDisplayItem.ShortWindow),
                LongWindowOrder = ordered.IndexOf(HudDisplayItem.LongWindow),
                ResetCreditsOrder = ordered.IndexOf(HudDisplayItem.ResetCredits)
            },
            cancellationToken);
    }

    private static string GetDisplayItemLabel(HudDisplayItem item) => item switch
    {
        HudDisplayItem.ShortWindow => "短周期",
        HudDisplayItem.LongWindow => "长周期",
        _ => "重置次数"
    };

    private ICommand GetMoveUpCommand(HudDisplayItem item) => item switch
    {
        HudDisplayItem.ShortWindow => MoveShortWindowUpCommand,
        HudDisplayItem.LongWindow => MoveLongWindowUpCommand,
        _ => MoveResetCreditsUpCommand
    };

    private ICommand GetMoveDownCommand(HudDisplayItem item) => item switch
    {
        HudDisplayItem.ShortWindow => MoveShortWindowDownCommand,
        HudDisplayItem.LongWindow => MoveLongWindowDownCommand,
        _ => MoveResetCreditsDownCommand
    };

    private static void AddCandidate(ICollection<string> candidates, string candidate)
    {
        if (!candidates.Contains(candidate))
        {
            candidates.Add(candidate);
        }
    }

    private HudSettings NormalizeSettings(HudSettings settings)
    {
        var mediumThreshold = Math.Clamp(settings.DynamicMediumRemainingThresholdPercent, 1, 100);
        var lowThreshold = Math.Clamp(settings.DynamicLowRemainingThresholdPercent, 0, mediumThreshold - 1);

        var normalized = settings with
        {
            PositionOffsetX = Math.Clamp(settings.PositionOffsetX, -MaxPositionOffsetPx, MaxPositionOffsetPx),
            PositionOffsetY = Math.Clamp(settings.PositionOffsetY, -MaxPositionOffsetPx, MaxPositionOffsetPx),
            RefreshIntervalSeconds = settings.RefreshIntervalSeconds switch
            {
                20 or 60 or 300 => settings.RefreshIntervalSeconds,
                _ => 20
            },
            FontSize = settings.FontSize switch
            {
                14 or 16 or 18 or 20 or 22 or 24 => settings.FontSize,
                _ => 18
            },
            BaseForegroundColorHex = NormalizeColorHex(settings.BaseForegroundColorHex, DefaultForegroundColorHex),
            DynamicHighRemainingColorHex = NormalizeColorHex(settings.DynamicHighRemainingColorHex, DefaultHighRemainingColorHex),
            DynamicMediumRemainingColorHex = NormalizeColorHex(settings.DynamicMediumRemainingColorHex, DefaultMediumRemainingColorHex),
            DynamicLowRemainingColorHex = NormalizeColorHex(settings.DynamicLowRemainingColorHex, DefaultLowRemainingColorHex),
            DynamicMediumRemainingThresholdPercent = mediumThreshold,
            DynamicLowRemainingThresholdPercent = lowThreshold,
            TextOpacityPercent = Math.Clamp((int)Math.Round(settings.TextOpacityPercent / 10d) * 10, 10, 100),
            ShortWindowOrder = settings.ShortWindowOrder,
            LongWindowOrder = settings.LongWindowOrder,
            ResetCreditsOrder = settings.ResetCreditsOrder,
            SettingsWindowWidth = settings.SettingsWindowWidth > 0d ? settings.SettingsWindowWidth : DefaultSettingsWindowWidth,
            SettingsWindowHeight = settings.SettingsWindowHeight > 0d ? settings.SettingsWindowHeight : DefaultSettingsWindowHeight
        };

        var orderMap = GetNormalizedOrderMap(settings.ShortWindowOrder, settings.LongWindowOrder, settings.ResetCreditsOrder);
        normalized = normalized with
        {
            ShortWindowOrder = orderMap[HudDisplayItem.ShortWindow],
            LongWindowOrder = orderMap[HudDisplayItem.LongWindow],
            ResetCreditsOrder = orderMap[HudDisplayItem.ResetCredits]
        };

        if (!normalized.ShowShortWindow && !normalized.ShowLongWindow && !normalized.ShowResetCredits)
        {
            normalized = normalized with { ShowShortWindow = true };
        }

        if (normalized.ShowShortWindow
            && !normalized.ShowShortWindowLabel
            && !normalized.ShowShortRemainingPercent
            && !normalized.ShowShortResetTime)
        {
            normalized = normalized with { ShowShortRemainingPercent = true };
        }

        if (normalized.ShowLongWindow
            && !normalized.ShowLongWindowLabel
            && !normalized.ShowLongRemainingPercent
            && !normalized.ShowLongResetTime)
        {
            normalized = normalized with { ShowLongRemainingPercent = true };
        }

        return normalized;
    }

    private static Dictionary<HudDisplayItem, int> GetNormalizedOrderMap(int shortOrder, int longOrder, int resetOrder)
    {
        var ordered = new List<(HudDisplayItem Item, int Order)>
        {
            (HudDisplayItem.ShortWindow, shortOrder),
            (HudDisplayItem.LongWindow, longOrder),
            (HudDisplayItem.ResetCredits, resetOrder)
        }
        .OrderBy(entry => entry.Order)
        .ThenBy(entry => (int)entry.Item)
        .ToList();

        return ordered
            .Select((entry, index) => new { entry.Item, Index = index })
            .ToDictionary(entry => entry.Item, entry => entry.Index);
    }

    private void SyncEditorInputs()
    {
        _isSyncingEditorInputs = true;
        BaseForegroundColorInput = _settings.BaseForegroundColorHex;
        DynamicHighRemainingColorInput = _settings.DynamicHighRemainingColorHex;
        DynamicMediumRemainingColorInput = _settings.DynamicMediumRemainingColorHex;
        DynamicLowRemainingColorInput = _settings.DynamicLowRemainingColorHex;
        DynamicMediumThresholdPercentInput = _settings.DynamicMediumRemainingThresholdPercent.ToString();
        DynamicLowThresholdPercentInput = _settings.DynamicLowRemainingThresholdPercent.ToString();
        TextOpacityEditorPercent = _settings.TextOpacityPercent;
        _isSyncingEditorInputs = false;
    }

    private static string NormalizeColorHex(string? value, string fallback)
    {
        try
        {
            var parsed = ColorConverter.ConvertFromString(value);
            return parsed is Color color ? color.ToString() : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static Brush CreateBrushFromHex(string? value, string fallback)
    {
        try
        {
            var parsed = ColorConverter.ConvertFromString(value);
            if (parsed is Color color)
            {
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                return brush;
            }
        }
        catch
        {
        }

        var fallbackColor = (Color)ColorConverter.ConvertFromString(fallback)!;
        var fallbackBrush = new SolidColorBrush(fallbackColor);
        fallbackBrush.Freeze();
        return fallbackBrush;
    }

    private static Brush ApplyOpacityToBrush(Brush brush, double opacity)
    {
        if (brush is not SolidColorBrush solidBrush)
        {
            var clonedBrush = brush.Clone();
            clonedBrush.Opacity = opacity;
            clonedBrush.Freeze();
            return clonedBrush;
        }

        var color = solidBrush.Color;
        var alpha = (byte)Math.Clamp((int)Math.Round(color.A * opacity), 0, 255);
        var adjustedBrush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        adjustedBrush.Freeze();
        return adjustedBrush;
    }

    private static int ParsePercentInput(string? value, int fallback)
    {
        return int.TryParse(value, out var parsed) ? parsed : fallback;
    }

    partial void OnBaseForegroundColorInputChanged(string value)
    {
        if (_isSyncingEditorInputs)
        {
            return;
        }

        _ = UpdateSettingsAsync(_settings with { BaseForegroundColorHex = value }, CancellationToken.None);
    }

    partial void OnDynamicHighRemainingColorInputChanged(string value)
    {
        if (_isSyncingEditorInputs)
        {
            return;
        }

        _ = UpdateSettingsAsync(_settings with { DynamicHighRemainingColorHex = value }, CancellationToken.None);
    }

    partial void OnDynamicMediumRemainingColorInputChanged(string value)
    {
        if (_isSyncingEditorInputs)
        {
            return;
        }

        _ = UpdateSettingsAsync(_settings with { DynamicMediumRemainingColorHex = value }, CancellationToken.None);
    }

    partial void OnDynamicLowRemainingColorInputChanged(string value)
    {
        if (_isSyncingEditorInputs)
        {
            return;
        }

        _ = UpdateSettingsAsync(_settings with { DynamicLowRemainingColorHex = value }, CancellationToken.None);
    }

    partial void OnDynamicMediumThresholdPercentInputChanged(string value)
    {
        if (_isSyncingEditorInputs)
        {
            return;
        }

        AutoApplyDynamicThresholds();
    }

    partial void OnDynamicLowThresholdPercentInputChanged(string value)
    {
        if (_isSyncingEditorInputs)
        {
            return;
        }

        AutoApplyDynamicThresholds();
    }

    partial void OnTextOpacityEditorPercentChanged(int value)
    {
        if (_isSyncingEditorInputs)
        {
            return;
        }

        var normalizedOpacity = Math.Clamp((int)Math.Round(value / 10d) * 10, 10, 100);
        if (normalizedOpacity != value)
        {
            _isSyncingEditorInputs = true;
            TextOpacityEditorPercent = normalizedOpacity;
            _isSyncingEditorInputs = false;
        }

        _ = UpdateSettingsAsync(_settings with { TextOpacityPercent = normalizedOpacity }, CancellationToken.None);
    }

    private void AutoApplyDynamicThresholds()
    {
        var lowThreshold = ParsePercentInput(DynamicLowThresholdPercentInput, _settings.DynamicLowRemainingThresholdPercent);
        var mediumThreshold = ParsePercentInput(DynamicMediumThresholdPercentInput, _settings.DynamicMediumRemainingThresholdPercent);
        mediumThreshold = Math.Clamp(mediumThreshold, 1, 100);
        lowThreshold = Math.Clamp(lowThreshold, 0, mediumThreshold - 1);

        _ = UpdateSettingsAsync(
            _settings with
            {
                DynamicLowRemainingThresholdPercent = lowThreshold,
                DynamicMediumRemainingThresholdPercent = mediumThreshold
            },
            CancellationToken.None);
    }

    private static string FormatSignedOffset(int value) => value > 0 ? $"+{value}" : value.ToString();

    private int GetEnabledDisplayItemCount()
    {
        var count = 0;
        if (_settings.ShowShortWindow) count++;
        if (_settings.ShowLongWindow) count++;
        if (_settings.ShowResetCredits) count++;
        return count;
    }

    private async Task TryLaunchCodexOnStartupAsync(CancellationToken cancellationToken)
    {
        if (!_settings.AutoLaunchCodexOnStartup)
        {
            return;
        }

        if (_windowTracker.GetTrackedWindow() is not null || _codexDesktopLauncher.IsCodexProcessRunning())
        {
            return;
        }

        try
        {
            await _codexDesktopLauncher.TryLaunchAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to launch Codex desktop during HUD startup.");
        }
    }

    public sealed record DisplayOrderRow(
        string Summary,
        ICommand MoveUpCommand,
        ICommand MoveDownCommand,
        bool CanMoveUp,
        bool CanMoveDown);
}
