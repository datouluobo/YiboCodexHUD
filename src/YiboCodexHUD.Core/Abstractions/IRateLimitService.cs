using YiboCodexHUD.Core.Models;

namespace YiboCodexHUD.Core.Abstractions;

public interface IRateLimitService
{
    Task<UsageSnapshot> GetLatestSnapshotAsync(CancellationToken cancellationToken = default);
}
