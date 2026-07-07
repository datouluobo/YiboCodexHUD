namespace YiboCodexHUD.Infrastructure.Models;

internal sealed class CodexRateLimitsResponse
{
    public CodexRateLimitSnapshot? RateLimits { get; init; }

    public CodexRateLimitResetCredits? RateLimitResetCredits { get; init; }
}
