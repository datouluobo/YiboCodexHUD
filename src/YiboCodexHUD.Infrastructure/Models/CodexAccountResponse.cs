namespace YiboCodexHUD.Infrastructure.Models;

internal sealed class CodexAccountResponse
{
    public CodexAccount? Account { get; init; }

    public bool RequiresOpenaiAuth { get; init; }
}
