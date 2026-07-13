using Microsoft.Extensions.Logging;
using YiboCodexHUD.Core.Abstractions;
using YiboCodexHUD.Infrastructure.Services;

namespace YiboCodexHUD.Desktop.Services;

public sealed class CodexActivationService
{
    private readonly ICodexWindowTracker _windowTracker;
    private readonly CodexDesktopLauncher _codexDesktopLauncher;
    private readonly ILogger<CodexActivationService> _logger;

    public CodexActivationService(
        ICodexWindowTracker windowTracker,
        CodexDesktopLauncher codexDesktopLauncher,
        ILogger<CodexActivationService> logger)
    {
        _windowTracker = windowTracker;
        _codexDesktopLauncher = codexDesktopLauncher;
        _logger = logger;
    }

    public async Task LaunchOrFocusCodexAsync(CancellationToken cancellationToken = default)
    {
        if (_windowTracker.TryActivateTrackedWindow())
        {
            return;
        }

        try
        {
            await _codexDesktopLauncher.TryLaunchAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to launch Codex/ChatGPT desktop while handling activation command.");
            return;
        }

        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(900), cancellationToken);
            _ = _windowTracker.TryActivateTrackedWindow();
        }
        catch (OperationCanceledException)
        {
        }
    }
}
