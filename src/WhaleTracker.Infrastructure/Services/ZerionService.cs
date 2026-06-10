using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WhaleTracker.Core.Interfaces;
using WhaleTracker.Core.Models;

namespace WhaleTracker.Infrastructure.Services;

/// <summary>
/// Zerion API Servisi
/// Balina cüzdanını takip eder
/// 
/// API Dokümantasyonu: https://developers.zerion.io/
/// </summary>
public class ZerionService : IZerionService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ZerionService> _logger;
    private readonly ZerionSettings _settings;

    private const string BASE_URL = "https://api.zerion.io/v1";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly HashSet<string> StableSymbols = new(StringComparer.OrdinalIgnoreCase)
    {
        "USDT", "USDC", "DAI", "USDE", "SUSDE", "FDUSD", "TUSD", "PYUSD"
    };

    public ZerionService(
        HttpClient httpClient,
        ILogger<ZerionService> logger,
        IOptions<AppSettings> settings)
    {
        _httpClient = httpClient;
        _logger = logger;
        _settings = settings.Value.Zerion;

        _httpClient.BaseAddress = new Uri(BASE_URL);

        if (!string.IsNullOrWhiteSpace(_settings.ApiKey))
        {
            var basicToken = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_settings.ApiKey}:"));
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basicToken);
        }
    }

    /// <summary>
    /// Balinanın güncel portföyünü çeker
    /// </summary>
    public async Task<WhaleStats> GetWalletPortfolioAsync(string walletAddress)
    {
        _logger.LogInformation("GetWalletPortfolioAsync çağrıldı: {Address}", walletAddress);

        EnsureConfigured();

        var portfolioJson = await GetJsonAsync($"/wallets/{walletAddress}/portfolio/?currency=usd");
        var totalUsd = ExtractPortfolioTotal(portfolioJson);
        var holdings = await GetWalletHoldingsAsync(walletAddress);

        return new WhaleStats
        {
            TotalUsd = totalUsd,
            Holdings = holdings
        };
    }

    /// <summary>
    /// Balinanın son işlemlerini çeker
    /// </summary>
    public async Task<List<TransactionEvent>> GetRecentTransactionsAsync(string walletAddress, int limit = 20)
    {
        _logger.LogInformation("GetRecentTransactionsAsync çağrıldı: {Address}, Limit: {Limit}", walletAddress, limit);

        EnsureConfigured();
        limit = Math.Clamp(limit, 1, 100);

        var json = await GetJsonAsync($"/wallets/{walletAddress}/transactions/?currency=usd&page[size]={limit}");
        using var doc = JsonDocument.Parse(json);
        if (!TryGetProperty(doc.RootElement, "data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return new List<TransactionEvent>();
        }

        var events = new List<TransactionEvent>();
        foreach (var item in data.EnumerateArray())
        {
            var tx = ParseTransaction(item);
            if (tx != null)
            {
                events.Add(tx);
            }
        }

        return events;
    }

    /// <summary>
    /// Belirli bir işlemin detayını çeker
    /// </summary>
    public async Task<TransactionEvent?> GetTransactionDetailAsync(string txHash)
    {
        _logger.LogInformation("GetTransactionDetailAsync çağrıldı: {TxHash}", txHash);

        EnsureConfigured();

        var json = await GetJsonAsync($"/transactions/{txHash}/?currency=usd");
        using var doc = JsonDocument.Parse(json);
        if (!TryGetProperty(doc.RootElement, "data", out var data))
        {
            return null;
        }

        return ParseTransaction(data);
    }

    // ================================================================
    // YARDIMCI METODLAR (İstersen kullan)
    // ================================================================

    /// <summary>
    /// Token sembolünü normalize et (WETH -> ETH)
    /// </summary>
    private async Task<string> GetJsonAsync(string requestUri)
    {
        var response = await _httpClient.GetAsync(requestUri);
        var json = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Zerion API error {Status}: {Body}", response.StatusCode, json);
            throw new HttpRequestException($"Zerion API returned {(int)response.StatusCode} for {requestUri}");
        }

        return json;
    }

    private async Task<List<Holding>> GetWalletHoldingsAsync(string walletAddress)
    {
        var json = await GetJsonAsync($"/wallets/{walletAddress}/positions/?currency=usd&filter[trash]=only_non_trash&sort=value&page[size]=25");
        using var doc = JsonDocument.Parse(json);

        if (!TryGetProperty(doc.RootElement, "data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return new List<Holding>();
        }

        var holdings = new List<Holding>();
        foreach (var position in data.EnumerateArray())
        {
            if (!TryGetProperty(position, "attributes", out var attributes))
            {
                continue;
            }

            var symbol = GetString(attributes, "fungible_info", "symbol");
            if (string.IsNullOrWhiteSpace(symbol))
            {
                continue;
            }

            holdings.Add(new Holding
            {
                Symbol = NormalizeSymbol(symbol),
                Amount = GetDecimal(attributes, "quantity", "float"),
                UsdValue = GetDecimal(attributes, "value")
            });
        }

        return holdings
            .Where(x => x.UsdValue > 0)
            .OrderByDescending(x => x.UsdValue)
            .ToList();
    }

    private static decimal ExtractPortfolioTotal(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        return GetDecimal(root, "data", "attributes", "total", "positions") is var positions && positions > 0
            ? positions
            : GetDecimal(root, "data", "attributes", "total", "value");
    }

    private TransactionEvent? ParseTransaction(JsonElement item)
    {
        if (!TryGetProperty(item, "attributes", out var attributes))
        {
            return null;
        }

        var transfers = TryGetProperty(attributes, "transfers", out var transferArray) &&
                        transferArray.ValueKind == JsonValueKind.Array
            ? transferArray.EnumerateArray().Select(ParseTransfer).Where(x => x != null).Cast<ParsedTransfer>().ToList()
            : new List<ParsedTransfer>();

        if (transfers.Count == 0)
        {
            return null;
        }

        var selected = SelectSignalTransfer(transfers);
        if (selected == null)
        {
            return null;
        }

        var chain = GetString(item, "relationships", "chain", "data", "id");
        var operationType = GetString(attributes, "operation_type");
        var txHash = GetString(attributes, "hash");
        var minedAt = GetString(attributes, "mined_at");

        return new TransactionEvent
        {
            TxHash = string.IsNullOrWhiteSpace(txHash) ? GetString(item, "id") : txHash,
            Chain = chain,
            Direction = selected.Direction,
            TokenSymbol = selected.Symbol,
            NormalizedSymbol = NormalizeSymbol(selected.Symbol),
            Amount = selected.Amount,
            UsdValue = selected.ValueUsd,
            BlockTimestamp = DateTime.TryParse(minedAt, out var parsed) ? parsed.ToUniversalTime() : DateTime.UtcNow,
            TransactionType = operationType
        };
    }

    private static ParsedTransfer? ParseTransfer(JsonElement transfer)
    {
        var symbol = GetString(transfer, "fungible_info", "symbol");
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return null;
        }

        return new ParsedTransfer
        {
            Symbol = symbol,
            RawDirection = GetString(transfer, "direction"),
            Amount = GetDecimal(transfer, "quantity", "float"),
            ValueUsd = GetDecimal(transfer, "value")
        };
    }

    private static ParsedTransfer? SelectSignalTransfer(List<ParsedTransfer> transfers)
    {
        var incoming = transfers.Where(x => IsIncoming(x.RawDirection)).OrderByDescending(x => x.ValueUsd).ToList();
        var outgoing = transfers.Where(x => IsOutgoing(x.RawDirection)).OrderByDescending(x => x.ValueUsd).ToList();

        if (incoming.Count > 0 && outgoing.Count > 0)
        {
            var inTop = incoming[0];
            var outTop = outgoing[0];
            var inStable = StableSymbols.Contains(inTop.Symbol);
            var outStable = StableSymbols.Contains(outTop.Symbol);

            if (outStable && !inStable)
            {
                inTop.Direction = "Incoming";
                return inTop;
            }

            if (!outStable && inStable)
            {
                outTop.Direction = "Outgoing";
                return outTop;
            }
        }

        var selected = transfers.OrderByDescending(x => x.ValueUsd).FirstOrDefault();
        if (selected == null)
        {
            return null;
        }

        selected.Direction = IsIncoming(selected.RawDirection) ? "Incoming" : "Outgoing";
        return selected;
    }

    private static bool IsIncoming(string direction)
    {
        return string.Equals(direction, "in", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(direction, "incoming", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOutgoing(string direction)
    {
        return string.Equals(direction, "out", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(direction, "outgoing", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(propertyName, out value);
    }

    private static string GetString(JsonElement element, params string[] path)
    {
        if (!TryWalk(element, out var value, path))
        {
            return string.Empty;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ToString();
    }

    private static decimal GetDecimal(JsonElement element, params string[] path)
    {
        if (!TryWalk(element, out var value, path))
        {
            return 0m;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
            JsonValueKind.String when decimal.TryParse(value.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0m
        };
    }

    private static bool TryWalk(JsonElement element, out JsonElement value, params string[] path)
    {
        value = element;
        foreach (var segment in path)
        {
            if (!TryGetProperty(value, segment, out value))
            {
                return false;
            }
        }

        return true;
    }

    private void EnsureConfigured()
    {
        if (string.IsNullOrWhiteSpace(_settings.ApiKey))
        {
            throw new InvalidOperationException("Zerion API key is not configured.");
        }
    }

    private static string NormalizeSymbol(string symbol)
    {
        // Wrapped tokenları düzelt
        var wrappedTokens = new Dictionary<string, string>
        {
            { "WETH", "ETH" },
            { "WBTC", "BTC" },
            { "WMATIC", "MATIC" },
            { "WAVAX", "AVAX" }
        };

        return wrappedTokens.TryGetValue(symbol.ToUpper(), out var normalized) 
            ? normalized 
            : symbol.ToUpper();
    }

    /// <summary>
    /// OKX'te işlem yapılabilir mi kontrol et
    /// </summary>
    private sealed class ParsedTransfer
    {
        public string Symbol { get; init; } = string.Empty;
        public string RawDirection { get; init; } = string.Empty;
        public string Direction { get; set; } = string.Empty;
        public decimal Amount { get; init; }
        public decimal ValueUsd { get; init; }
    }
}
