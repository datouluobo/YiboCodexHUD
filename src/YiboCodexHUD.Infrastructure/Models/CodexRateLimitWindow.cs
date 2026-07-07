namespace YiboCodexHUD.Infrastructure.Models;

internal sealed class CodexRateLimitWindow
{
    public double UsedPercent { get; init; }

    public int? WindowDurationMins { get; init; }

    public long? ResetsAt { get; init; }
}
