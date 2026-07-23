namespace YiboCodexHUD.Infrastructure.Models;

public sealed record TokenUsageState
{
    public DateTimeOffset? CurrentPeriodStartedAt { get; init; }

    public long? CurrentPeriodInputTokens { get; init; }

    public long? CurrentPeriodOutputTokens { get; init; }

    public DateTimeOffset? LastShortWindowResetsAt { get; init; }

    public DateTimeOffset? LastLongWindowResetsAt { get; init; }

    public int? LastResetCreditsAvailable { get; init; }

    public long? LastObservedInputTokens { get; init; }

    public long? LastObservedOutputTokens { get; init; }

    public DateTimeOffset? LastSuccessfulRefreshAt { get; init; }
}
