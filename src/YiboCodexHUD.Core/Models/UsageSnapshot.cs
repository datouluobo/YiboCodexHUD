namespace YiboCodexHUD.Core.Models;

public sealed record UsageSnapshot
{
    public string? AccountEmail { get; init; }

    public string? PlanType { get; init; }

    public double? ShortWindowUsedPercent { get; init; }

    public int? ShortWindowMinutes { get; init; }

    public DateTimeOffset? ShortWindowResetsAt { get; init; }

    public double? LongWindowUsedPercent { get; init; }

    public int? LongWindowMinutes { get; init; }

    public DateTimeOffset? LongWindowResetsAt { get; init; }

    public int? ResetCreditsAvailable { get; init; }

    public IReadOnlyList<DateTimeOffset> ResetCreditExpirations { get; init; } = Array.Empty<DateTimeOffset>();

    public DateTimeOffset FetchedAt { get; init; }
}
