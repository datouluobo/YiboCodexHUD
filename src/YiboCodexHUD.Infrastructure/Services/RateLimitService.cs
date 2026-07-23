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
    private readonly TokenUsageStateStore _tokenUsageStateStore;
    private readonly CodexTokenUsageLogReader _tokenUsageLogReader;
    private readonly IClock _clock;
    private readonly ILogger<RateLimitService> _logger;
    private UsageSnapshot? _lastSuccessfulSnapshot;
    private bool _hasLoggedMissingResetCredits;

    public RateLimitService(
        CodexProtocolClient protocolClient,
        RateLimitResetCreditWebService resetCreditWebService,
        TokenUsageStateStore tokenUsageStateStore,
        CodexTokenUsageLogReader tokenUsageLogReader,
        IClock clock,
        ILogger<RateLimitService> logger)
    {
        _protocolClient = protocolClient;
        _resetCreditWebService = resetCreditWebService;
        _tokenUsageStateStore = tokenUsageStateStore;
        _tokenUsageLogReader = tokenUsageLogReader;
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
                var shortWindowResetsAt = ToDateTimeOffset(normalizedWindows.ShortWindow?.ResetsAt);
                var longWindowResetsAt = ToDateTimeOffset(normalizedWindows.LongWindow?.ResetsAt);
                var tokenUsage = await TryReadTokenUsageAsync(
                    shortWindowResetsAt,
                    normalizedWindows.ShortWindow?.WindowDurationMins,
                    longWindowResetsAt,
                    normalizedWindows.LongWindow?.WindowDurationMins,
                    resetCreditsAvailable,
                    fetchedAt,
                    cancellationToken);

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

    private async Task<TokenUsageRanges> TryReadTokenUsageAsync(
        DateTimeOffset? shortWindowResetsAt,
        int? shortWindowMinutes,
        DateTimeOffset? longWindowResetsAt,
        int? longWindowMinutes,
        int? resetCreditsAvailable,
        DateTimeOffset fetchedAt,
        CancellationToken cancellationToken)
    {
        try
        {
            var currentPeriodStartedAt = await ResolveCurrentPeriodStartedAtAsync(
                shortWindowResetsAt,
                shortWindowMinutes,
                longWindowResetsAt,
                longWindowMinutes,
                resetCreditsAvailable,
                fetchedAt,
                cancellationToken);

            var logTokenUsage = await _tokenUsageLogReader.TryReadAsync(currentPeriodStartedAt, fetchedAt, cancellationToken);
            if (logTokenUsage.Current?.HasAnyTokens == true
                || logTokenUsage.Today?.HasAnyTokens == true
                || logTokenUsage.CurrentPeriod?.HasAnyTokens == true)
            {
                return logTokenUsage;
            }

            var usageResponse = await _protocolClient.SendRequestAsync<JsonElement>(
                "account/usage/read",
                null,
                cancellationToken);

            var parsed = ParseTokenUsageRanges(usageResponse);
            var currentPeriod = await ResolveCurrentPeriodTokenUsageAsync(
                parsed,
                shortWindowResetsAt,
                longWindowResetsAt,
                resetCreditsAvailable,
                fetchedAt,
                cancellationToken);

            return parsed with { CurrentPeriod = currentPeriod };
        }
        catch (Exception exception)
        {
            _logger.LogInformation(exception, "Token usage data is unavailable. The token usage HUD item will be hidden.");
            return TokenUsageRanges.Empty;
        }
    }

    private async Task<DateTimeOffset?> ResolveCurrentPeriodStartedAtAsync(
        DateTimeOffset? shortWindowResetsAt,
        int? shortWindowMinutes,
        DateTimeOffset? longWindowResetsAt,
        int? longWindowMinutes,
        int? resetCreditsAvailable,
        DateTimeOffset fetchedAt,
        CancellationToken cancellationToken)
    {
        var state = await _tokenUsageStateStore.LoadAsync(cancellationToken);
        var inferredStartedAt = InferCurrentPeriodStartedAt(
            shortWindowResetsAt,
            shortWindowMinutes,
            longWindowResetsAt,
            longWindowMinutes);
        var periodChanged = HasPeriodBoundaryChanged(state, shortWindowResetsAt, longWindowResetsAt, resetCreditsAvailable);
        var startedAt = periodChanged || state.CurrentPeriodStartedAt is null
            ? inferredStartedAt ?? fetchedAt
            : state.CurrentPeriodStartedAt;

        await _tokenUsageStateStore.SaveAsync(
            state with
            {
                CurrentPeriodStartedAt = startedAt,
                LastShortWindowResetsAt = shortWindowResetsAt,
                LastLongWindowResetsAt = longWindowResetsAt,
                LastResetCreditsAvailable = resetCreditsAvailable,
                LastSuccessfulRefreshAt = fetchedAt
            },
            cancellationToken);

        return startedAt;
    }

    private static DateTimeOffset? InferCurrentPeriodStartedAt(
        DateTimeOffset? shortWindowResetsAt,
        int? shortWindowMinutes,
        DateTimeOffset? longWindowResetsAt,
        int? longWindowMinutes)
    {
        var candidates = new List<DateTimeOffset>();
        AddWindowStartedAt(candidates, shortWindowResetsAt, shortWindowMinutes);
        AddWindowStartedAt(candidates, longWindowResetsAt, longWindowMinutes);
        return candidates.Count == 0 ? null : candidates.Min();
    }

    private static void AddWindowStartedAt(ICollection<DateTimeOffset> candidates, DateTimeOffset? resetsAt, int? minutes)
    {
        if (resetsAt.HasValue && minutes.HasValue && minutes.Value > 0)
        {
            candidates.Add(resetsAt.Value - TimeSpan.FromMinutes(minutes.Value));
        }
    }

    private async Task<TokenUsageRangeSnapshot?> ResolveCurrentPeriodTokenUsageAsync(
        TokenUsageRanges parsed,
        DateTimeOffset? shortWindowResetsAt,
        DateTimeOffset? longWindowResetsAt,
        int? resetCreditsAvailable,
        DateTimeOffset fetchedAt,
        CancellationToken cancellationToken)
    {
        if (parsed.CurrentPeriod?.HasAnyTokens == true)
        {
            await SaveObservedTokenUsageStateAsync(
                parsed.Cumulative ?? parsed.CurrentPeriod,
                parsed.CurrentPeriod,
                shortWindowResetsAt,
                longWindowResetsAt,
                resetCreditsAvailable,
                fetchedAt,
                cancellationToken);
            return parsed.CurrentPeriod;
        }

        if (parsed.Cumulative?.HasAnyTokens != true)
        {
            return null;
        }

        var state = await _tokenUsageStateStore.LoadAsync(cancellationToken);
        var periodChanged = HasPeriodBoundaryChanged(state, shortWindowResetsAt, longWindowResetsAt, resetCreditsAvailable);
        var cumulativeInput = parsed.Cumulative.InputTokens;
        var cumulativeOutput = parsed.Cumulative.OutputTokens;

        var periodInput = periodChanged
            ? 0
            : Math.Max(0, state.CurrentPeriodInputTokens ?? 0);
        var periodOutput = periodChanged
            ? 0
            : Math.Max(0, state.CurrentPeriodOutputTokens ?? 0);

        if (state.LastObservedInputTokens.HasValue && cumulativeInput.HasValue && cumulativeInput.Value >= state.LastObservedInputTokens.Value)
        {
            periodInput += cumulativeInput.Value - state.LastObservedInputTokens.Value;
        }

        if (state.LastObservedOutputTokens.HasValue && cumulativeOutput.HasValue && cumulativeOutput.Value >= state.LastObservedOutputTokens.Value)
        {
            periodOutput += cumulativeOutput.Value - state.LastObservedOutputTokens.Value;
        }

        var updatedPeriod = new TokenUsageRangeSnapshot
        {
            InputTokens = cumulativeInput.HasValue ? periodInput : null,
            OutputTokens = cumulativeOutput.HasValue ? periodOutput : null
        };

        var updatedState = state with
        {
            CurrentPeriodStartedAt = periodChanged || state.CurrentPeriodStartedAt is null ? fetchedAt : state.CurrentPeriodStartedAt,
            CurrentPeriodInputTokens = updatedPeriod.InputTokens,
            CurrentPeriodOutputTokens = updatedPeriod.OutputTokens,
            LastShortWindowResetsAt = shortWindowResetsAt,
            LastLongWindowResetsAt = longWindowResetsAt,
            LastResetCreditsAvailable = resetCreditsAvailable,
            LastObservedInputTokens = cumulativeInput,
            LastObservedOutputTokens = cumulativeOutput,
            LastSuccessfulRefreshAt = fetchedAt
        };

        await _tokenUsageStateStore.SaveAsync(updatedState, cancellationToken);
        return updatedPeriod.HasAnyTokens ? updatedPeriod : null;
    }

    private async Task SaveObservedTokenUsageStateAsync(
        TokenUsageRangeSnapshot observed,
        TokenUsageRangeSnapshot period,
        DateTimeOffset? shortWindowResetsAt,
        DateTimeOffset? longWindowResetsAt,
        int? resetCreditsAvailable,
        DateTimeOffset fetchedAt,
        CancellationToken cancellationToken)
    {
        var state = await _tokenUsageStateStore.LoadAsync(cancellationToken);
        var periodChanged = HasPeriodBoundaryChanged(state, shortWindowResetsAt, longWindowResetsAt, resetCreditsAvailable);

        await _tokenUsageStateStore.SaveAsync(
            state with
            {
                CurrentPeriodStartedAt = periodChanged || state.CurrentPeriodStartedAt is null ? fetchedAt : state.CurrentPeriodStartedAt,
                CurrentPeriodInputTokens = period.InputTokens,
                CurrentPeriodOutputTokens = period.OutputTokens,
                LastShortWindowResetsAt = shortWindowResetsAt,
                LastLongWindowResetsAt = longWindowResetsAt,
                LastResetCreditsAvailable = resetCreditsAvailable,
                LastObservedInputTokens = observed.InputTokens,
                LastObservedOutputTokens = observed.OutputTokens,
                LastSuccessfulRefreshAt = fetchedAt
            },
            cancellationToken);
    }

    private static bool HasPeriodBoundaryChanged(
        TokenUsageState state,
        DateTimeOffset? shortWindowResetsAt,
        DateTimeOffset? longWindowResetsAt,
        int? resetCreditsAvailable)
    {
        if (state.LastSuccessfulRefreshAt is null)
        {
            return true;
        }

        return HasResetTimeJumped(state.LastShortWindowResetsAt, shortWindowResetsAt)
            || HasResetTimeJumped(state.LastLongWindowResetsAt, longWindowResetsAt)
            || (state.LastResetCreditsAvailable.HasValue
                && resetCreditsAvailable.HasValue
                && resetCreditsAvailable.Value < state.LastResetCreditsAvailable.Value);
    }

    private static bool HasResetTimeJumped(DateTimeOffset? previous, DateTimeOffset? current)
    {
        if (!previous.HasValue || !current.HasValue)
        {
            return false;
        }

        return current.Value > previous.Value.AddMinutes(5);
    }

    private static TokenUsageRanges ParseTokenUsageRanges(JsonElement root)
    {
        var candidates = new List<TokenUsageCandidate>();
        CollectTokenUsageCandidates(root, scope: null, candidates);

        return new TokenUsageRanges(
            PickBestTokenUsage(candidates, TokenUsageScope.Current),
            PickBestTokenUsage(candidates, TokenUsageScope.Today),
            PickBestTokenUsage(candidates, TokenUsageScope.CurrentPeriod),
            PickBestTokenUsage(candidates, TokenUsageScope.Cumulative));
    }

    private static void CollectTokenUsageCandidates(JsonElement element, TokenUsageScope? scope, ICollection<TokenUsageCandidate> candidates)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                CollectTokenUsageCandidates(item, scope, candidates);
            }

            return;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var localMetricsByScope = new Dictionary<TokenUsageScope, TokenUsageRangeSnapshot>();

        foreach (var property in element.EnumerateObject())
        {
            var propertyScope = MergeScope(scope, InferTokenUsageScope(property.Name));
            if (propertyScope.HasValue
                && IsTokenUsageMetricName(property.Name)
                && TryReadInt64(property.Value, out var tokenValue))
            {
                localMetricsByScope.TryGetValue(propertyScope.Value, out var localMetrics);
                localMetricsByScope[propertyScope.Value] = MergeTokenUsageMetric(localMetrics, property.Name, tokenValue);
            }
        }

        foreach (var entry in localMetricsByScope)
        {
            if (entry.Value.HasAnyTokens)
            {
                candidates.Add(new TokenUsageCandidate(entry.Key, entry.Value));
            }
        }

        foreach (var property in element.EnumerateObject())
        {
            var childScope = MergeScope(scope, InferTokenUsageScope(property.Name));
            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                CollectTokenUsageCandidates(property.Value, childScope, candidates);
            }
            else if (property.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in property.Value.EnumerateArray())
                {
                    CollectTokenUsageCandidates(item, childScope, candidates);
                }
            }
        }
    }

    private static TokenUsageRangeSnapshot? PickBestTokenUsage(
        IEnumerable<TokenUsageCandidate> candidates,
        TokenUsageScope scope)
    {
        return candidates
            .Where(candidate => candidate.Scope == scope)
            .Select(candidate => candidate.Metrics)
            .Where(static metrics => metrics.HasAnyTokens)
            .OrderByDescending(static metrics => CountTokenMetrics(metrics))
            .ThenByDescending(static metrics => metrics.EffectiveTotalTokens ?? 0)
            .FirstOrDefault();
    }

    private static TokenUsageScope? MergeScope(TokenUsageScope? current, TokenUsageScope? next) => next ?? current;

    private static int CountTokenMetrics(TokenUsageRangeSnapshot metrics)
    {
        var count = 0;
        if (metrics.InputTokens.HasValue) count++;
        if (metrics.OutputTokens.HasValue) count++;
        if (metrics.TotalTokens.HasValue) count++;
        return count;
    }

    private static TokenUsageScope? InferTokenUsageScope(string propertyName)
    {
        var normalized = NormalizeJsonName(propertyName);
        if (normalized.Contains("today", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("daily", StringComparison.OrdinalIgnoreCase))
        {
            return TokenUsageScope.Today;
        }

        if (normalized.Contains("currentperiod", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("billingperiod", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("quotaperiod", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("cycle", StringComparison.OrdinalIgnoreCase))
        {
            return TokenUsageScope.CurrentPeriod;
        }

        if (normalized.Contains("session", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("conversation", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("task", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("turn", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("current", StringComparison.OrdinalIgnoreCase))
        {
            return TokenUsageScope.Current;
        }

        if (normalized.Contains("lifetime", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("alltime", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("total", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("usage", StringComparison.OrdinalIgnoreCase))
        {
            return TokenUsageScope.Cumulative;
        }

        return null;
    }

    private static bool IsTokenUsageMetricName(string propertyName)
    {
        var normalized = NormalizeJsonName(propertyName);
        return normalized.Contains("tokens", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("token", StringComparison.OrdinalIgnoreCase);
    }

    private static TokenUsageRangeSnapshot MergeTokenUsageMetric(
        TokenUsageRangeSnapshot? metrics,
        string propertyName,
        long value)
    {
        metrics ??= new TokenUsageRangeSnapshot();
        var normalized = NormalizeJsonName(propertyName);
        value = Math.Max(0, value);

        if (normalized.Contains("input", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("prompt", StringComparison.OrdinalIgnoreCase))
        {
            return metrics with { InputTokens = value };
        }

        if (normalized.Contains("output", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("completion", StringComparison.OrdinalIgnoreCase))
        {
            return metrics with { OutputTokens = value };
        }

        return metrics with { TotalTokens = value };
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

    private static string NormalizeJsonName(string value) =>
        value.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);

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

        var reportedAvailableCount = resetCredits.AvailableCount ?? resetCredits.SnakeCaseAvailableCount;
        if (resetCredits.Credits is null || resetCredits.Credits.Count == 0)
        {
            return reportedAvailableCount ?? (resetCreditExpirations.Count > 0 ? resetCreditExpirations.Count : null);
        }

        var availableCredits = resetCredits.Credits.Count(static credit =>
            string.IsNullOrWhiteSpace(credit.Status)
            || string.Equals(credit.Status, "available", StringComparison.OrdinalIgnoreCase));

        var listAvailableCount = availableCredits > 0
            ? availableCredits
            : resetCreditExpirations.Count > 0
                ? resetCreditExpirations.Count
                : resetCredits.Credits.Count;

        return listAvailableCount > 0 ? listAvailableCount : reportedAvailableCount;
    }

    private static bool HasCreditRows(CodexRateLimitResetCredits? resetCredits) =>
        resetCredits?.Credits is { Count: > 0 };

    private static CodexRateLimitResetCredits? PreferDetailedResetCredits(
        CodexRateLimitResetCredits? primary,
        CodexRateLimitResetCredits? fallback)
    {
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

    private enum TokenUsageScope
    {
        Current,
        Today,
        CurrentPeriod,
        Cumulative
    }

    private sealed record TokenUsageCandidate(TokenUsageScope Scope, TokenUsageRangeSnapshot Metrics);

}
