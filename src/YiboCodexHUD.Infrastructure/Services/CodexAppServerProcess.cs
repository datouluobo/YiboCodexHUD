using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using YiboCodexHUD.Infrastructure.Options;
using YiboCodexHUD.Core.Utilities;

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

        if (!CodexDesktopIdentity.TryResolveExecutablePath(_options.ExecutablePath, out var executablePath))
        {
            throw new FileNotFoundException("Unable to locate a Codex/ChatGPT app-server executable.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = _options.Arguments,
            WorkingDirectory = ResolveWorkingDirectory(),
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
            "Starting Codex/ChatGPT app-server. Executable: {ExecutablePath}, Arguments: {Arguments}",
            executablePath,
            _options.Arguments);

        _process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start Codex/ChatGPT app-server process.");

        return Task.FromResult(_process);
    }

    private static string ResolveWorkingDirectory()
    {
        var userProfileDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var codexHomeDirectory = Path.Combine(userProfileDirectory, ".codex");

        // The Codex app-server resolves the signed-in desktop context from this
        // directory. Starting it from the HUD install directory can cause the
        // rate-limit endpoint to reject the request.
        return Directory.Exists(codexHomeDirectory)
            ? codexHomeDirectory
            : userProfileDirectory;
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
            _logger.LogWarning(exception, "Failed to stop Codex/ChatGPT app-server process cleanly.");
        }
        finally
        {
            _process.Dispose();
            _process = null;
        }
    }
}
