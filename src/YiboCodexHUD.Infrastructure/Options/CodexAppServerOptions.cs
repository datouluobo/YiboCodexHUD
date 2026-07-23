namespace YiboCodexHUD.Infrastructure.Options;

public sealed class CodexAppServerOptions
{
    public const string SectionName = "CodexAppServer";

    public string ExecutablePath { get; set; } = "codex.exe";

    public string Arguments { get; set; } = "app-server";

    public string LaunchArguments { get; set; } = string.Empty;

    public int InitializationTimeoutSeconds { get; set; } = 10;
}
