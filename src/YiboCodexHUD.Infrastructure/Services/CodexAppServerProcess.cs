using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using YiboCodexHUD.Infrastructure.Options;

namespace YiboCodexHUD.Infrastructure.Services;

public sealed class CodexAppServerProcess
{
    private readonly CodexAppServerOptions _options;
    private readonly ILogger<CodexAppServerProcess> _logger;
    private Process? _process;

    public CodexAppServerProcess(
        IOptions<CodexAppServerOptions> options,
        ILogger<CodexAppServerProcess> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public Task<Process> EnsureStartedAsync(CancellationToken cancellationToken = default)
    {
        if (_process is { HasExited: false })
        {
            return Task.FromResult(_process);
        }

        var executablePath = Environment.ExpandEnvironmentVariables(_options.ExecutablePath);
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = _options.Arguments,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            UseShellExecute = false,
            CreateNoWindow = true
        };

        _logger.LogInformation(
            "Starting Codex app-server. Executable: {ExecutablePath}, Arguments: {Arguments}",
            executablePath,
            _options.Arguments);

        _process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start Codex app-server process.");

        return Task.FromResult(_process);
    }

    public void Stop()
    {
        if (_process is null)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(2000);
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to stop Codex app-server process cleanly.");
        }
        finally
        {
            _process.Dispose();
            _process = null;
        }
    }
}
