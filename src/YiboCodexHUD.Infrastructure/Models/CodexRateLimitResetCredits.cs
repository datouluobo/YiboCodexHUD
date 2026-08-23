using System.Text.Json.Serialization;

namespace YiboCodexHUD.Infrastructure.Models;

internal sealed class CodexRateLimitResetCredits
{
    public int? AvailableCount { get; init; }

    [JsonPropertyName("available_count")]
    public int? SnakeCaseAvailableCount { get; init; }

    public IReadOnlyList<CodexRateLimitResetCredit>? Credits { get; init; }

    // Newer Codex clients expose a lightweight summary from the app server and
    // return the detailed credit rows separately. Keep both shapes so the HUD
    // can continue to use the summary count when the details are unavailable.
    public CodexRateLimitResetCredits? Summary { get; init; }

    public CodexRateLimitResetCredits? Details { get; init; }
}
