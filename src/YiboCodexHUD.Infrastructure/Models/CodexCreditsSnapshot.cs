namespace YiboCodexHUD.Infrastructure.Models;

internal sealed class CodexCreditsSnapshot
{
    public bool HasCredits { get; init; }

    public bool Unlimited { get; init; }

    public string? Balance { get; init; }
}
