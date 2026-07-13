using System.IO;
using System.IO.Pipes;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace YiboCodexHUD.Desktop.Services;

public sealed class SingleInstanceCommandServer : BackgroundService
{
    private readonly CodexActivationService _codexActivationService;
    private readonly ILogger<SingleInstanceCommandServer> _logger;

    public SingleInstanceCommandServer(
        CodexActivationService codexActivationService,
        ILogger<SingleInstanceCommandServer> logger)
    {
        _codexActivationService = codexActivationService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    SingleInstanceCommandProtocol.PipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(stoppingToken);
                using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                var command = await reader.ReadLineAsync(stoppingToken);
                await HandleCommandAsync(command, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed to handle single-instance command.");
            }
        }
    }

    private Task HandleCommandAsync(string? command, CancellationToken cancellationToken)
    {
        if (string.Equals(command, SingleInstanceCommandProtocol.LaunchOrFocusCodexCommand, StringComparison.Ordinal))
        {
            return _codexActivationService.LaunchOrFocusCodexAsync(cancellationToken);
        }

        _logger.LogDebug("Ignored unknown single-instance command: {Command}", command);
        return Task.CompletedTask;
    }
}
