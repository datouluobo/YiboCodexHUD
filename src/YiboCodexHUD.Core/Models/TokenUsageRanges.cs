namespace YiboCodexHUD.Core.Models;

public sealed record TokenUsageRanges(
    TokenUsageRangeSnapshot? Current,
    TokenUsageRangeSnapshot? Today,
    TokenUsageRangeSnapshot? CurrentPeriod,
    TokenUsageRangeSnapshot? Cumulative)
{
    public static TokenUsageRanges Empty { get; } = new(null, null, null, null);
}
