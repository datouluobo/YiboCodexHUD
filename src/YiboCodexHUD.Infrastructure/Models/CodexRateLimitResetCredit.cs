using System.Text.Json;
using System.Text.Json.Serialization;

namespace YiboCodexHUD.Infrastructure.Models;

internal sealed class CodexRateLimitResetCredit
{
    public string? Id { get; init; }

    public string? Status { get; init; }

    public string? ExpiresAt { get; init; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; init; }
}
