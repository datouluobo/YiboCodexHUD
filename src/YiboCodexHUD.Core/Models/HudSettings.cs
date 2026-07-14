namespace YiboCodexHUD.Core.Models;

public sealed record HudSettings
{
    public bool AutoLaunchCodexOnStartup { get; init; }

    public bool ShowShortWindow { get; init; } = true;

    public bool ShowLongWindow { get; init; } = true;

    public bool ShowResetCredits { get; init; } = true;

    public bool AutoRefreshEnabled { get; init; } = true;

    public int RefreshIntervalSeconds { get; init; } = 20;

    public HudDisplayMode DisplayMode { get; init; } = HudDisplayMode.Auto;

    public int FontSize { get; init; } = 18;

    public bool HideWhenCodexUnavailable { get; init; } = true;

    // Legacy shared offsets kept for backward-compatible imports.
    public int PositionOffsetX { get; init; }

    public int PositionOffsetY { get; init; }

    public HudHorizontalAlignment HorizontalAlignment { get; init; } = HudHorizontalAlignment.Center;

    public int LeftPositionOffsetX { get; init; }

    public int LeftPositionOffsetY { get; init; }

    public int CenterPositionOffsetX { get; init; }

    public int CenterPositionOffsetY { get; init; }

    public int RightPositionOffsetX { get; init; }

    public int RightPositionOffsetY { get; init; }

    public HudColorMode ColorMode { get; init; } = HudColorMode.Default;

    public string BaseForegroundColorHex { get; init; } = "#FF6A6A6A";

    public string DynamicHighRemainingColorHex { get; init; } = "#FF6A6A6A";

    public string DynamicMediumRemainingColorHex { get; init; } = "#FFC28A22";

    public string DynamicLowRemainingColorHex { get; init; } = "#FFC24A3A";

    public int DynamicMediumRemainingThresholdPercent { get; init; } = 50;

    public int DynamicLowRemainingThresholdPercent { get; init; } = 20;

    public int TextOpacityPercent { get; init; } = 100;

    public bool ShowShortWindowLabel { get; init; } = true;

    public bool ShowShortRemainingPercent { get; init; } = true;

    public bool ShowShortResetTime { get; init; } = true;

    public bool ShowLongWindowLabel { get; init; } = true;

    public bool ShowLongRemainingPercent { get; init; } = true;

    public bool ShowLongResetTime { get; init; } = true;

    public bool ShowResetCreditsLabel { get; init; } = true;

    public bool ShowResetCreditsNearestExpiration { get; init; }

    public bool ShowResetCreditsAllExpirations { get; init; }

    public bool ShowSeparatorDots { get; init; } = true;

    public int ShortWindowOrder { get; init; }

    public int LongWindowOrder { get; init; } = 1;

    public int ResetCreditsOrder { get; init; } = 2;

    public double? SettingsWindowLeft { get; init; }

    public double? SettingsWindowTop { get; init; }

    public double SettingsWindowWidth { get; init; } = 620d;

    public double SettingsWindowHeight { get; init; } = 820d;
}
