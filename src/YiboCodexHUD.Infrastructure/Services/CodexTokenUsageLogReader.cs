using System.Text.Json;
using Microsoft.Extensions.Logging;
using YiboCodexHUD.Core.Models;

namespace YiboCodexHUD.Infrastructure.Services;

public sealed class CodexTokenUsageLogReader
{
    private const int MaxSessionFilesToScan = 160;
    private readonly ILogger<CodexTokenUsageLogReader> _logger;

    public CodexTokenUsageLogReader(ILogger<CodexTokenUsageLogReader> logger)
    {
        _logger = logger;
    }

    public async Task<TokenUsageRanges> TryReadAsync(DateTimeOffset? currentPeriodStartedAt, DateTimeOffset fetchedAt, CancellationToken cancellationToken)
    {
        try
        {
            var today = fetchedAt.ToLocalTime().Date;
            var files = EnumerateSessionFiles(currentPeriodStartedAt, fetchedAt).ToArray();
            if (files.Length == 0)
            {
                return TokenUsageRanges.Empty;
            }

            TokenUsageRangeSnapshot? latestCurrent = null;
            DateTimeOffset? latestCurrentTimestamp = null;
            var todayTotals = new TokenUsageTotals();
            var periodTotals = new TokenUsageTotals();

            foreach (var filePath in files)
            {
                var fileStats = await ReadSessionFileAsync(filePath, today, currentPeriodStartedAt, cancellationToken);
                if (fileStats.LatestTotal?.HasAnyTokens == true
                    && (!latestCurrentTimestamp.HasValue || fileStats.LatestTimestamp > latestCurrentTimestamp))
                {
                    latestCurrent = fileStats.LatestTotal;
                    latestCurrentTimestamp = fileStats.LatestTimestamp;
                }

                todayTotals.Add(fileStats.Today);
                periodTotals.Add(fileStats.CurrentPeriod);
            }

            return new TokenUsageRanges(
                latestCurrent,
                todayTotals.ToSnapshot(),
                periodTotals.ToSnapshot(),
                null);
        }
        catch (Exception exception)
        {
            _logger.LogInformation(exception, "Failed to read token usage from Codex session logs.");
            return TokenUsageRanges.Empty;
        }
    }

    private static IEnumerable<string> EnumerateSessionFiles(DateTimeOffset? currentPeriodStartedAt, DateTimeOffset fetchedAt)
    {
        var codexHome = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex");

        var candidates = new List<FileInfo>();
        AddSessionFiles(Path.Combine(codexHome, "sessions"), currentPeriodStartedAt, fetchedAt, candidates);
        AddSessionFiles(Path.Combine(codexHome, "archived_sessions"), currentPeriodStartedAt, fetchedAt, candidates);

        return candidates
            .GroupBy(static file => file.FullName, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .OrderByDescending(static file => file.LastWriteTimeUtc)
            .Take(MaxSessionFilesToScan)
            .Select(static file => file.FullName);
    }

    private static void AddSessionFiles(
        string directoryPath,
        DateTimeOffset? currentPeriodStartedAt,
        DateTimeOffset fetchedAt,
        ICollection<FileInfo> candidates)
    {
        if (!Directory.Exists(directoryPath))
        {
            return;
        }

        var oldestRelevantUtc = currentPeriodStartedAt?.UtcDateTime
            ?? fetchedAt.ToLocalTime().Date.ToUniversalTime();

        try
        {
            foreach (var filePath in Directory.EnumerateFiles(directoryPath, "*.jsonl", SearchOption.AllDirectories))
            {
                FileInfo fileInfo;
                try
                {
                    fileInfo = new FileInfo(filePath);
                }
                catch
                {
                    continue;
                }

                if (fileInfo.LastWriteTimeUtc >= oldestRelevantUtc.AddDays(-1))
                {
                    candidates.Add(fileInfo);
                }
            }
        }
        catch
        {
        }
    }

    private static async Task<SessionTokenUsageStats> ReadSessionFileAsync(
        string filePath,
        DateTime today,
        DateTimeOffset? currentPeriodStartedAt,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);

        TokenUsageRangeSnapshot? latestTotal = null;
        TokenUsageRangeSnapshot? previousTotal = null;
        DateTimeOffset? latestTimestamp = null;
        var todayTotals = new TokenUsageTotals();
        var periodTotals = new TokenUsageTotals();

        while (!reader.EndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var line = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(line) || !line.Contains("\"token_count\"", StringComparison.Ordinal))
            {
                continue;
            }

            if (!TryParseTokenCountLine(line, out var timestamp, out var totalUsage, out var lastUsage))
            {
                continue;
            }

            var effectiveLastUsage = lastUsage ?? SubtractTokenUsage(totalUsage, previousTotal);
            previousTotal = totalUsage;

            if (totalUsage?.HasAnyTokens == true)
            {
                latestTotal = totalUsage;
                latestTimestamp = timestamp;
            }

            if (effectiveLastUsage?.HasAnyTokens != true)
            {
                continue;
            }

            if (timestamp.ToLocalTime().Date == today)
            {
                todayTotals.Add(effectiveLastUsage);
            }

            if (!currentPeriodStartedAt.HasValue || timestamp >= currentPeriodStartedAt.Value)
            {
                periodTotals.Add(effectiveLastUsage);
            }
        }

        return new SessionTokenUsageStats(latestTotal, latestTimestamp, todayTotals, periodTotals);
    }

    private static bool TryParseTokenCountLine(
        string line,
        out DateTimeOffset timestamp,
        out TokenUsageRangeSnapshot? totalUsage,
        out TokenUsageRangeSnapshot? lastUsage)
    {
        timestamp = default;
        totalUsage = null;
        lastUsage = null;

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!TryGetTimestamp(root, out timestamp)
                || !root.TryGetProperty("payload", out var payload)
                || !payload.TryGetProperty("type", out var payloadType)
                || !string.Equals(payloadType.GetString(), "token_count", StringComparison.Ordinal)
                || !payload.TryGetProperty("info", out var info))
            {
                return false;
            }

            totalUsage = TryReadTokenUsage(info, "total_token_usage")
                ?? TryReadTokenUsage(info, "totalTokenUsage");
            lastUsage = TryReadTokenUsage(info, "last_token_usage")
                ?? TryReadTokenUsage(info, "lastTokenUsage");

            return totalUsage?.HasAnyTokens == true || lastUsage?.HasAnyTokens == true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetTimestamp(JsonElement root, out DateTimeOffset timestamp)
    {
        timestamp = default;
        return root.TryGetProperty("timestamp", out var timestampElement)
            && timestampElement.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(timestampElement.GetString(), out timestamp);
    }

    private static TokenUsageRangeSnapshot? TryReadTokenUsage(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var snapshot = new TokenUsageRangeSnapshot
        {
            InputTokens = TryReadInt64Property(usage, "input_tokens") ?? TryReadInt64Property(usage, "inputTokens"),
            OutputTokens = TryReadInt64Property(usage, "output_tokens") ?? TryReadInt64Property(usage, "outputTokens"),
            TotalTokens = TryReadInt64Property(usage, "total_tokens") ?? TryReadInt64Property(usage, "totalTokens")
        };

        return snapshot.HasAnyTokens ? snapshot : null;
    }

    private static long? TryReadInt64Property(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            return Math.Max(0, number);
        }

        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out number))
        {
            return Math.Max(0, number);
        }

        return null;
    }

    private static TokenUsageRangeSnapshot? SubtractTokenUsage(TokenUsageRangeSnapshot? current, TokenUsageRangeSnapshot? previous)
    {
        if (current?.HasAnyTokens != true || previous?.HasAnyTokens != true)
        {
            return null;
        }

        return new TokenUsageRangeSnapshot
        {
            InputTokens = SubtractNullable(current.InputTokens, previous.InputTokens),
            OutputTokens = SubtractNullable(current.OutputTokens, previous.OutputTokens),
            TotalTokens = SubtractNullable(current.TotalTokens, previous.TotalTokens)
        };
    }

    private static long? SubtractNullable(long? current, long? previous) =>
        current.HasValue && previous.HasValue && current.Value >= previous.Value
            ? current.Value - previous.Value
            : null;

    private sealed record SessionTokenUsageStats(
        TokenUsageRangeSnapshot? LatestTotal,
        DateTimeOffset? LatestTimestamp,
        TokenUsageTotals Today,
        TokenUsageTotals CurrentPeriod);

    private sealed class TokenUsageTotals
    {
        private long _inputTokens;
        private long _outputTokens;
        private long _totalTokens;
        private bool _hasInputTokens;
        private bool _hasOutputTokens;
        private bool _hasTotalTokens;

        public void Add(TokenUsageTotals totals)
        {
            Add(totals.ToSnapshot());
        }

        public void Add(TokenUsageRangeSnapshot? snapshot)
        {
            if (snapshot is null)
            {
                return;
            }

            if (snapshot.InputTokens.HasValue)
            {
                _inputTokens += snapshot.InputTokens.Value;
                _hasInputTokens = true;
            }

            if (snapshot.OutputTokens.HasValue)
            {
                _outputTokens += snapshot.OutputTokens.Value;
                _hasOutputTokens = true;
            }

            if (snapshot.TotalTokens.HasValue)
            {
                _totalTokens += snapshot.TotalTokens.Value;
                _hasTotalTokens = true;
            }
        }

        public TokenUsageRangeSnapshot? ToSnapshot()
        {
            var snapshot = new TokenUsageRangeSnapshot
            {
                InputTokens = _hasInputTokens ? _inputTokens : null,
                OutputTokens = _hasOutputTokens ? _outputTokens : null,
                TotalTokens = _hasTotalTokens ? _totalTokens : null
            };

            return snapshot.HasAnyTokens ? snapshot : null;
        }
    }
}
