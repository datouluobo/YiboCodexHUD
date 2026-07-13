using Microsoft.Extensions.Logging;
using YiboCodexHUD.Core.Abstractions;
using YiboCodexHUD.Core.Models;
using YiboCodexHUD.Infrastructure.Models;

namespace YiboCodexHUD.Infrastructure.Services;

public sealed class RateLimitService : IRateLimitService
{
    private const int LongWindowThresholdMinutes = 1440;
    private readonly CodexProtocolClient _protocolClient;
    private readonly RateLimitResetCreditWebService _resetCreditWebService;
    private readonly IClock _clock;
    private readonly ILogger<RateLimitService> _logger;
    private UsageSnapshot? _lastSuccessfulSnapshot;
    private bool _hasLoggedMissingResetCredits;

    public RateLimitService(
        CodexProtocolClient protocolClient,
        RateLimitResetCreditWebService resetCreditWebService,
        IClock clock,
        ILogger<RateLimitService> logger)
    {
        _protocolClient = protocolClient;
        _resetCreditWebService = resetCreditWebService;
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
                    ?? throw new InvalidOperationException("Codex/ChatGPT app-server returned no rate limit snapshot.");
                var resetCredits = rateLimitsResponse.RateLimitResetCredits ?? rateLimitsResponse.SnakeCaseRateLimitResetCredits;
                if (resetCredits is null || (resetCredits.AvailableCount is null && resetCredits.SnakeCaseAvailableCount is null && (resetCredits.Credits is null || resetCredits.Credits.Count == 0)))
                {
                    resetCredits = await _resetCreditWebService.TryFetchAsync(cancellationToken) ?? resetCredits;
                }

                var resetCreditExpirations = GetResetCreditExpirations(resetCredits);
                var resetCreditsAvailable = GetResetCreditsAvailable(resetCredits, resetCreditExpirations);

                if (resetCredits is null && !_hasLoggedMissingResetCredits)
                {
                    _hasLoggedMissingResetCredits = true;
                    _logger.LogInformation(
                        "Codex/ChatGPT rate-limit response did not include reset credit metadata. Top-level fields: {TopLevelFields}. rateLimits fields: {RateLimitFields}.",
                        FormatAvailableFields(rateLimitsResponse.ExtensionData),
                        FormatAvailableFields(snapshot.ExtensionData));
                }

                var normalizedWindows = NormalizeUsageWindows(snapshot.Primary, snapshot.Secondary);

                _lastSuccessfulSnapshot = new UsageSnapshot
                {
                    AccountEmail = null,
                    PlanType = snapshot.PlanType,
                    ShortWindowUsedPercent = normalizedWindows.ShortWindow?.UsedPercent,
                    ShortWindowMinutes = normalizedWindows.ShortWindow?.WindowDurationMins,
                    ShortWindowResetsAt = ToDateTimeOffset(normalizedWindows.ShortWindow?.ResetsAt),
                    LongWindowUsedPercent = normalizedWindows.LongWindow?.UsedPercent,
                    LongWindowMinutes = normalizedWindows.LongWindow?.WindowDurationMins,
                    LongWindowResetsAt = ToDateTimeOffset(normalizedWindows.LongWindow?.ResetsAt),
                    ResetCreditsAvailable = resetCreditsAvailable,
                    ResetCreditExpirations = resetCreditExpirations,
                    FetchedAt = fetchedAt
                };

                if (resetCreditExpirations.Count > 0)
                {
                    _logger.LogInformation(
                        "Resolved {ResetCreditCount} rate-limit reset credits. Expirations: {Expirations}.",
                        resetCreditsAvailable,
                        string.Join(", ", resetCreditExpirations.Select(static value => value.ToLocalTime().ToString("yyyy-MM-dd HH:mm"))));
                }

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

    private static NormalizedUsageWindows NormalizeUsageWindows(
        CodexRateLimitWindow? primary,
        CodexRateLimitWindow? secondary)
    {
        var windows = new[]
            {
                ToUsageWindow(primary),
                ToUsageWindow(secondary)
            }
            .Where(static window => window is not null)
            .Select(static window => window!)
            .OrderBy(static window => window.WindowDurationMins ?? int.MaxValue)
            .ThenBy(static window => window.ResetsAt ?? long.MaxValue)
            .ToArray();

        return windows.Length switch
        {
            0 => new NormalizedUsageWindows(null, null),
            1 when ShouldTreatAsLongWindow(windows[0]) => new NormalizedUsageWindows(null, windows[0]),
            1 => new NormalizedUsageWindows(windows[0], null),
            _ => new NormalizedUsageWindows(windows[0], windows[^1])
        };
    }

    private static UsageWindow? ToUsageWindow(CodexRateLimitWindow? window)
    {
        if (window is null)
        {
            return null;
        }

        var hasUsedPercent = !double.IsNaN(window.UsedPercent) && window.UsedPercent > 0d;
        if (!hasUsedPercent && window.WindowDurationMins is null && window.ResetsAt is null)
        {
            return null;
        }

        return new UsageWindow(window.UsedPercent, window.WindowDurationMins, window.ResetsAt);
    }

    private static bool ShouldTreatAsLongWindow(UsageWindow window) =>
        window.WindowDurationMins.HasValue && window.WindowDurationMins.Value >= LongWindowThresholdMinutes;

    private static int? GetResetCreditsAvailable(
        CodexRateLimitResetCredits? resetCredits,
        IReadOnlyList<DateTimeOffset> resetCreditExpirations)
    {
        if (resetCredits is null)
        {
            return null;
        }

        if (resetCredits.AvailableCount.HasValue)
        {
            return resetCredits.AvailableCount.Value;
        }

        if (resetCredits.SnakeCaseAvailableCount.HasValue)
        {
            return resetCredits.SnakeCaseAvailableCount.Value;
        }

        if (resetCredits.Credits is null || resetCredits.Credits.Count == 0)
        {
            return resetCreditExpirations.Count > 0 ? resetCreditExpirations.Count : null;
        }

        var availableCredits = resetCredits.Credits.Count(static credit =>
            string.IsNullOrWhiteSpace(credit.Status)
            || string.Equals(credit.Status, "available", StringComparison.OrdinalIgnoreCase));

        return availableCredits > 0 ? availableCredits : resetCreditExpirations.Count;
    }

    private static IReadOnlyList<DateTimeOffset> GetResetCreditExpirations(CodexRateLimitResetCredits? resetCredits)
    {
        if (resetCredits?.Credits is null || resetCredits.Credits.Count == 0)
        {
            return Array.Empty<DateTimeOffset>();
        }

        return resetCredits.Credits
            .Select(GetResetCreditExpiration)
            .Where(static value => value.HasValue)
            .Select(static value => value!.Value)
            .Distinct()
            .OrderBy(static value => value)
            .ToArray();
    }

    private static DateTimeOffset? GetResetCreditExpiration(CodexRateLimitResetCredit credit)
    {
        if (TryParseDateTimeOffset(credit.ExpiresAt, out var expiration))
        {
            return expiration;
        }

        if (credit.ExtensionData is null)
        {
            return null;
        }

        foreach (var key in new[] { "expires_at", "expiration", "expiresAt" })
        {
            if (!credit.ExtensionData.TryGetValue(key, out var value))
            {
                continue;
            }

            if (value.ValueKind == System.Text.Json.JsonValueKind.String
                && TryParseDateTimeOffset(value.GetString(), out expiration))
            {
                return expiration;
            }
        }

        return null;
    }

    private static bool TryParseDateTimeOffset(string? rawValue, out DateTimeOffset value)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            value = default;
            return false;
        }

        return DateTimeOffset.TryParse(
            rawValue,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out value);
    }

    private static string FormatAvailableFields(IDictionary<string, System.Text.Json.JsonElement>? extensionData)
    {
        if (extensionData is null || extensionData.Count == 0)
        {
            return "(none)";
        }

        return string.Join(", ", extensionData.Keys.OrderBy(static key => key, StringComparer.Ordinal));
    }

    private sealed record UsageWindow(double UsedPercent, int? WindowDurationMins, long? ResetsAt);

    private sealed record NormalizedUsageWindows(UsageWindow? ShortWindow, UsageWindow? LongWindow);
}
