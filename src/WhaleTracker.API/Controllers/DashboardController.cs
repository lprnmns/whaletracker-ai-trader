using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using WhaleTracker.Core.Interfaces;
using WhaleTracker.Core.Models;

namespace WhaleTracker.API.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public class DashboardController : ControllerBase
{
    private readonly IOkxService _okxService;
    private readonly IAIService _aiService;
    private readonly ILogger<DashboardController> _logger;
    private readonly IWebHostEnvironment _env;
    private readonly ZerionSettings _zerionSettings;

    public DashboardController(
        IOkxService okxService,
        IAIService aiService,
        ILogger<DashboardController> logger,
        IWebHostEnvironment env,
        IOptions<AppSettings> settings)
    {
        _okxService = okxService;
        _aiService = aiService;
        _logger = logger;
        _env = env;
        _zerionSettings = settings.Value.Zerion;
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetStatus([FromQuery] string? address = null)
    {
        var wallet = ResolveAddress(address);
        var status = LoadWatchStatus(wallet);
        var latest = LoadLatestEvent(wallet);

        var okxAvailable = false;
        decimal? okxBalance = null;
        int okxPositions = 0;
        decimal? okxPnl = null;
        decimal? okxPnlPct = null;

        try
        {
            var stats = await _okxService.GetAccountInfoAsync();
            okxAvailable = true;
            okxBalance = stats.TotalUsd;
            okxPositions = stats.ActivePositions.Count;

            if (status?.StartOkxBalanceUsdt is decimal startBal && startBal > 0 && okxBalance.HasValue)
            {
                okxPnl = okxBalance.Value - startBal;
                okxPnlPct = (okxPnl.Value / startBal) * 100m;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OKX status fetch failed.");
        }

        var nowUtc = DateTimeOffset.UtcNow;
        var heartbeatAt = status?.LastHeartbeat;
        var staleSeconds = heartbeatAt.HasValue ? (nowUtc - heartbeatAt.Value).TotalSeconds : double.MaxValue;
        var threshold = Math.Max(_zerionSettings.PollingIntervalSeconds * 2, 30);
        var pollOk = string.Equals(status?.LastPollStatus, "ok", StringComparison.OrdinalIgnoreCase);
        var isActive = heartbeatAt.HasValue && staleSeconds <= threshold && pollOk;

        var whaleBalance = latest?.WhaleBalanceUsdt;
        if (!whaleBalance.HasValue || whaleBalance <= 0)
        {
            whaleBalance = status?.StartWhaleBalanceUsdt;
        }

        var latestPositions = latest?.WhalePositions;
        if (latestPositions == null || latestPositions.Count == 0)
        {
            latestPositions = status?.StartWhalePositions;
        }

        var whalePositionsCount = latestPositions?
            .Count(p => !string.IsNullOrWhiteSpace(p.Symbol) && p.ValueUsdt is not null) ?? 0;

        return Ok(new
        {
            wallet,
            program = new
            {
                active = isActive,
                startedAt = status?.StartedAt ?? status?.StartedAtLegacy,
                lastHeartbeat = heartbeatAt,
                lastError = status?.LastError,
                staleSeconds,
                startOkxBalance = status?.StartOkxBalanceUsdt,
                startWhaleBalance = status?.StartWhaleBalanceUsdt
            },
            okx = new
            {
                available = okxAvailable,
                balance = okxBalance,
                openPositions = okxPositions,
                pnlUsd = okxPnl,
                pnlPct = okxPnlPct
            },
            whale = new
            {
                balance = whaleBalance,
                lastEvent = latest?.RawEvent,
                positions = whalePositionsCount
            }
        });
    }

    [HttpGet("positions")]
    public async Task<IActionResult> GetPositions([FromQuery] string? address = null)
    {
        var wallet = ResolveAddress(address);
        var status = LoadWatchStatus(wallet);
        var latest = LoadLatestEvent(wallet);

        var okxPositions = new List<object>();
        try
        {
            var stats = await _okxService.GetAccountInfoAsync();
            foreach (var pos in stats.ActivePositions)
            {
                var pnlPct = pos.MarginUsd > 0 ? (pos.UnrealizedPnl / pos.MarginUsd) * 100m : 0m;
                okxPositions.Add(new
                {
                    symbol = pos.Symbol,
                    side = pos.Direction,
                    marginUsd = pos.MarginUsd,
                    entryPrice = pos.EntryPrice,
                    size = pos.Size,
                    pnlUsd = pos.UnrealizedPnl,
                    pnlPct
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OKX positions fetch failed.");
        }

        var whalePositions = new List<WhalePositionRow>();
        var startMap = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        if (status?.StartWhalePositions != null)
        {
            foreach (var pos in status.StartWhalePositions)
            {
                if (string.IsNullOrWhiteSpace(pos.Symbol) || pos.ValueUsdt is null)
                {
                    continue;
                }
                startMap[pos.Symbol] = pos.ValueUsdt.Value;
            }
        }

        var sourcePositions = latest?.WhalePositions;
        if (sourcePositions == null || sourcePositions.Count == 0)
        {
            sourcePositions = status?.StartWhalePositions;
        }

        if (sourcePositions != null)
        {
            foreach (var pos in sourcePositions)
            {
                if (string.IsNullOrWhiteSpace(pos.Symbol) || pos.ValueUsdt is null)
                {
                    continue;
                }

                startMap.TryGetValue(pos.Symbol, out var startValue);
                var pnlUsd = pos.ValueUsdt.Value - startValue;
                decimal? pnlPct = null;
                if (startValue > 0)
                {
                    pnlPct = (pnlUsd / startValue) * 100m;
                }

                whalePositions.Add(new WhalePositionRow
                {
                    Symbol = pos.Symbol ?? string.Empty,
                    Amount = pos.Amount,
                    ValueUsd = pos.ValueUsdt,
                    PnlUsd = pnlUsd,
                    PnlPct = pnlPct
                });
            }
        }

        var whaleTop = whalePositions
            .OrderByDescending(pos => pos.ValueUsd ?? 0m)
            .Take(6)
            .ToList();

        return Ok(new
        {
            wallet,
            okxPositions,
            whalePositions = whaleTop
        });
    }

    [HttpGet("logs")]
    public IActionResult GetLogs([FromQuery] string? address = null, [FromQuery] int limit = 200, [FromQuery] string? level = null)
    {
        var wallet = ResolveAddress(address);
        var logPath = GetLogPath(wallet);

        if (!System.IO.File.Exists(logPath))
        {
            return Ok(new { wallet, logs = Array.Empty<object>() });
        }

        var lines = System.IO.File.ReadAllLines(logPath);
        var entries = new List<LogEntry>();
        foreach (var line in lines.Reverse())
        {
            if (entries.Count >= limit)
            {
                break;
            }
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var entry = JsonSerializer.Deserialize<LogEntry>(line, JsonOptions);
                if (entry == null)
                {
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(level) && !string.Equals(entry.Level, level, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                entries.Add(entry);
            }
            catch
            {
                continue;
            }
        }

        return Ok(new { wallet, logs = entries });
    }

    [HttpGet("benchmarks")]
    public IActionResult GetBenchmarks([FromQuery] string range = "1w")
    {
        var from = GetRangeStart(range);
        var okxSeries = LoadCsvSeries("okx_balance.csv", from);
        var bistSeries = LoadCsvSeries("bist100.csv", from);
        var goldSeries = LoadCsvSeries("gold.csv", from);
        var depositSeries = LoadCsvSeries("deposit.csv", from);

        return Ok(new
        {
            range,
            okx = okxSeries,
            bist100 = bistSeries,
            gold = goldSeries,
            deposit = depositSeries
        });
    }

    [HttpPost("chat")]
    public async Task<IActionResult> Chat([FromQuery] string? address, [FromBody] ChatRequest? request)
    {
        var wallet = ResolveAddress(request?.Address ?? address);
        var question = request?.Question?.Trim();
        if (string.IsNullOrWhiteSpace(question))
        {
            return BadRequest(new { message = "Question is required." });
        }

        var latest = LoadLatestEvent(wallet);
        var positions = latest?.WhalePositions;
        if (positions == null || positions.Count == 0)
        {
            return BadRequest(new { message = "Whale positions not available yet." });
        }

        var distribution = BuildDistribution(positions, includeOther: false, out var distributionText);
        if (distribution.Count == 0)
        {
            return BadRequest(new { message = "No usable whale positions found." });
        }

        var cashPct = distribution.Where(d => d.Symbol == "CASH").Sum(d => d.Percent);
        var majorsPct = distribution.Where(d => d.Symbol is "BTC" or "ETH").Sum(d => d.Percent);
        var altPct = distribution.Where(d => d.Symbol != "CASH" && d.Symbol != "BTC" && d.Symbol != "ETH")
            .Sum(d => d.Percent);

        if (IsPercentForecastQuestion(question))
        {
            var forecast = BuildPercentForecast(cashPct, majorsPct, altPct);
            return Ok(new
            {
                wallet,
                answer = forecast,
                distribution
            });
        }

        var prompt = BuildWhaleChatPrompt(distributionText, cashPct, majorsPct, altPct, question);
        var answer = await _aiService.AskAsync(prompt);
        if (IsRefusalResponse(answer))
        {
            answer = BuildCommentary(distributionText, cashPct, majorsPct, altPct);
        }

        return Ok(new
        {
            wallet,
            answer,
            distribution
        });
    }

    private string ResolveAddress(string? address)
    {
        if (!string.IsNullOrWhiteSpace(address))
        {
            return address;
        }

        if (IsValidAddress(_zerionSettings.WhaleAddress))
        {
            var configured = _zerionSettings.WhaleAddress;
            var configuredStatus = LoadWatchStatus(configured);
            if (IsStatusFresh(configuredStatus))
            {
                return configured;
            }
        }

        var fallback = FindLatestHeartbeatAddress();
        if (!string.IsNullOrWhiteSpace(fallback))
        {
            return fallback;
        }

        return _zerionSettings.WhaleAddress ?? string.Empty;
    }

    private WatchStatus? LoadWatchStatus(string wallet)
    {
        var path = GetHeartbeatPath(wallet);
        if (!System.IO.File.Exists(path))
        {
            return null;
        }
        try
        {
            var raw = System.IO.File.ReadAllText(path);
            return JsonSerializer.Deserialize<WatchStatus>(raw, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private LatestEvent? LoadLatestEvent(string wallet)
    {
        var path = GetLatestEventPath(wallet);
        if (!System.IO.File.Exists(path))
        {
            return null;
        }
        try
        {
            var raw = System.IO.File.ReadAllText(path);
            return JsonSerializer.Deserialize<LatestEvent>(raw, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private List<SeriesPoint> LoadCsvSeries(string fileName, DateTimeOffset from)
    {
        var path = Path.Combine(GetDataRoot(), "benchmarks", fileName);
        if (!System.IO.File.Exists(path))
        {
            return new List<SeriesPoint>();
        }

        var output = new List<SeriesPoint>();
        foreach (var line in System.IO.File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("date", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = line.Split(',', 2);
            if (parts.Length != 2)
            {
                continue;
            }

            if (!TryParseDate(parts[0], out var dt))
            {
                continue;
            }

            if (dt < from)
            {
                continue;
            }

            if (!decimal.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
            {
                continue;
            }

            output.Add(new SeriesPoint
            {
                Timestamp = dt.ToUniversalTime().ToString("o"),
                Value = value
            });
        }

        return output;
    }

    private static bool TryParseDate(string raw, out DateTimeOffset dt)
    {
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out dt))
        {
            return true;
        }

        if (DateTimeOffset.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out dt))
        {
            return true;
        }

        dt = default;
        return false;
    }

    private static DateTimeOffset GetRangeStart(string range)
    {
        var now = DateTimeOffset.UtcNow;
        return range switch
        {
            "1d" => now.AddDays(-1),
            "1w" => now.AddDays(-7),
            "1m" => now.AddMonths(-1),
            "1y" => now.AddYears(-1),
            _ => now.AddDays(-7)
        };
    }

    private string GetDataRoot()
    {
        return Path.GetFullPath(Path.Combine(_env.ContentRootPath, "..", "..", "data"));
    }

    private static bool IsValidAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        var trimmed = address.Trim();
        if (trimmed.Contains("BALINA", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("CUZDAN", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("BURAYA", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private string? FindLatestHeartbeatAddress()
    {
        try
        {
            var dataRoot = GetDataRoot();
            if (!Directory.Exists(dataRoot))
            {
                return null;
            }

            var files = Directory.GetFiles(dataRoot, "zerion_watch_status_*.json");
            if (files.Length == 0)
            {
                return null;
            }

            var latest = files.OrderByDescending(path => System.IO.File.GetLastWriteTimeUtc(path)).First();
            var name = Path.GetFileNameWithoutExtension(latest);
            const string prefix = "zerion_watch_status_";
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var suffix = name.Substring(prefix.Length);
            if (string.IsNullOrWhiteSpace(suffix))
            {
                return null;
            }

            return "0x" + suffix;
        }
        catch
        {
            return null;
        }
    }

    private string SafeAddress(string wallet)
    {
        var safe = wallet.ToLowerInvariant().Replace("0x", "");
        return string.Concat(safe.Where(char.IsLetterOrDigit));
    }

    private string GetHeartbeatPath(string wallet)
    {
        return Path.Combine(GetDataRoot(), $"zerion_watch_status_{SafeAddress(wallet)}.json");
    }

    private string GetLatestEventPath(string wallet)
    {
        return Path.Combine(GetDataRoot(), $"zerion_latest_event_{SafeAddress(wallet)}.json");
    }

    private string GetLogPath(string wallet)
    {
        return Path.Combine(GetDataRoot(), $"zerion_watch_log_{SafeAddress(wallet)}.jsonl");
    }

    private bool IsStatusFresh(WatchStatus? status)
    {
        if (status?.LastHeartbeat is null)
        {
            return false;
        }

        var threshold = Math.Max(_zerionSettings.PollingIntervalSeconds * 2, 30);
        var staleSeconds = (DateTimeOffset.UtcNow - status.LastHeartbeat.Value).TotalSeconds;
        var pollOk = string.Equals(status.LastPollStatus, "ok", StringComparison.OrdinalIgnoreCase);
        return staleSeconds <= threshold && pollOk;
    }

    private static List<DistributionEntry> BuildDistribution(
        IEnumerable<WhalePosition> positions,
        bool includeOther,
        out string distributionText)
    {
        var totals = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        decimal totalValue = 0m;

        foreach (var pos in positions)
        {
            if (string.IsNullOrWhiteSpace(pos.Symbol) || pos.ValueUsdt is null)
            {
                continue;
            }

            var symbol = NormalizeSymbol(pos.Symbol);
            var value = pos.ValueUsdt.Value;
            if (value <= 0)
            {
                continue;
            }

            var key = IsCashSymbol(symbol) ? "CASH" : symbol;
            totals.TryGetValue(key, out var current);
            totals[key] = current + value;
            totalValue += value;
        }

        var ordered = totals
            .Where(kvp => kvp.Value > 0)
            .OrderByDescending(kvp => kvp.Value)
            .ToList();

        var entries = new List<DistributionEntry>();
        if (totalValue <= 0)
        {
            distributionText = string.Empty;
            return entries;
        }

        const int maxItems = 6;
        decimal remainder = 0m;
        for (var i = 0; i < ordered.Count; i++)
        {
            if (i >= maxItems)
            {
                remainder += ordered[i].Value;
                continue;
            }

            var percent = (ordered[i].Value / totalValue) * 100m;
            entries.Add(new DistributionEntry
            {
                Symbol = ordered[i].Key,
                Percent = Math.Round(percent, 2)
            });
        }

        if (includeOther && remainder > 0)
        {
            var remainderPct = Math.Round((remainder / totalValue) * 100m, 2);
            if (remainderPct > 0)
            {
                entries.Add(new DistributionEntry
                {
                    Symbol = "OTHER",
                    Percent = remainderPct
                });
            }
        }

        distributionText = string.Join(", ", entries.Select(entry =>
            $"{FormatDistributionLabel(entry.Symbol)} {entry.Percent:0.##}%"));

        return entries;
    }

    private static string NormalizeSymbol(string symbol)
    {
        var trimmed = symbol.Trim();
        return trimmed.ToUpperInvariant() switch
        {
            "WETH" => "ETH",
            "WBTC" => "BTC",
            _ => trimmed.ToUpperInvariant()
        };
    }

    private static bool IsCashSymbol(string symbol)
    {
        return symbol is "USDT" or "USDC" or "DAI" or "USDP" or "TUSD" or "FDUSD" or "FRAX" or "USDE" or "USD";
    }

    private static string FormatDistributionLabel(string symbol)
    {
        return symbol == "CASH" ? "Nakit (USDT/USDC)" : symbol;
    }

    private static string BuildWhaleChatPrompt(
        string distributionText,
        decimal cashPct,
        decimal majorsPct,
        decimal altPct,
        string question)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Sen WhaleTracker icin balina portfoy dagilimini yorumlayan bir kripto analistsin.");
        sb.AppendLine("Kurallar:");
        sb.AppendLine("- Sadece portfoy dagilimindan cikarim yap.");
        sb.AppendLine("- Risk uyarisi, yatirim tavsiyesi reddi, belirsizlik veya regule uyarisi YAZMA.");
        sb.AppendLine("- Turkce yaz, kisa ve net ol.");
        sb.AppendLine("- Yuzde hareket tahmini, fiyat hedefi veya risk yorumu YAZMA.");
        sb.AppendLine("- Cevapta once en yuksek 6 pozisyonun yuzdesini ver.");
        sb.AppendLine("- Nakit (USDT/USDC) orani yuksekse 'dusus beklentisi yuksek' de.");
        sb.AppendLine("- Coin orani yuksekse 'yukselis beklentisi yuksek' de.");
        sb.AppendLine("- BTC/ETH agirligi belirginse 'major ralli' vurgusu yap.");
        sb.AppendLine("- Altcoin agirligi belirginse 'altcoin rallisi' vurgusu yap.");
        sb.AppendLine("- Format: 'Oranlar: ... | Yorum: ...' seklinde kisa yaz.");
        sb.AppendLine();
        sb.AppendLine($"Portfoy dagilimi (yuzde): {distributionText}");
        sb.AppendLine($"Nakit: %{cashPct:0.##} | BTC+ETH: %{majorsPct:0.##} | Altcoin: %{altPct:0.##}");
        sb.AppendLine();
        sb.AppendLine($"Soru: {question}");
        return sb.ToString();
    }

    private static bool IsPercentForecastQuestion(string question)
    {
        var normalized = NormalizeForMatch(question);
        var hasPercent = normalized.Contains("yuzde") || normalized.Contains("%");
        var hasDirection = normalized.Contains("yuksel") || normalized.Contains("duser") || normalized.Contains("dus");
        var hasQuestion = normalized.Contains("kac") || normalized.Contains("ne kadar");
        return hasPercent && hasDirection && hasQuestion;
    }

    private static string BuildPercentForecast(decimal cashPct, decimal majorsPct, decimal altPct)
    {
        decimal up;
        decimal down;
        if (cashPct >= 60)
        {
            up = 30;
            down = 70;
        }
        else if (majorsPct >= 50 && cashPct < 40)
        {
            up = 70;
            down = 30;
        }
        else if (altPct >= 50)
        {
            up = 75;
            down = 25;
        }
        else
        {
            up = 55;
            down = 45;
        }

        return $"Yukselis: %{up:0}, Dusus: %{down:0}";
    }

    private static string BuildCommentary(string distributionText, decimal cashPct, decimal majorsPct, decimal altPct)
    {
        var comment = new System.Text.StringBuilder();
        if (cashPct >= 50)
        {
            comment.Append("dusus beklentisi yuksek");
        }
        else
        {
            comment.Append("yukselis beklentisi yuksek");
        }

        if (majorsPct >= 35 && majorsPct >= altPct)
        {
            comment.Append(", major ralli");
        }
        else if (altPct >= 35)
        {
            comment.Append(", altcoin rallisi");
        }

        return $"Oranlar: {distributionText} | Yorum: {comment}.";
    }

    private static bool IsRefusalResponse(string answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
        {
            return true;
        }

        var normalized = NormalizeForMatch(answer);
        return normalized.Contains("uzgunum") ||
               normalized.Contains("yardimci olam") ||
               normalized.Contains("yerine getirem") ||
               normalized.Contains("bunu yapamam");
    }

    private static string NormalizeForMatch(string text)
    {
        var lower = text.ToLowerInvariant();
        return lower
            .Replace("ı", "i")
            .Replace("ş", "s")
            .Replace("ğ", "g")
            .Replace("ü", "u")
            .Replace("ö", "o")
            .Replace("ç", "c");
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed class WatchStatus
    {
        public string? Address { get; set; }
        public DateTimeOffset? StartedAt { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("started_at")]
        public DateTimeOffset? StartedAtLegacy { get; set; }
        public DateTimeOffset? LastHeartbeat { get; set; }
        public string? LastError { get; set; }
        public string? LastPollStatus { get; set; }
        public decimal? StartOkxBalanceUsdt { get; set; }
        public decimal? StartWhaleBalanceUsdt { get; set; }
        public List<WhalePosition>? StartWhalePositions { get; set; }
    }

    private sealed class LatestEvent
    {
        public decimal? WhaleBalanceUsdt { get; set; }
        public string? RawEvent { get; set; }
        public List<WhalePosition>? WhalePositions { get; set; }
    }

    private sealed class WhalePosition
    {
        public string? Symbol { get; set; }
        public decimal? Amount { get; set; }
        public decimal? ValueUsdt { get; set; }
    }

    private sealed class WhalePositionRow
    {
        public string Symbol { get; set; } = string.Empty;
        public decimal? Amount { get; set; }
        public decimal? ValueUsd { get; set; }
        public decimal? PnlUsd { get; set; }
        public decimal? PnlPct { get; set; }
    }

    private sealed class LogEntry
    {
        public DateTimeOffset Timestamp { get; set; }
        public string? Level { get; set; }
        public string? Message { get; set; }
        public JsonElement Context { get; set; }
    }

    private sealed class SeriesPoint
    {
        public string Timestamp { get; set; } = string.Empty;
        public decimal Value { get; set; }
    }

    public sealed class ChatRequest
    {
        public string? Address { get; set; }
        public string? Question { get; set; }
    }

    public sealed class DistributionEntry
    {
        public string Symbol { get; set; } = string.Empty;
        public decimal Percent { get; set; }
    }
}
