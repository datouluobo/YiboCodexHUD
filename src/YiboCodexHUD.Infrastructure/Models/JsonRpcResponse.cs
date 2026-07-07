using System.Text.Json;

namespace YiboCodexHUD.Infrastructure.Models;

internal sealed class JsonRpcResponse
{
    public int? Id { get; init; }

    public JsonElement? Result { get; init; }

    public JsonRpcError? Error { get; init; }

    public string? Method { get; init; }
}
