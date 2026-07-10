using System.Text.Json;
using System.Text.Json.Serialization;

namespace YiboCodexHUD.Infrastructure.Models;

internal sealed class CodexRateLimitsResponse
{
    public CodexRateLimitSnapshot? RateLimits { get; init; }

    public CodexRateLimitResetCredits? RateLimitResetCredits { get; init; }

    [JsonPropertyName("rate_limit_reset_credits")]
    public CodexRateLimitResetCredits? SnakeCaseRateLimitResetCredits { get; init; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; init; }
}
