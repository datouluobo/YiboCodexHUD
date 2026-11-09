using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using YiboCodexHUD.Infrastructure.Options;
using YiboCodexHUD.Core.Utilities;

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
                    if (CodexDesktopIdentity.MatchesProcessName(process.ProcessName))
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
            _logger.LogWarning(exception, "Failed to probe existing Codex/ChatGPT processes.");
            return false;
        }
    }

    public Task<bool> TryLaunchAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Codex/ChatGPT desktop activation requires Windows.");
        }

        Exception? lastException = null;

        foreach (var desktopAppUserModelId in CodexDesktopIdentity.GetDesktopAppUserModelIds())
        {
            cancellationToken.ThrowIfCancellationRequested();

            _logger.LogInformation(
                "Launching Codex/ChatGPT desktop via app activation. AppUserModelId: {AppUserModelId}",
                desktopAppUserModelId);

            try
            {
                var manager = (IApplicationActivationManager)new ApplicationActivationManager();
                try
                {
                    Marshal.ThrowExceptionForHR(manager.ActivateApplication(desktopAppUserModelId, null, 0, out _));
                }
                finally
                {
                    Marshal.ReleaseComObject(manager);
                }
                return Task.FromResult(true);
            }
            catch (Exception exception)
            {
                lastException = exception;
                _logger.LogWarning(exception, "Failed to launch Codex/ChatGPT desktop using app activation {AppUserModelId}.", desktopAppUserModelId);
            }
        }

        var launchCandidates = CodexDesktopIdentity.GetLaunchCandidates(_options.ExecutablePath);
        foreach (var executablePath in launchCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = _options.LaunchArguments,
                UseShellExecute = true
            };

            _logger.LogInformation(
                "Launching Codex/ChatGPT desktop via executable fallback. Executable: {ExecutablePath}, Arguments: {Arguments}",
                executablePath,
                _options.LaunchArguments);

            try
            {
                var process = Process.Start(startInfo);
                if (process is null)
                {
                    continue;
                }

                process.Dispose();
                return Task.FromResult(true);
            }
            catch (Exception exception)
            {
                lastException = exception;
                _logger.LogWarning(exception, "Failed to launch Codex/ChatGPT desktop using executable fallback {ExecutablePath}.", executablePath);
            }
        }

        throw new InvalidOperationException("Failed to launch Codex/ChatGPT desktop.", lastException);
    }

    [ComImport]
    [Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    private class ApplicationActivationManager { }

    [ComImport]
    [Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string? arguments,
            uint options,
            out uint processId);
    }
}
