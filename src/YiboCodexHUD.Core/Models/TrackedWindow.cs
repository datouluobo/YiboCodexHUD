namespace YiboCodexHUD.Core.Models;

public sealed record TrackedWindow(
    nint Handle,
    string Title,
    WindowBounds Bounds,
    WindowBounds ClientBounds,
    WindowBounds MonitorBounds,
    WindowBounds TitleBarSafeBounds,
    bool IsForeground,
    bool IsTopVisible,
    bool IsMaximized);
