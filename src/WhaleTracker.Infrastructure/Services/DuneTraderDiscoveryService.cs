using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WhaleTracker.Core.Interfaces;
using WhaleTracker.Core.Models;

namespace WhaleTracker.Infrastructure.Services;

public sealed class DuneTraderDiscoveryService : ITraderDiscoveryService
{
    private static readonly HashSet<string> TerminalStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "QUERY_STATE_COMPLETED",
        "QUERY_STATE_FAILED",
        "QUERY_STATE_CANCELLED",
        "QUERY_STATE_EXPIRED"
    };

    private readonly HttpClient _httpClient;
    private readonly ILogger<DuneTraderDiscoveryService> _logger;
    private readonly DuneSettings _settings;

    public DuneTraderDiscoveryService(
        HttpClient httpClient,
        ILogger<DuneTraderDiscoveryService> logger,
        IOptions<AppSettings> settings)
    {
        _httpClient = httpClient;
        _logger = logger;
        _settings = settings.Value.Dune;
        _httpClient.BaseAddress = new Uri(_settings.BaseUrl);
        _httpClient.DefaultRequestHeaders.Add("X-Dune-Api-Key", _settings.ApiKey);
    }

    public async Task<TraderDiscoveryResult> DiscoverAsync(
        TraderDiscoveryRequest request,
        Func<TraderDiscoveryProgress, CancellationToken, Task>? progress = null,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        NormalizeRequest(request);

        var startedAt = DateTime.UtcNow;
        await ReportAsync(progress, 5, "preparing_sql", "PREPARING", "Dune SQL and filters are being prepared.", cancellationToken);
        var executionId = _settings.QueryId.HasValue
            ? await ExecuteSavedQueryAsync(_settings.QueryId.Value, request, cancellationToken)
            : await ExecuteSqlAsync(BuildDiscoverySql(request), cancellationToken);
        await ReportAsync(
            progress,
            15,
            "submitted",
            "QUERY_STATE_PENDING",
            $"Query submitted to Dune. Execution: {executionId}",
            cancellationToken,
            executionId);

        var resultDocument = await WaitForResultAsync(executionId, progress, cancellationToken);
        using (resultDocument)
        {
            var root = resultDocument.RootElement;
            var state = GetString(root, "state");
            if (!state.Equals("QUERY_STATE_COMPLETED", StringComparison.OrdinalIgnoreCase))
            {
                var error = GetString(root, "error");
                throw new InvalidOperationException(
                    $"Dune execution {executionId} ended as {state}: {error}");
            }

            await ReportAsync(
                progress,
                85,
                "parsing_results",
                state,
                "Dune completed. Candidate rows are being validated.",
                cancellationToken,
                executionId);
            var candidates = ParseCandidates(root);
            _logger.LogInformation(
                "Dune discovery {ExecutionId} completed with {CandidateCount} candidates.",
                executionId,
                candidates.Count);

            return new TraderDiscoveryResult
            {
                ExecutionId = executionId,
                State = state,
                StartedAtUtc = startedAt,
                CompletedAtUtc = DateTime.UtcNow,
                Candidates = candidates
            };
        }
    }

    public async Task<bool> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        using var request = new HttpRequestMessage(HttpMethod.Get, "query/1");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode ||
               response.StatusCode is System.Net.HttpStatusCode.Forbidden
                   or System.Net.HttpStatusCode.NotFound;
    }

    public static string BuildDiscoverySql(TraderDiscoveryRequest request)
    {
        NormalizeRequest(request);
        var minimumSwapUsd = request.MinimumSwapUsd.ToString("0.########", CultureInfo.InvariantCulture);

        return $$"""
            WITH transaction_swaps AS (
                SELECT
                    blockchain,
                    tx_hash,
                    tx_from AS wallet,
                    min(block_time) AS block_time,
                    max(amount_usd) AS amount_usd
                FROM dex.trades
                WHERE block_date >= current_date - interval '{{request.LookbackDays}}' day
                  AND block_time >= now() - interval '{{request.LookbackDays}}' day
                  AND blockchain IN ('ethereum', 'arbitrum', 'base', 'optimism')
                  AND tx_from IS NOT NULL
                  AND amount_usd >= {{minimumSwapUsd}}
                  AND (
                      upper(coalesce(token_bought_symbol, '')) IN (
                          'BTC', 'WBTC', 'CBBTC', 'ETH', 'WETH', 'SOL', 'LINK', 'AVAX'
                      )
                      OR upper(coalesce(token_sold_symbol, '')) IN (
                          'BTC', 'WBTC', 'CBBTC', 'ETH', 'WETH', 'SOL', 'LINK', 'AVAX'
                      )
                  )
                  AND (
                    upper(coalesce(token_bought_symbol, '')) IN (
                        'BTC', 'WBTC', 'CBBTC', 'ETH', 'WETH', 'SOL', 'LINK', 'AVAX',
                        'USDT', 'USDC', 'DAI', 'USDE'
                    )
                    AND upper(coalesce(token_sold_symbol, '')) IN (
                        'BTC', 'WBTC', 'CBBTC', 'ETH', 'WETH', 'SOL', 'LINK', 'AVAX',
                        'USDT', 'USDC', 'DAI', 'USDE'
                    )
                  )
                GROUP BY blockchain, tx_hash, tx_from
            ),
            prequalified_wallets AS (
                SELECT
                    wallet,
                    count(*) AS meaningful_swap_count,
                    count(DISTINCT date_trunc('week', block_time)) AS active_week_count,
                    round(sum(amount_usd), 2) AS approved_notional_usd,
                    count(DISTINCT blockchain) AS active_chain_count,
                    array_agg(DISTINCT blockchain) AS active_chains,
                    min(block_time) AS first_trade_utc,
                    max(block_time) AS last_trade_utc
                FROM transaction_swaps
                GROUP BY wallet
                HAVING count(*) >= {{request.MinimumMeaningfulSwaps}}
                   AND count(DISTINCT date_trunc('week', block_time)) >= {{request.MinimumActiveWeeks}}
                ORDER BY approved_notional_usd DESC
                LIMIT {{Math.Min(2500, request.CandidateLimit * 5)}}
            ),
            eligible_wallets AS (
                SELECT p.*
                FROM prequalified_wallets p
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM labels.addresses l
                    WHERE l.address = p.wallet
                      AND lower(l.category) IN (
                          'cex', 'bridge', 'dex', 'dao', 'mev', 'contract', 'bot'
                      )
                )
            )
            SELECT
                concat('0x', lower(to_hex(wallet))) AS wallet_address,
                meaningful_swap_count,
                active_week_count,
                approved_notional_usd,
                active_chain_count,
                active_chains,
                first_trade_utc,
                last_trade_utc
            FROM eligible_wallets
            ORDER BY approved_notional_usd DESC
            LIMIT {{request.CandidateLimit}}
            """;
    }

    private async Task<string> ExecuteSqlAsync(string sql, CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object> { ["sql"] = sql };
        if (!string.IsNullOrWhiteSpace(_settings.Performance))
        {
            payload["performance"] = _settings.Performance;
        }

        using var response = await _httpClient.PostAsJsonAsync("sql/execute", payload, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Dune SQL execution returned {(int)response.StatusCode}: {body}");
        }

        using var document = JsonDocument.Parse(body);
        return GetRequiredString(document.RootElement, "execution_id");
    }

    private async Task<string> ExecuteSavedQueryAsync(
        long queryId,
        TraderDiscoveryRequest request,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            query_parameters = new Dictionary<string, object>
            {
                ["lookback_days"] = request.LookbackDays,
                ["minimum_active_weeks"] = request.MinimumActiveWeeks,
                ["minimum_meaningful_swaps"] = request.MinimumMeaningfulSwaps,
                ["minimum_swap_usd"] = request.MinimumSwapUsd,
                ["candidate_limit"] = request.CandidateLimit
            }
        };

        using var response = await _httpClient.PostAsJsonAsync(
            $"query/{queryId}/execute",
            payload,
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Dune query {queryId} execution returned {(int)response.StatusCode}: {body}");
        }

        using var document = JsonDocument.Parse(body);
        return GetRequiredString(document.RootElement, "execution_id");
    }

    private async Task<JsonDocument> WaitForResultAsync(
        string executionId,
        Func<TraderDiscoveryProgress, CancellationToken, Task>? progress,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(Math.Clamp(_settings.TimeoutSeconds, 10, 900));
        var pollInterval = TimeSpan.FromSeconds(Math.Clamp(_settings.PollIntervalSeconds, 1, 30));

        var lastState = string.Empty;
        var pollCount = 0;
        var lastReportedPercent = 15;
        while (DateTime.UtcNow < deadline)
        {
            using var response = await _httpClient.GetAsync(
                $"execution/{executionId}/results?limit=1000",
                cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Dune result request returned {(int)response.StatusCode}: {body}");
            }

            var document = JsonDocument.Parse(body);
            var state = GetString(document.RootElement, "state");
            pollCount++;
            if (!state.Equals(lastState, StringComparison.OrdinalIgnoreCase) || pollCount % 5 == 0)
            {
                var elapsed = DateTime.UtcNow - (deadline.AddSeconds(-Math.Clamp(_settings.TimeoutSeconds, 10, 900)));
                var percent = state.Equals("QUERY_STATE_COMPLETED", StringComparison.OrdinalIgnoreCase)
                    ? 80
                    : state.Equals("QUERY_STATE_EXECUTING", StringComparison.OrdinalIgnoreCase)
                        ? Math.Min(78, 25 + pollCount * 2)
                        : Math.Min(35, 15 + pollCount);
                percent = Math.Max(lastReportedPercent, percent);
                await ReportAsync(
                    progress,
                    percent,
                    "dune_execution",
                    state,
                    $"Dune is {state.Replace("QUERY_STATE_", string.Empty).ToLowerInvariant()} ({elapsed.TotalSeconds:F0}s elapsed).",
                    cancellationToken,
                    executionId);
                lastReportedPercent = percent;
                lastState = state;
            }
            if (TerminalStates.Contains(state))
            {
                return document;
            }

            document.Dispose();
            await Task.Delay(pollInterval, cancellationToken);
        }

        try
        {
            using var cancelResponse = await _httpClient.PostAsync(
                $"execution/{executionId}/cancel",
                content: null,
                cancellationToken);
            _logger.LogWarning(
                "Cancelled Dune execution {ExecutionId} after timeout. Status: {StatusCode}",
                executionId,
                (int)cancelResponse.StatusCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not cancel timed out Dune execution {ExecutionId}.", executionId);
        }

        throw new TimeoutException($"Dune execution {executionId} did not finish before timeout.");
    }

    private static Task ReportAsync(
        Func<TraderDiscoveryProgress, CancellationToken, Task>? progress,
        int percent,
        string stage,
        string state,
        string message,
        CancellationToken cancellationToken,
        string executionId = "") =>
        progress?.Invoke(new TraderDiscoveryProgress
        {
            Percent = Math.Clamp(percent, 0, 100),
            Stage = stage,
            State = state,
            Message = message,
            ExecutionId = executionId,
            TimestampUtc = DateTime.UtcNow
        }, cancellationToken) ?? Task.CompletedTask;

    private static List<TraderDiscoveryCandidate> ParseCandidates(JsonElement root)
    {
        if (!root.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("rows", out var rows) ||
            rows.ValueKind != JsonValueKind.Array)
        {
            return new List<TraderDiscoveryCandidate>();
        }

        return rows.EnumerateArray()
            .Select(row => new TraderDiscoveryCandidate
            {
                WalletAddress = GetString(row, "wallet_address").ToLowerInvariant(),
                MeaningfulSwapCount = GetInt(row, "meaningful_swap_count"),
                ActiveWeekCount = GetInt(row, "active_week_count"),
                ApprovedNotionalUsd = GetDecimal(row, "approved_notional_usd"),
                ActiveChainCount = GetInt(row, "active_chain_count"),
                ActiveChains = GetStringArray(row, "active_chains"),
                FirstTradeUtc = GetDateTime(row, "first_trade_utc"),
                LastTradeUtc = GetDateTime(row, "last_trade_utc")
            })
            .Where(candidate => candidate.WalletAddress.Length == 42)
            .ToList();
    }

    private static void NormalizeRequest(TraderDiscoveryRequest request)
    {
        request.LookbackDays = Math.Clamp(request.LookbackDays, 7, 180);
        request.MinimumActiveWeeks = Math.Clamp(
            request.MinimumActiveWeeks,
            1,
            Math.Max(1, (int)Math.Ceiling(request.LookbackDays / 7m)));
        request.MinimumMeaningfulSwaps = Math.Clamp(request.MinimumMeaningfulSwaps, 1, 1_000);
        request.MinimumSwapUsd = Math.Clamp(request.MinimumSwapUsd, 1m, 10_000_000m);
        request.CandidateLimit = Math.Clamp(request.CandidateLimit, 1, 500);
    }

    private void EnsureConfigured()
    {
        if (string.IsNullOrWhiteSpace(_settings.ApiKey))
        {
            throw new InvalidOperationException("DUNE_API_KEY is not configured.");
        }
    }

    private static string GetRequiredString(JsonElement element, string name)
    {
        var value = GetString(element, name);
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Dune response did not contain {name}.")
            : value;
    }

    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.ToString()
            : string.Empty;

    private static int GetInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) &&
        int.TryParse(value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;

    private static decimal GetDecimal(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) &&
        decimal.TryParse(value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0m;

    private static DateTime GetDateTime(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) &&
        DateTime.TryParse(
            value.ToString(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : DateTime.MinValue;

    private static List<string> GetStringArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return new List<string>();
        }

        return value.EnumerateArray()
            .Select(item => item.ToString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToList();
    }
}
