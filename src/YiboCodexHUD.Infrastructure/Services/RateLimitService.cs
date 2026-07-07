using Microsoft.Extensions.Logging;
using YiboCodexHUD.Core.Abstractions;
using YiboCodexHUD.Core.Models;
using YiboCodexHUD.Infrastructure.Models;

namespace YiboCodexHUD.Infrastructure.Services;

public sealed class RateLimitService : IRateLimitService
{
    private readonly CodexProtocolClient _protocolClient;
    private readonly IClock _clock;
    private readonly ILogger<RateLimitService> _logger;
    private UsageSnapshot? _lastSuccessfulSnapshot;

    public RateLimitService(
        CodexProtocolClient protocolClient,
        IClock clock,
        ILogger<RateLimitService> logger)
    {
        _protocolClient = protocolClient;
        _clock = clock;
        _logger = logger;
    }

    public async Task<UsageSnapshot> GetLatestSnapshotAsync(CancellationToken cancellationToken = default)
    {
        Exception? lastError = null;

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                var fetchedAt = _clock.UtcNow;
                var rateLimitsResponse = await _protocolClient.SendRequestAsync<CodexRateLimitsResponse>(
                    "account/rateLimits/read",
                    null,
                    cancellationToken);

                var snapshot = rateLimitsResponse?.RateLimits
                    ?? throw new InvalidOperationException("Codex app-server returned no rate limit snapshot.");

                _lastSuccessfulSnapshot = new UsageSnapshot
                {
                    AccountEmail = null,
                    PlanType = snapshot.PlanType,
                    ShortWindowUsedPercent = snapshot.Primary?.UsedPercent,
                    ShortWindowMinutes = snapshot.Primary?.WindowDurationMins,
                    ShortWindowResetsAt = ToDateTimeOffset(snapshot.Primary?.ResetsAt),
                    LongWindowUsedPercent = snapshot.Secondary?.UsedPercent,
                    LongWindowMinutes = snapshot.Secondary?.WindowDurationMins,
                    LongWindowResetsAt = ToDateTimeOffset(snapshot.Secondary?.ResetsAt),
                    ResetCreditsAvailable = rateLimitsResponse.RateLimitResetCredits?.AvailableCount,
                    FetchedAt = fetchedAt
                };

                _logger.LogInformation("Fetched live rate-limit snapshot at {FetchedAt} on attempt {Attempt}.", fetchedAt, attempt);
                return _lastSuccessfulSnapshot;
            }
            catch (Exception exception) when (attempt < 3)
            {
                lastError = exception;
                _logger.LogWarning(exception, "Attempt {Attempt} to fetch rate limits failed. Retrying...", attempt);
                await Task.Delay(TimeSpan.FromMilliseconds(700 * attempt), cancellationToken);
            }
            catch (Exception exception)
            {
                lastError = exception;
                break;
            }
        }

        if (_lastSuccessfulSnapshot is not null)
        {
            _logger.LogWarning(lastError, "Returning last successful rate-limit snapshot because the latest fetch failed.");
            return _lastSuccessfulSnapshot;
        }

        throw lastError ?? new InvalidOperationException("Failed to fetch rate limits.");
    }

    private static DateTimeOffset? ToDateTimeOffset(long? unixSeconds) =>
        unixSeconds is null ? null : DateTimeOffset.FromUnixTimeSeconds(unixSeconds.Value);
}
