namespace YiboCodexHUD.Desktop.Services;

public static class SingleInstanceCommandProtocol
{
    public const string MutexName = @"Local\YiboCodexHUD.Desktop.Singleton";
    public const string PipeName = "YiboCodexHUD.Desktop.Singleton.Command";
    public const string LaunchOrFocusCodexCommand = "launch-or-focus-codex";
}
