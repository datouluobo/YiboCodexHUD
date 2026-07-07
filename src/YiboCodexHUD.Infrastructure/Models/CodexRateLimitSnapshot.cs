namespace YiboCodexHUD.Infrastructure.Models;

internal sealed class CodexRateLimitSnapshot
{
    public string? LimitId { get; init; }

    public string? LimitName { get; init; }

    public CodexRateLimitWindow? Primary { get; init; }

    public CodexRateLimitWindow? Secondary { get; init; }

    public CodexCreditsSnapshot? Credits { get; init; }

    public string? PlanType { get; init; }
}
