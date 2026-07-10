using System.Text.Json.Serialization;

namespace YiboCodexHUD.Infrastructure.Models;

internal sealed class CodexRateLimitResetCredits
{
    public int? AvailableCount { get; init; }

    [JsonPropertyName("available_count")]
    public int? SnakeCaseAvailableCount { get; init; }

    public IReadOnlyList<CodexRateLimitResetCredit>? Credits { get; init; }
}
