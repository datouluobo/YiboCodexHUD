using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using YiboCodexHUD.Infrastructure.Models;
using YiboCodexHUD.Infrastructure.Options;

namespace YiboCodexHUD.Infrastructure.Services;

public sealed class CodexProtocolClient : IDisposable
{
    private readonly CodexAppServerProcess _appServerProcess;
    private readonly ILogger<CodexProtocolClient> _logger;
    private readonly TimeSpan _initializationTimeout;
    private readonly JsonSerializerOptions _serializerOptions;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private StreamWriter? _writer;
    private StreamReader? _reader;
    private bool _initialized;
    private int _nextRequestId = 1;

    public CodexProtocolClient(
        CodexAppServerProcess appServerProcess,
        IOptions<CodexAppServerOptions> options,
        ILogger<CodexProtocolClient> logger)
    {
        _appServerProcess = appServerProcess;
        _initializationTimeout = TimeSpan.FromSeconds(options.Value.InitializationTimeoutSeconds);
        _logger = logger;
        _serializerOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            var process = await _appServerProcess.EnsureStartedAsync(cancellationToken);
            _writer = process.StandardInput;
            _reader = process.StandardOutput;

            _ = Task.Run(() => DrainStandardErrorAsync(process.StandardError), CancellationToken.None);

            var initializeResult = await SendRequestInternalAsync<JsonElement>(
                "initialize",
                new
                {
                    protocolVersion = "2025-06-18",
                    clientInfo = new
                    {
                        name = "YiboCodexHUD",
                        version = "0.1.0"
                    },
                    capabilities = new { }
                },
                _initializationTimeout,
                cancellationToken);

            _logger.LogInformation("Codex/ChatGPT app-server initialized: {InitializeResult}", initializeResult.ToString());

            await SendNotificationInternalAsync("initialized", new { }, cancellationToken);
            _initialized = true;
        }
        catch
        {
            ResetTransport();
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<T?> SendRequestAsync<T>(string method, object? @params = null, CancellationToken cancellationToken = default)
    {
        try
        {
            await InitializeAsync(cancellationToken);
            await _gate.WaitAsync(cancellationToken);
            try
            {
                return await SendRequestInternalAsync<T>(method, @params, _initializationTimeout, cancellationToken);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch
        {
            ResetTransport();
            throw;
        }
    }

    private async Task<T?> SendRequestInternalAsync<T>(
        string method,
        object? @params,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        EnsureTransportReady();

        var requestId = _nextRequestId++;
        var request = new JsonRpcRequest
        {
            Id = requestId,
            Method = method,
            Params = @params,
            Jsonrpc = "2.0"
        };

        var json = JsonSerializer.Serialize(request, _serializerOptions);
        await _writer!.WriteLineAsync(json);
        await _writer.FlushAsync();

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        while (true)
        {
            var line = await ReadLineWithTimeoutAsync(timeout, linkedCts.Token);
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var response = JsonSerializer.Deserialize<JsonRpcResponse>(line, _serializerOptions)
                ?? throw new InvalidOperationException("Failed to deserialize app-server response.");

            if (response.Method is not null)
            {
                _logger.LogDebug("Ignoring app-server notification: {Notification}", line);
                continue;
            }

            if (response.Id != requestId)
            {
                _logger.LogDebug("Ignoring out-of-band response: {Response}", line);
                continue;
            }

            if (response.Error is not null)
            {
                throw new InvalidOperationException($"Codex/ChatGPT app-server error {response.Error.Code}: {response.Error.Message}");
            }

            if (response.Result is null)
            {
                return default;
            }

            return response.Result.Value.Deserialize<T>(_serializerOptions);
        }
    }

    private async Task SendNotificationInternalAsync(string method, object? @params, CancellationToken cancellationToken)
    {
        EnsureTransportReady();

        var notification = new JsonRpcRequest
        {
            Method = method,
            Params = @params,
            Jsonrpc = "2.0"
        };

        var json = JsonSerializer.Serialize(notification, _serializerOptions);
        await _writer!.WriteLineAsync(json.AsMemory(), cancellationToken);
        await _writer.FlushAsync();
    }

    private async Task<string?> ReadLineWithTimeoutAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        EnsureTransportReady();

        var readTask = Task.Run(() => _reader!.ReadLine(), CancellationToken.None);
        var timeoutTask = Task.Delay(timeout);
        var cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        var completedTask = await Task.WhenAny(readTask, timeoutTask, cancellationTask);

        if (completedTask == readTask)
        {
            return await readTask;
        }

        if (completedTask == cancellationTask)
        {
            throw new OperationCanceledException("App-server read was canceled.", cancellationToken);
        }

        throw new TimeoutException($"Timed out waiting for app-server output after {timeout.TotalSeconds:0} seconds.");
    }

    private void EnsureTransportReady()
    {
        if (_writer is null || _reader is null)
        {
            throw new InvalidOperationException("Codex/ChatGPT app-server transport is not ready.");
        }
    }

    private async Task DrainStandardErrorAsync(StreamReader errorReader)
    {
        while (true)
        {
            var line = await errorReader.ReadLineAsync();
            if (line is null)
            {
                return;
            }

            _logger.LogDebug("Codex/ChatGPT app-server stderr: {Line}", line);
        }
    }

    private void ResetTransport()
    {
        _logger.LogWarning("Resetting Codex/ChatGPT app-server transport after a failed request.");

        _writer = null;
        _reader = null;
        _initialized = false;
        _nextRequestId = 1;
        _appServerProcess.Stop();
    }

    public void Dispose()
    {
        _gate.Dispose();
        _appServerProcess.Stop();
    }
}
