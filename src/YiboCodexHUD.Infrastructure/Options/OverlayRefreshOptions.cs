namespace YiboCodexHUD.Infrastructure.Options;

public sealed class OverlayRefreshOptions
{
    public const string SectionName = "OverlayRefresh";

    public int RefreshIntervalSeconds { get; set; } = 300;

    public int RetryIntervalSeconds { get; set; } = 30;
}
