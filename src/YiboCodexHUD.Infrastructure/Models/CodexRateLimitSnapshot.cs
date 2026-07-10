using System.Text.Json;
using System.Text.Json.Serialization;

namespace YiboCodexHUD.Infrastructure.Models;

internal sealed class CodexRateLimitSnapshot
{
    public string? LimitId { get; init; }

    public string? LimitName { get; init; }

    public CodexRateLimitWindow? Primary { get; init; }

    public CodexRateLimitWindow? Secondary { get; init; }

    public CodexCreditsSnapshot? Credits { get; init; }

    public string? PlanType { get; init; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; init; }
}
