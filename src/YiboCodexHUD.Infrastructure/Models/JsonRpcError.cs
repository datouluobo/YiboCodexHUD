namespace YiboCodexHUD.Infrastructure.Models;

internal sealed class JsonRpcError
{
    public int Code { get; init; }

    public string Message { get; init; } = string.Empty;
}
