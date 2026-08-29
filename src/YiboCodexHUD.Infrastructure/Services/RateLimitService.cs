using System.Text.Json;
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
                if (!HasCreditRows(resetCredits))
                {
                    resetCredits = PreferDetailedResetCredits(
                        resetCredits,
                        await _resetCreditWebService.TryFetchAsync(cancellationToken));
                }

                var resetCreditExpirations = GetResetCreditExpirations(resetCredits, fetchedAt);
                var resetCreditsAvailable = GetResetCreditsAvailable(resetCredits, resetCreditExpirations, fetchedAt);

                if (resetCredits is null && !_hasLoggedMissingResetCredits)
                {
                    _hasLoggedMissingResetCredits = true;
                    _logger.LogInformation(
                        "Codex/ChatGPT rate-limit response did not include reset credit metadata. Top-level fields: {TopLevelFields}. rateLimits fields: {RateLimitFields}.",
                        FormatAvailableFields(rateLimitsResponse.ExtensionData),
                        FormatAvailableFields(snapshot.ExtensionData));
                }

                var normalizedWindows = NormalizeUsageWindows(snapshot.Primary, snapshot.Secondary);
                var shortWindowResetsAt = ToDateTimeOffset(normalizedWindows.ShortWindow?.ResetsAt);
                var longWindowResetsAt = ToDateTimeOffset(normalizedWindows.LongWindow?.ResetsAt);
                var tokenUsage = await ReadOfficialTokenUsageAsync(fetchedAt, cancellationToken);

                _lastSuccessfulSnapshot = new UsageSnapshot
                {
                    AccountEmail = null,
                    PlanType = snapshot.PlanType,
                    ShortWindowUsedPercent = normalizedWindows.ShortWindow?.UsedPercent,
                    ShortWindowMinutes = normalizedWindows.ShortWindow?.WindowDurationMins,
                    ShortWindowResetsAt = shortWindowResetsAt,
                    LongWindowUsedPercent = normalizedWindows.LongWindow?.UsedPercent,
                    LongWindowMinutes = normalizedWindows.LongWindow?.WindowDurationMins,
                    LongWindowResetsAt = longWindowResetsAt,
                    ResetCreditsAvailable = resetCreditsAvailable,
                    ResetCreditExpirations = resetCreditExpirations,
                    CurrentTokenUsage = tokenUsage.Current,
                    TodayTokenUsage = tokenUsage.Today,
                    CurrentPeriodTokenUsage = tokenUsage.CurrentPeriod,
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

    private async Task<TokenUsageRanges> ReadOfficialTokenUsageAsync(
        DateTimeOffset fetchedAt,
        CancellationToken cancellationToken)
    {
        var usageResponse = await _protocolClient.SendRequestAsync<JsonElement>(
            "account/usage/read",
            null,
            cancellationToken);

        if (usageResponse.ValueKind != JsonValueKind.Object
            || !usageResponse.TryGetProperty("dailyUsageBuckets", out var buckets)
            || buckets.ValueKind != JsonValueKind.Array)
        {
            _logger.LogInformation("Codex/ChatGPT did not return daily token usage buckets. The token usage HUD item will be hidden.");
            return TokenUsageRanges.Empty;
        }

        var today = fetchedAt.ToLocalTime().Date;
        var weekStart = today.AddDays(-6);
        long todayTokens = 0;
        long weekTokens = 0;
        var hasTodayTokens = false;
        var hasWeekTokens = false;

        foreach (var bucket in buckets.EnumerateArray())
        {
            if (!TryReadUsageBucket(bucket, out var date, out var tokens))
            {
                continue;
            }

            if (date == today)
            {
                todayTokens += tokens;
                hasTodayTokens = true;
            }

            if (date >= weekStart && date <= today)
            {
                weekTokens += tokens;
                hasWeekTokens = true;
            }
        }

        return new TokenUsageRanges(
            Current: null,
            Today: hasTodayTokens ? new TokenUsageRangeSnapshot { TotalTokens = todayTokens } : null,
            CurrentPeriod: hasWeekTokens ? new TokenUsageRangeSnapshot { TotalTokens = weekTokens } : null,
            Cumulative: null);
    }

    private static bool TryReadUsageBucket(JsonElement bucket, out DateTime date, out long tokens)
    {
        date = default;
        tokens = 0;

        return bucket.ValueKind == JsonValueKind.Object
            && bucket.TryGetProperty("startDate", out var startDate)
            && startDate.ValueKind == JsonValueKind.String
            && DateTime.TryParseExact(
                startDate.GetString(),
                "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out date)
            && bucket.TryGetProperty("tokens", out var tokenValue)
            && TryReadInt64(tokenValue, out tokens)
            && tokens >= 0;
    }

    private static bool TryReadInt64(JsonElement element, out long value)
    {
        if (element.ValueKind == JsonValueKind.Number)
        {
            return element.TryGetInt64(out value);
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            return long.TryParse(element.GetString(), out value);
        }

        value = 0;
        return false;
    }

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
        IReadOnlyList<DateTimeOffset> resetCreditExpirations,
        DateTimeOffset fetchedAt)
    {
        if (resetCredits is null)
        {
            return null;
        }

        var reportedAvailableCount = GetReportedAvailableCount(resetCredits);
        var creditRows = GetCreditRows(resetCredits);
        if (creditRows.Count == 0)
        {
            return reportedAvailableCount ?? (resetCreditExpirations.Count > 0 ? resetCreditExpirations.Count : null);
        }

        var availableCredits = creditRows.Count(credit =>
            IsAvailableResetCredit(credit)
            && !IsExpiredResetCredit(credit, fetchedAt));

        // A detailed credit list is authoritative. Do not fall back to a stale
        // aggregate count when every listed credit has already expired.
        if (availableCredits == 0)
        {
            return null;
        }

        var listAvailableCount = availableCredits;

        return listAvailableCount;
    }

    private static bool HasCreditRows(CodexRateLimitResetCredits? resetCredits) =>
        GetCreditRows(resetCredits).Count > 0;

    private static CodexRateLimitResetCredits? PreferDetailedResetCredits(
        CodexRateLimitResetCredits? primary,
        CodexRateLimitResetCredits? fallback)
    {
        // A live zero count means the credit was used or has expired. It must not
        // be replaced by an older detailed response from the browser cache.
        if (HasReportedNoResetCredits(primary))
        {
            return primary;
        }

        if (fallback is null)
        {
            return primary;
        }

        if (!HasCreditRows(primary) && HasCreditRows(fallback))
        {
            return fallback;
        }

        return primary ?? fallback;
    }

    private static bool HasReportedNoResetCredits(CodexRateLimitResetCredits? resetCredits) =>
        GetReportedAvailableCount(resetCredits) == 0;

    private static IReadOnlyList<DateTimeOffset> GetResetCreditExpirations(
        CodexRateLimitResetCredits? resetCredits,
        DateTimeOffset fetchedAt)
    {
        var creditRows = GetCreditRows(resetCredits);
        if (creditRows.Count == 0)
        {
            return Array.Empty<DateTimeOffset>();
        }

        return creditRows
            .Select(GetResetCreditExpiration)
            .Where(static value => value.HasValue)
            .Select(static value => value!.Value)
            .Where(expiration => expiration > fetchedAt)
            .Distinct()
            .OrderBy(static value => value)
            .ToArray();
    }

    private static int? GetReportedAvailableCount(CodexRateLimitResetCredits? resetCredits)
    {
        if (resetCredits is null)
        {
            return null;
        }

        return resetCredits.AvailableCount
            ?? resetCredits.SnakeCaseAvailableCount
            ?? GetReportedAvailableCount(resetCredits.Summary)
            ?? GetReportedAvailableCount(resetCredits.Details);
    }

    private static IReadOnlyList<CodexRateLimitResetCredit> GetCreditRows(CodexRateLimitResetCredits? resetCredits)
    {
        if (resetCredits is null)
        {
            return Array.Empty<CodexRateLimitResetCredit>();
        }

        if (resetCredits.Credits is { Count: > 0 } credits)
        {
            return credits;
        }

        var detailedRows = GetCreditRows(resetCredits.Details);
        return detailedRows.Count > 0
            ? detailedRows
            : GetCreditRows(resetCredits.Summary);
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

            if (TryParseDateTimeOffset(value, out expiration))
            {
                return expiration;
            }
        }

        return null;
    }

    private static bool IsAvailableResetCredit(CodexRateLimitResetCredit credit) =>
        string.IsNullOrWhiteSpace(credit.Status)
        || string.Equals(credit.Status, "available", StringComparison.OrdinalIgnoreCase);

    private static bool IsExpiredResetCredit(CodexRateLimitResetCredit credit, DateTimeOffset fetchedAt) =>
        GetResetCreditExpiration(credit) is { } expiration && expiration <= fetchedAt;

    private static bool TryParseDateTimeOffset(JsonElement rawValue, out DateTimeOffset value)
    {
        if (rawValue.ValueKind == JsonValueKind.Number
            && rawValue.TryGetInt64(out var unixSeconds))
        {
            try
            {
                value = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                value = default;
                return false;
            }
        }

        if (rawValue.ValueKind != JsonValueKind.String)
        {
            value = default;
            return false;
        }

        var textValue = rawValue.GetString();
        if (long.TryParse(textValue, out unixSeconds))
        {
            try
            {
                value = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                value = default;
                return false;
            }
        }

        return DateTimeOffset.TryParse(
            textValue,
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
