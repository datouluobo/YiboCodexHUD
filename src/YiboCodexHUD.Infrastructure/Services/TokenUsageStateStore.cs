using System.Text.Json;
using Microsoft.Extensions.Logging;
using YiboCodexHUD.Infrastructure.Models;

namespace YiboCodexHUD.Infrastructure.Services;

public sealed class TokenUsageStateStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ILogger<TokenUsageStateStore> _logger;
    private readonly string _stateFilePath;

    public TokenUsageStateStore(ILogger<TokenUsageStateStore> logger)
    {
        _logger = logger;

        var appDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "YiboCodexHUD");

        _stateFilePath = Path.Combine(appDataDirectory, "token-usage-state.json");
    }

    public async Task<TokenUsageState> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_stateFilePath))
        {
            return new TokenUsageState();
        }

        try
        {
            await using var stream = File.OpenRead(_stateFilePath);
            return await JsonSerializer.DeserializeAsync<TokenUsageState>(stream, SerializerOptions, cancellationToken)
                ?? new TokenUsageState();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to load token usage state from {StateFilePath}. Falling back to an empty state.", _stateFilePath);
            return new TokenUsageState();
        }
    }

    public async Task SaveAsync(TokenUsageState state, CancellationToken cancellationToken = default)
    {
        var directoryPath = Path.GetDirectoryName(_stateFilePath)
            ?? throw new InvalidOperationException("Failed to resolve the token usage state directory.");

        Directory.CreateDirectory(directoryPath);

        await using var stream = File.Create(_stateFilePath);
        await JsonSerializer.SerializeAsync(stream, state, SerializerOptions, cancellationToken);
    }
}
