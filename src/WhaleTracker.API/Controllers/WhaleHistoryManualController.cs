using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Globalization;
using System.Text.Json;
using WhaleTracker.Core.Interfaces;
using WhaleTracker.Core.Models;

namespace WhaleTracker.API.Controllers;

[ApiController]
[Authorize]
[Route("api/test/whale-history")]
public class WhaleHistoryManualController : ControllerBase
{
    private readonly IOkxService _okxService;
    private readonly IAIService _aiService;
    private readonly ILogger<WhaleHistoryManualController> _logger;

    public WhaleHistoryManualController(
        IOkxService okxService,
        IAIService aiService,
        ILogger<WhaleHistoryManualController> logger)
    {
        _okxService = okxService;
        _aiService = aiService;
        _logger = logger;
    }

    [HttpPost("ai")]
    public async Task<IActionResult> AnalyzeRawEvent([FromBody] JsonElement request)
    {
        if (request.ValueKind != JsonValueKind.Object)
        {
            return BadRequest(new { Error = "Request body must be a JSON object." });
        }

        var rawEvent = GetString(request, "RawEvent");
        if (string.IsNullOrWhiteSpace(rawEvent))
        {
            return BadRequest(new { Error = "RawEvent is required." });
        }

        if (rawEvent.Length > 4000)
        {
            return BadRequest(new { Error = "RawEvent too large. Please shorten the payload." });
        }

        decimal ourBalance = 0m;
        List<OurPosition> ourPositions = new();
        var usedSnapshot = false;

        var useSnapshot = GetBool(request, "UseSnapshot");
        var whaleBalance = GetDecimal(request, "WhaleBalanceUSDT", 100000m);
        var txHash = GetString(request, "TxHash");

        if (useSnapshot)
        {
            usedSnapshot = true;
            ourBalance = GetDecimal(request, "OurBalanceUSDT", 0m);
            ourPositions = ParsePositions(request);
        }
        else
        {
            try
            {
                var userStats = await _okxService.GetAccountInfoAsync();
                var positions = userStats.ActivePositions;

                ourBalance = userStats.TotalUsd;
                ourPositions = positions.Select(p => new OurPosition
                {
                    Symbol = p.Symbol,
                    Direction = p.Direction,
                    MarginUSDT = p.MarginUsd,
                    Leverage = p.MarginUsd > 0 && p.EntryPrice > 0
                        ? (int)Math.Ceiling((p.Size * p.EntryPrice) / p.MarginUsd)
                        : 3,
                    EntryPrice = p.EntryPrice,
                    UnrealizedPnL = p.UnrealizedPnl
                }).ToList();
            }
            catch (Exception ex)
            {
                var snapshotBalance = GetDecimal(request, "OurBalanceUSDT", 0m);
                var snapshotPositions = ParsePositions(request);

                if (snapshotBalance > 0m || snapshotPositions.Count > 0)
                {
                    usedSnapshot = true;
                    ourBalance = snapshotBalance;
                    ourPositions = snapshotPositions;
                    _logger.LogWarning(ex, "OKX unavailable, using provided snapshot.");
                }
                else
                {
                    _logger.LogError(ex, "Manual AI analyze failed to fetch OKX state.");
                    return Ok(new
                    {
                        Error = $"OKX unavailable: {ex.Message}",
                        Context = new
                        {
                            BalanceUSDT = ourBalance,
                            WhaleBalanceUSDT = whaleBalance,
                            Positions = ourPositions.Count,
                            UsedSnapshot = usedSnapshot
                        },
                        Decision = new
                        {
                            Action = "IGNORE",
                            Symbol = string.Empty,
                            AmountUSDT = 0m,
                            Leverage = 3,
                            ConfidenceScore = 0,
                            Reasoning = "OKX unavailable; AI skipped.",
                            ShouldTrade = false,
                            ParseSuccess = false,
                            ParseError = ex.Message,
                            RawResponse = string.Empty
                        }
                    });
                }
            }
        }

        var movement = new WhaleMovement
        {
            Type = "RAW",
            RawText = rawEvent,
            TxHash = string.IsNullOrWhiteSpace(txHash)
                ? $"0x_raw_{Guid.NewGuid():N}"[..12]
                : txHash,
            Timestamp = DateTime.UtcNow
        };

        var context = new AIContext
        {
            OurBalanceUSDT = ourBalance,
            WhaleBalanceUSDT = whaleBalance,
            NewMovement = movement,
            OurPositions = ourPositions
        };

        _logger.LogInformation("Manual AI analyze: balance={Balance} positions={Count}",
            context.OurBalanceUSDT, context.OurPositions.Count);

        AIDecision decision;
        try
        {
            decision = await _aiService.AnalyzeMovementAsync(context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Manual AI analyze failed.");
            decision = new AIDecision
            {
                Action = "IGNORE",
                Symbol = string.Empty,
                AmountUSDT = 0m,
                Leverage = 3,
                ConfidenceScore = 0,
                Reasoning = $"AI error: {ex.Message}",
                ShouldTrade = false,
                ParseSuccess = false,
                ParseError = ex.Message,
                RawResponse = string.Empty
            };
        }

        return Ok(new
        {
            Context = new
            {
                BalanceUSDT = context.OurBalanceUSDT,
                WhaleBalanceUSDT = context.WhaleBalanceUSDT,
                Positions = context.OurPositions.Count,
                UsedSnapshot = usedSnapshot
            },
            Decision = new
            {
                decision.Action,
                decision.Symbol,
                decision.AmountUSDT,
                decision.Leverage,
                decision.ConfidenceScore,
                decision.Reasoning,
                decision.ShouldTrade,
                decision.ParseSuccess,
                decision.ParseError,
                decision.RawResponse
            }
        });
    }

    private static string GetString(JsonElement root, string name)
    {
        if (root.TryGetProperty(name, out var element))
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                return element.GetString() ?? string.Empty;
            }
            return element.ToString();
        }

        return string.Empty;
    }

    private static bool GetBool(JsonElement root, string name)
    {
        if (root.TryGetProperty(name, out var element))
        {
            if (element.ValueKind == JsonValueKind.True) return true;
            if (element.ValueKind == JsonValueKind.False) return false;
            if (element.ValueKind == JsonValueKind.String && bool.TryParse(element.GetString(), out var parsed))
            {
                return parsed;
            }
        }

        return false;
    }

    private static decimal GetDecimal(JsonElement root, string name, decimal fallback)
    {
        if (root.TryGetProperty(name, out var element))
        {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out var num))
            {
                return num;
            }

            if (element.ValueKind == JsonValueKind.String)
            {
                var text = element.GetString() ?? string.Empty;
                text = text.Replace(',', '.');
                if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                {
                    return parsed;
                }
            }
        }

        return fallback;
    }

    private static List<OurPosition> ParsePositions(JsonElement root)
    {
        var positions = new List<OurPosition>();
        if (!root.TryGetProperty("OurPositions", out var element))
        {
            return positions;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            return positions;
        }

        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var position = new OurPosition
            {
                Symbol = GetString(item, "Symbol"),
                Direction = GetString(item, "Direction"),
                MarginUSDT = GetDecimal(item, "MarginUSDT", 0m),
                EntryPrice = GetDecimal(item, "EntryPrice", 0m),
                UnrealizedPnL = GetDecimal(item, "UnrealizedPnL", 0m),
                Leverage = (int)GetDecimal(item, "Leverage", 3m)
            };

            if (!string.IsNullOrWhiteSpace(position.Symbol))
            {
                positions.Add(position);
            }
        }

        return positions;
    }

    [HttpPost("execute")]
    public async Task<IActionResult> ExecuteDecision([FromBody] WhaleHistoryExecuteRequest request)
    {
        if (request == null)
        {
            return BadRequest(new { Error = "Request body is required." });
        }

        var action = (request.Action ?? string.Empty).Trim().ToUpperInvariant();
        var normalizedSymbol = (request.Symbol ?? string.Empty).Trim().ToUpperInvariant();
        normalizedSymbol = normalizedSymbol switch
        {
            "WETH" => "ETH",
            "WBTC" => "BTC",
            "USDC" => "USDT",
            _ => normalizedSymbol
        };

        var mappedAction = action switch
        {
            "LONG" => TradeAction.OPEN_LONG,
            "OPEN_LONG" => TradeAction.OPEN_LONG,
            "CLOSE_LONG" => TradeAction.CLOSE_LONG,
            "SHORT" => TradeAction.CLOSE_LONG,
            "OPEN_SHORT" => TradeAction.OPEN_SHORT,
            "CLOSE_SHORT" => TradeAction.CLOSE_SHORT,
            _ => TradeAction.IGNORE
        };

        if (mappedAction == TradeAction.IGNORE)
        {
            return Ok(new { Status = "IGNORED", Reason = $"Action not tradable: {request.Action}" });
        }

        if (string.IsNullOrWhiteSpace(normalizedSymbol))
        {
            return BadRequest(new { Error = "Symbol is required." });
        }

        if (!await _okxService.IsSymbolSupportedAsync(normalizedSymbol))
        {
            return Ok(new { Status = "IGNORED", Reason = $"Symbol not supported: {normalizedSymbol}" });
        }

        if (request.AmountUSDT <= 0)
        {
            return Ok(new { Status = "IGNORED", Reason = "AmountUSDT <= 0" });
        }

        var signal = new TradeSignal
        {
            Decision = "TRADE",
            Reason = request.Reasoning ?? "Manual test",
            Symbol = normalizedSymbol,
            Action = mappedAction,
            Leverage = request.Leverage > 0 ? request.Leverage : 3,
            MarginAmountUSDT = request.AmountUSDT,
            TradeConfidence = 0,
            SourceTxHash = request.SourceTxHash ?? string.Empty
        };

        var result = await _okxService.ExecuteTradeAsync(signal);

        return Ok(new
        {
            Result = new
            {
                result.Success,
                result.OrderId,
                result.Symbol,
                result.Side,
                result.Size,
                result.ErrorMessage
            }
        });
    }
}

public class WhaleHistoryAiRequest
{
    public string RawEvent { get; set; } = string.Empty;
    public decimal WhaleBalanceUSDT { get; set; } = 100000m;
    public string? TxHash { get; set; }
    public bool UseSnapshot { get; set; }
    public decimal? OurBalanceUSDT { get; set; }
    public List<OurPosition>? OurPositions { get; set; }
}

public class WhaleHistoryExecuteRequest
{
    public string Action { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public decimal AmountUSDT { get; set; }
    public int Leverage { get; set; } = 3;
    public string Reasoning { get; set; } = string.Empty;
    public string? SourceTxHash { get; set; }
}
