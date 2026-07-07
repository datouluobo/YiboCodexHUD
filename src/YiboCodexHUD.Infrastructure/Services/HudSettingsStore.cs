using System.Text.Json;
using Microsoft.Extensions.Logging;
using YiboCodexHUD.Core.Abstractions;
using YiboCodexHUD.Core.Models;

namespace YiboCodexHUD.Infrastructure.Services;

public sealed class HudSettingsStore : IHudSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ILogger<HudSettingsStore> _logger;
    private readonly string _settingsFilePath;

    public HudSettingsStore(ILogger<HudSettingsStore> logger)
    {
        _logger = logger;

        var appDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "YiboCodexHUD");

        _settingsFilePath = Path.Combine(appDataDirectory, "hudsettings.json");
    }

    public async Task<HudSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_settingsFilePath))
        {
            return new HudSettings();
        }

        try
        {
            await using var stream = File.OpenRead(_settingsFilePath);
            var settings = await JsonSerializer.DeserializeAsync<HudSettings>(stream, SerializerOptions, cancellationToken);
            return settings ?? new HudSettings();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to load HUD settings from {SettingsFilePath}. Falling back to defaults.", _settingsFilePath);
            return new HudSettings();
        }
    }

    public async Task SaveAsync(HudSettings settings, CancellationToken cancellationToken = default)
    {
        var directoryPath = Path.GetDirectoryName(_settingsFilePath)
            ?? throw new InvalidOperationException("Failed to resolve the HUD settings directory.");

        Directory.CreateDirectory(directoryPath);

        await using var stream = File.Create(_settingsFilePath);
        await JsonSerializer.SerializeAsync(stream, settings, SerializerOptions, cancellationToken);
    }
}
