using YiboCodexHUD.Core.Models;

namespace YiboCodexHUD.Core.Abstractions;

public interface ICodexWindowTracker
{
    TrackedWindow? GetTrackedWindow();
}
