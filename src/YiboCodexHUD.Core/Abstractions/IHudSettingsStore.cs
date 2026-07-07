using YiboCodexHUD.Core.Models;

namespace YiboCodexHUD.Core.Abstractions;

public interface IHudSettingsStore
{
    Task<HudSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(HudSettings settings, CancellationToken cancellationToken = default);
}
