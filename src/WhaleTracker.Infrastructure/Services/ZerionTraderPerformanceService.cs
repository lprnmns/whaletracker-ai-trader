using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WhaleTracker.Core.Interfaces;
using WhaleTracker.Core.Models;

namespace WhaleTracker.Infrastructure.Services;

public sealed class ZerionTraderPerformanceService : ITraderPerformanceService
{
    private static readonly SemaphoreSlim RequestGate = new(1, 1);
    private static DateTime _lastRequestAt = DateTime.MinValue;
    private readonly HttpClient _httpClient;
    private readonly ILogger<ZerionTraderPerformanceService> _logger;
    private readonly ZerionSettings _settings;

    public ZerionTraderPerformanceService(
        HttpClient httpClient,
        ILogger<ZerionTraderPerformanceService> logger,
        IOptions<AppSettings> settings)
    {
        _httpClient = httpClient;
        _logger = logger;
        _settings = settings.Value.Zerion;
        _httpClient.BaseAddress = new Uri("https://api.zerion.io/v1/");

        if (!string.IsNullOrWhiteSpace(_settings.ApiKey))
        {
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_settings.ApiKey}:"));
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        }
    }

    public async Task<TraderPerformance> AnalyzeAsync(
        string walletAddress,
        DateTime startUtc,
        DateTime endUtc,
        CancellationToken cancellationToken = default)
    {
        var normalized = walletAddress.Trim().ToLowerInvariant();
        var period = SelectChartPeriod(startUtc);

        try
        {
            var chartJson = await GetJsonAsync(
                $"wallets/{normalized}/charts/{period}?currency=usd&filter[positions]=no_filter",
                cancellationToken);
            var pnlJson = await GetJsonAsync(
                $"wallets/{normalized}/pnl?currency=usd&since={ToUnixMilliseconds(startUtc)}&till={ToUnixMilliseconds(endUtc)}",
                cancellationToken);

            var chart = ParseChart(chartJson);
            var startPoint = NearestPoint(chart, startUtc);
            var endPoint = NearestPoint(chart, endUtc);
            var pnl = ParsePnl(pnlJson);

            var adjustedProfit = TraderPerformanceMath.AdjustedProfit(
                startPoint.Value,
                endPoint.Value,
                pnl.ReceivedExternal,
                pnl.SentExternal);
            var adjustedReturn = TraderPerformanceMath.AdjustedReturnPercent(
                startPoint.Value,
                adjustedProfit);

            return new TraderPerformance
            {
                WalletAddress = normalized,
                StartingValueUsd = Round(startPoint.Value),
                EndingValueUsd = Round(endPoint.Value),
                ReceivedExternalUsd = Round(pnl.ReceivedExternal),
                SentExternalUsd = Round(pnl.SentExternal),
                TotalFeesUsd = Round(pnl.TotalFee),
                AdjustedProfitUsd = Round(adjustedProfit),
                AdjustedReturnPercent = Math.Round(adjustedReturn, 4),
                RealizedGainUsd = Round(pnl.RealizedGain),
                Score = TraderPerformanceMath.Score(adjustedProfit, adjustedReturn),
                StartPointUtc = startPoint.Timestamp,
                EndPointUtc = endPoint.Timestamp,
                ChartPeriod = period
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Trader performance analysis failed for {Wallet}", normalized);
            return new TraderPerformance
            {
                WalletAddress = normalized,
                Status = "failed",
                Error = ex.Message,
                ChartPeriod = period
            };
        }
    }

    private async Task<string> GetJsonAsync(string requestUri, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_settings.ApiKey))
        {
            throw new InvalidOperationException("ZERION_API_KEY is not configured.");
        }

        for (var attempt = 1; attempt <= 8; attempt++)
        {
            await RequestGate.WaitAsync(cancellationToken);
            HttpResponseMessage response;
            try
            {
                var elapsed = DateTime.UtcNow - _lastRequestAt;
                if (elapsed < TimeSpan.FromSeconds(2))
                {
                    await Task.Delay(TimeSpan.FromSeconds(2) - elapsed, cancellationToken);
                }

                response = await _httpClient.GetAsync(requestUri, cancellationToken);
                _lastRequestAt = DateTime.UtcNow;
            }
            finally
            {
                RequestGate.Release();
            }

            using (response)
            {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return body;
            }

            var retryable = response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable ||
                            (int)response.StatusCode >= 500;
            if (!retryable || attempt == 8)
            {
                throw new HttpRequestException($"Zerion returned {(int)response.StatusCode}: {body}");
            }

            var delay = response.Headers.RetryAfter?.Delta ??
                        TimeSpan.FromSeconds(Math.Min(30, attempt * 4));
            await Task.Delay(delay, cancellationToken);
            }
        }

        throw new HttpRequestException("Zerion request failed after retries.");
    }

    private static List<ChartPoint> ParseChart(string json)
    {
        using var document = JsonDocument.Parse(json);
        var points = document.RootElement
            .GetProperty("data")
            .GetProperty("attributes")
            .GetProperty("points");

        return points.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.Array && x.GetArrayLength() >= 2)
            .Select(x => new ChartPoint(
                DateTimeOffset.FromUnixTimeSeconds(x[0].GetInt64()).UtcDateTime,
                x[1].GetDecimal()))
            .OrderBy(x => x.Timestamp)
            .ToList();
    }

    private static PnlValues ParsePnl(string json)
    {
        using var document = JsonDocument.Parse(json);
        var attributes = document.RootElement.GetProperty("data").GetProperty("attributes");
        return new PnlValues(
            GetDecimal(attributes, "realized_gain"),
            GetDecimal(attributes, "received_external"),
            GetDecimal(attributes, "sent_external"),
            GetDecimal(attributes, "total_fee"));
    }

    private static ChartPoint NearestPoint(List<ChartPoint> points, DateTime target)
    {
        if (points.Count == 0)
        {
            throw new InvalidOperationException("Zerion chart returned no portfolio points.");
        }

        var utcTarget = target.ToUniversalTime();
        return points.MinBy(point => Math.Abs((point.Timestamp - utcTarget).Ticks))!;
    }

    private static string SelectChartPeriod(DateTime startUtc)
    {
        var age = DateTime.UtcNow - startUtc.ToUniversalTime();
        if (age <= TimeSpan.FromHours(1)) return "hour";
        if (age <= TimeSpan.FromDays(1)) return "day";
        if (age <= TimeSpan.FromDays(7)) return "week";
        if (age <= TimeSpan.FromDays(30)) return "month";
        if (age <= TimeSpan.FromDays(90)) return "3months";
        if (age <= TimeSpan.FromDays(180)) return "6months";
        if (age <= TimeSpan.FromDays(365)) return "year";
        if (age <= TimeSpan.FromDays(365 * 5)) return "5years";
        return "max";
    }

    private static long ToUnixMilliseconds(DateTime value) =>
        new DateTimeOffset(value.ToUniversalTime()).ToUnixTimeMilliseconds();

    private static decimal GetDecimal(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return 0m;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)
            ? number
            : decimal.TryParse(value.ToString(), out number) ? number : 0m;
    }

    private static decimal Round(decimal value) => Math.Round(value, 2);

    private sealed record ChartPoint(DateTime Timestamp, decimal Value);
    private sealed record PnlValues(
        decimal RealizedGain,
        decimal ReceivedExternal,
        decimal SentExternal,
        decimal TotalFee);
}
