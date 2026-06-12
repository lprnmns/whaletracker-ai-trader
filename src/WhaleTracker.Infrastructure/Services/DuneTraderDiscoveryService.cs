using System.Globalization;
using System.Net;
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
            ? await ExecuteSavedQueryAsync(_settings.QueryId.Value, request, progress, cancellationToken)
            : await ExecuteSqlAsync(BuildDiscoverySql(request), progress, cancellationToken);
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
            var diagnostics = ParseDiagnostics(root);
            _logger.LogInformation(
                "Dune discovery {ExecutionId} completed with {CandidateCount} candidates from {ActiveWalletCount} active wallets.",
                executionId,
                candidates.Count,
                diagnostics.ActiveWalletCount);

            return new TraderDiscoveryResult
            {
                ExecutionId = executionId,
                State = state,
                StartedAtUtc = startedAt,
                CompletedAtUtc = DateTime.UtcNow,
                Candidates = candidates,
                Diagnostics = diagnostics
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
            WITH raw_major_swaps AS (
                SELECT
                    t.blockchain,
                    t.tx_hash,
                    t.tx_from AS wallet,
                    t.block_time,
                    t.amount_usd,
                    upper(coalesce(t.token_bought_symbol, '')) AS bought_symbol,
                    upper(coalesce(t.token_sold_symbol, '')) AS sold_symbol
                FROM dex.trades t
                WHERE t.block_date >= current_date - interval '{{request.LookbackDays}}' day
                  AND t.block_time >= now() - interval '{{request.LookbackDays}}' day
                  AND t.blockchain IN ('ethereum', 'arbitrum', 'base', 'optimism')
                  AND t.tx_from IS NOT NULL
                  AND t.amount_usd >= {{minimumSwapUsd}}
                  AND (
                      upper(coalesce(t.token_bought_symbol, '')) IN (
                          'BTC', 'WBTC', 'CBBTC', 'ETH', 'WETH', 'SOL', 'LINK', 'AVAX'
                      )
                      OR upper(coalesce(t.token_sold_symbol, '')) IN (
                          'BTC', 'WBTC', 'CBBTC', 'ETH', 'WETH', 'SOL', 'LINK', 'AVAX'
                      )
                  )
            ),
            approved_pair_swaps AS (
                SELECT *
                FROM raw_major_swaps
                WHERE bought_symbol IN (
                        'BTC', 'WBTC', 'CBBTC', 'ETH', 'WETH', 'SOL', 'LINK', 'AVAX',
                        'USDT', 'USDC', 'DAI', 'USDE'
                    )
                  AND sold_symbol IN (
                        'BTC', 'WBTC', 'CBBTC', 'ETH', 'WETH', 'SOL', 'LINK', 'AVAX',
                        'USDT', 'USDC', 'DAI', 'USDE'
                    )
            ),
            sandwich_wallets AS (
                SELECT DISTINCT blockchain, tx_from AS wallet
                FROM dex.sandwiches
                WHERE block_month >= date_trunc('month', current_date - interval '{{request.LookbackDays}}' day)
                  AND block_time >= now() - interval '{{request.LookbackDays}}' day
                  AND blockchain IN ('ethereum', 'arbitrum', 'base', 'optimism')
                  AND tx_from IS NOT NULL
            ),
            eligible_swap_legs AS (
                SELECT s.*
                FROM approved_pair_swaps s
                WHERE NOT EXISTS (
                      SELECT 1
                      FROM labels.addresses l
                      WHERE l.blockchain = s.blockchain
                        AND l.address = s.wallet
                        AND lower(l.category) IN (
                            'cex', 'bridge', 'mev', 'bot'
                        )
                  )
                  AND NOT EXISTS (
                      SELECT 1
                      FROM sandwich_wallets mev
                      WHERE mev.blockchain = s.blockchain
                        AND mev.wallet = s.wallet
                  )
            ),
            transaction_swaps AS (
                SELECT
                    blockchain,
                    tx_hash,
                    wallet,
                    min(block_time) AS block_time,
                    max(amount_usd) AS amount_usd,
                    max(bought_symbol) AS bought_symbol,
                    max(sold_symbol) AS sold_symbol
                FROM eligible_swap_legs
                GROUP BY blockchain, tx_hash, wallet
            ),
            daily_activity AS (
                SELECT
                    wallet,
                    max(daily_swaps) AS maximum_daily_swaps
                FROM (
                    SELECT wallet, date(block_time) AS trade_date, count(*) AS daily_swaps
                    FROM transaction_swaps
                    GROUP BY wallet, date(block_time)
                )
                GROUP BY wallet
            ),
            wallet_assets AS (
                SELECT
                    wallet,
                    count(DISTINCT asset) AS distinct_major_assets
                FROM (
                    SELECT wallet, bought_symbol AS asset
                    FROM transaction_swaps
                    WHERE bought_symbol NOT IN ('USDT', 'USDC', 'DAI', 'USDE')
                    UNION
                    SELECT wallet, sold_symbol AS asset
                    FROM transaction_swaps
                    WHERE sold_symbol NOT IN ('USDT', 'USDC', 'DAI', 'USDE')
                )
                GROUP BY wallet
            ),
            activity_qualified_wallets AS (
                SELECT
                    t.wallet,
                    count(*) AS meaningful_swap_count,
                    count(DISTINCT date_trunc('week', block_time)) AS active_week_count,
                    round(sum(amount_usd), 2) AS approved_notional_usd,
                    round(avg(amount_usd), 2) AS average_swap_usd,
                    d.maximum_daily_swaps,
                    a.distinct_major_assets,
                    round(
                        100 * (
                            0.45 * least(count(DISTINCT date_trunc('week', block_time)) / {{Math.Max(1m, request.LookbackDays / 7m).ToString("0.########", CultureInfo.InvariantCulture)}}, 1) +
                            0.25 * least(a.distinct_major_assets / 4.0, 1) +
                            0.20 * (1 - least(d.maximum_daily_swaps / 50.0, 1)) +
                            0.10 * least(ln(1 + avg(amount_usd)) / ln(100001), 1)
                        ),
                        2
                    ) AS copyability_score,
                    count(DISTINCT blockchain) AS active_chain_count,
                    array_agg(DISTINCT blockchain) AS active_chains,
                    min(block_time) AS first_trade_utc,
                    max(block_time) AS last_trade_utc
                FROM transaction_swaps t
                JOIN daily_activity d ON d.wallet = t.wallet
                JOIN wallet_assets a ON a.wallet = t.wallet
                GROUP BY t.wallet, d.maximum_daily_swaps, a.distinct_major_assets
                HAVING count(*) >= {{request.MinimumMeaningfulSwaps}}
                   AND count(DISTINCT date_trunc('week', block_time)) >= {{request.MinimumActiveWeeks}}
                   AND count(*) <= {{request.LookbackDays * 12}}
                   AND d.maximum_daily_swaps <= 50
                   AND a.distinct_major_assets >= 2
            ),
            prequalified_wallets AS (
                SELECT *
                FROM activity_qualified_wallets
                ORDER BY copyability_score DESC, approved_notional_usd DESC
                LIMIT {{Math.Min(2500, request.CandidateLimit * 5)}}
            ),
            selected_candidates AS (
                SELECT *
                FROM prequalified_wallets
                ORDER BY copyability_score DESC, approved_notional_usd DESC
                LIMIT {{request.CandidateLimit}}
            ),
            diagnostics AS (
                SELECT
                    (SELECT count(*) FROM raw_major_swaps) AS raw_swap_count,
                    (SELECT count(*) FROM approved_pair_swaps) AS approved_pair_swap_count,
                    (SELECT count(DISTINCT concat(blockchain, ':', to_hex(tx_hash))) FROM eligible_swap_legs) AS eligible_transaction_count,
                    (SELECT count(DISTINCT wallet) FROM transaction_swaps) AS wallet_count,
                    (SELECT count(*) FROM activity_qualified_wallets) AS active_wallet_count
            )
            SELECT
                'candidate' AS row_kind,
                concat('0x', lower(to_hex(wallet))) AS wallet_address,
                meaningful_swap_count,
                active_week_count,
                approved_notional_usd,
                average_swap_usd,
                maximum_daily_swaps,
                distinct_major_assets,
                copyability_score,
                active_chain_count,
                active_chains,
                first_trade_utc,
                last_trade_utc,
                cast(null AS bigint) AS raw_swap_count,
                cast(null AS bigint) AS approved_pair_swap_count,
                cast(null AS bigint) AS eligible_transaction_count,
                cast(null AS bigint) AS wallet_count,
                cast(null AS bigint) AS active_wallet_count
            FROM selected_candidates
            UNION ALL
            SELECT
                'diagnostics' AS row_kind,
                cast(null AS varchar) AS wallet_address,
                cast(null AS bigint) AS meaningful_swap_count,
                cast(null AS bigint) AS active_week_count,
                cast(null AS double) AS approved_notional_usd,
                cast(null AS double) AS average_swap_usd,
                cast(null AS bigint) AS maximum_daily_swaps,
                cast(null AS bigint) AS distinct_major_assets,
                cast(null AS double) AS copyability_score,
                cast(null AS bigint) AS active_chain_count,
                cast(array[] AS array(varchar)) AS active_chains,
                cast(null AS timestamp) AS first_trade_utc,
                cast(null AS timestamp) AS last_trade_utc,
                raw_swap_count,
                approved_pair_swap_count,
                eligible_transaction_count,
                wallet_count,
                active_wallet_count
            FROM diagnostics
            """;
    }

    private async Task<string> ExecuteSqlAsync(
        string sql,
        Func<TraderDiscoveryProgress, CancellationToken, Task>? progress,
        CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object> { ["sql"] = sql };
        if (!string.IsNullOrWhiteSpace(_settings.Performance))
        {
            payload["performance"] = _settings.Performance;
        }

        using var response = await SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Post, "sql/execute")
            {
                Content = JsonContent.Create(payload)
            },
            progress,
            8,
            "submitting_query",
            string.Empty,
            cancellationToken);
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
        Func<TraderDiscoveryProgress, CancellationToken, Task>? progress,
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

        using var response = await SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Post, $"query/{queryId}/execute")
            {
                Content = JsonContent.Create(payload)
            },
            progress,
            8,
            "submitting_query",
            string.Empty,
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
            using var response = await SendWithRetryAsync(
                () => new HttpRequestMessage(
                    HttpMethod.Get,
                    $"execution/{executionId}/results?limit=1000"),
                progress,
                lastReportedPercent,
                "polling_dune",
                executionId,
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

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<HttpRequestMessage> requestFactory,
        Func<TraderDiscoveryProgress, CancellationToken, Task>? progress,
        int progressPercent,
        string progressStage,
        string executionId,
        CancellationToken cancellationToken)
    {
        const int maximumAttempts = 4;
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            HttpResponseMessage? response = null;
            try
            {
                using var request = requestFactory();
                response = await _httpClient.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode || !IsTransient(response.StatusCode))
                {
                    return response;
                }

                if (attempt == maximumAttempts)
                {
                    return response;
                }

                var delay = response.Headers.RetryAfter?.Delta ??
                            TimeSpan.FromSeconds(Math.Pow(2, attempt));
                _logger.LogWarning(
                    "Dune returned {StatusCode}. Retrying attempt {NextAttempt}/{MaximumAttempts} in {DelaySeconds}s.",
                    (int)response.StatusCode,
                    attempt + 1,
                    maximumAttempts,
                    delay.TotalSeconds);
                await ReportAsync(
                    progress,
                    progressPercent,
                    progressStage,
                    "RETRYING",
                    $"Dune returned HTTP {(int)response.StatusCode}. Retry {attempt + 1}/{maximumAttempts} in {delay.TotalSeconds:F0}s.",
                    cancellationToken,
                    executionId);
                response.Dispose();
                await Task.Delay(delay, cancellationToken);
            }
            catch (HttpRequestException ex) when (attempt < maximumAttempts)
            {
                response?.Dispose();
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                _logger.LogWarning(
                    ex,
                    "Dune network request failed. Retrying attempt {NextAttempt}/{MaximumAttempts} in {DelaySeconds}s.",
                    attempt + 1,
                    maximumAttempts,
                    delay.TotalSeconds);
                await ReportAsync(
                    progress,
                    progressPercent,
                    progressStage,
                    "RETRYING",
                    $"Dune network error: {ex.Message}. Retry {attempt + 1}/{maximumAttempts} in {delay.TotalSeconds:F0}s.",
                    cancellationToken,
                    executionId);
                await Task.Delay(delay, cancellationToken);
            }
        }

        throw new HttpRequestException("Dune request failed after retries.");
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
        (int)statusCode >= 500;

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
                AverageSwapUsd = GetDecimal(row, "average_swap_usd"),
                MaximumDailySwaps = GetInt(row, "maximum_daily_swaps"),
                DistinctMajorAssets = GetInt(row, "distinct_major_assets"),
                CopyabilityScore = GetDecimal(row, "copyability_score"),
                ActiveChainCount = GetInt(row, "active_chain_count"),
                ActiveChains = GetStringArray(row, "active_chains"),
                FirstTradeUtc = GetDateTime(row, "first_trade_utc"),
                LastTradeUtc = GetDateTime(row, "last_trade_utc")
            })
            .Where(candidate => candidate.WalletAddress.Length == 42)
            .ToList();
    }

    private static TraderDiscoveryDiagnostics ParseDiagnostics(JsonElement root)
    {
        if (!root.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("rows", out var rows) ||
            rows.ValueKind != JsonValueKind.Array)
        {
            return new TraderDiscoveryDiagnostics();
        }

        var row = rows.EnumerateArray().FirstOrDefault(item =>
            GetString(item, "row_kind").Equals("diagnostics", StringComparison.OrdinalIgnoreCase));
        if (row.ValueKind == JsonValueKind.Undefined)
        {
            return new TraderDiscoveryDiagnostics();
        }

        return new TraderDiscoveryDiagnostics
        {
            RawSwapCount = GetLong(row, "raw_swap_count"),
            ApprovedPairSwapCount = GetLong(row, "approved_pair_swap_count"),
            EligibleTransactionCount = GetLong(row, "eligible_transaction_count"),
            WalletCount = GetLong(row, "wallet_count"),
            ActiveWalletCount = GetLong(row, "active_wallet_count")
        };
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

    private static long GetLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) &&
        long.TryParse(value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;

    private static decimal GetDecimal(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) &&
        decimal.TryParse(value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0m;

    private static DateTime GetDateTime(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return DateTime.MinValue;
        }

        var text = value.ToString().Trim();
        if (text.EndsWith(" UTC", StringComparison.OrdinalIgnoreCase))
        {
            text = $"{text[..^4]}Z";
        }

        return DateTime.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : DateTime.MinValue;
    }

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
