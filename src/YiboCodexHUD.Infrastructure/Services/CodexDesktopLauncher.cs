using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using YiboCodexHUD.Infrastructure.Options;

namespace YiboCodexHUD.Infrastructure.Services;

public sealed class CodexDesktopLauncher
{
    private readonly CodexAppServerOptions _options;
    private readonly ILogger<CodexDesktopLauncher> _logger;

    public CodexDesktopLauncher(
        IOptions<CodexAppServerOptions> options,
        ILogger<CodexDesktopLauncher> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public bool IsCodexProcessRunning()
    {
        try
        {
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    if (process.ProcessName.Contains("codex", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch
                {
                }
                finally
                {
                    process.Dispose();
                }
            }

            return false;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to probe existing Codex processes.");
            return false;
        }
    }

    public Task<bool> TryLaunchAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (IsCodexProcessRunning())
        {
            _logger.LogInformation("Skipping Codex desktop launch because an existing Codex process is already running.");
            return Task.FromResult(false);
        }

        var executablePath = Environment.ExpandEnvironmentVariables(_options.ExecutablePath);
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = _options.LaunchArguments,
            UseShellExecute = true
        };

        _logger.LogInformation(
            "Launching Codex desktop. Executable: {ExecutablePath}, Arguments: {Arguments}",
            executablePath,
            _options.LaunchArguments);

        var process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException("Failed to launch Codex desktop.");
        }

        process.Dispose();

        return Task.FromResult(true);
    }
}
