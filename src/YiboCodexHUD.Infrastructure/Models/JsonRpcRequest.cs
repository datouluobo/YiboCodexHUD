namespace YiboCodexHUD.Infrastructure.Models;

internal sealed class JsonRpcRequest
{
    public string Jsonrpc { get; init; } = "2.0";

    public int? Id { get; init; }

    public required string Method { get; init; }

    public object? Params { get; init; }
}
