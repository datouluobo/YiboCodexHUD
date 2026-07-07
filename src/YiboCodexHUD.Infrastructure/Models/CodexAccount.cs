namespace YiboCodexHUD.Infrastructure.Models;

internal sealed class CodexAccount
{
    public string Type { get; init; } = string.Empty;

    public string? Email { get; init; }

    public string? PlanType { get; init; }
}
