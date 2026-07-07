using YiboCodexHUD.Core.Abstractions;

namespace YiboCodexHUD.Infrastructure.Services;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
