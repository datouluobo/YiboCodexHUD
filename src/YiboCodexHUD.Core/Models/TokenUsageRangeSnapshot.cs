namespace YiboCodexHUD.Core.Models;

public sealed record TokenUsageRangeSnapshot
{
    public long? InputTokens { get; init; }

    public long? OutputTokens { get; init; }

    public long? TotalTokens { get; init; }

    public bool HasAnyTokens => InputTokens.HasValue || OutputTokens.HasValue || TotalTokens.HasValue;

    public long? EffectiveTotalTokens => TotalTokens ?? AddNullable(InputTokens, OutputTokens);

    private static long? AddNullable(long? left, long? right)
    {
        if (!left.HasValue && !right.HasValue)
        {
            return null;
        }

        return Math.Max(0, left ?? 0) + Math.Max(0, right ?? 0);
    }
}
