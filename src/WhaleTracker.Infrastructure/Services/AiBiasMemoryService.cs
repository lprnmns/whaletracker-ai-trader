using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WhaleTracker.Core.Interfaces;
using WhaleTracker.Core.Models;
using WhaleTracker.Data;
using WhaleTracker.Data.Entities;

namespace WhaleTracker.Infrastructure.Services;

public class AiBiasMemoryService : IAiBiasMemoryService
{
    private const string GlobalStateId = "global";
    private const decimal NoiseWalletPercent = 5m;
    private static readonly HashSet<string> StableSymbols = new(StringComparer.OrdinalIgnoreCase)
    {
        "USDT", "USDC", "DAI", "USDE", "SUSDE", "FDUSD", "TUSD"
    };

    private readonly WhaleTrackerDbContext _db;

    public AiBiasMemoryService(WhaleTrackerDbContext db)
    {
        _db = db;
    }

    public async Task<string> BuildPromptMemoryAsync(CancellationToken cancellationToken = default)
    {
        var state = await _db.AiBiasStates
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == GlobalStateId, cancellationToken);

        if (state == null)
        {
            return "MEMORY: Bias is NEUTRAL. No previous whale decision events recorded.";
        }

        var recent = await _db.AiDecisionEvents
            .AsNoTracking()
            .OrderByDescending(x => x.CreatedAt)
            .Take(8)
            .Select(x => new
            {
                x.CreatedAt,
                x.WalletAddress,
                x.MovementType,
                x.Symbol,
                x.MovementUsd,
                x.Action,
                x.BiasDelta,
                x.IgnoredReason
            })
            .ToListAsync(cancellationToken);

        var recentText = recent.Count == 0
            ? "No recent events."
            : string.Join(" | ", recent.Select(x =>
                $"{x.CreatedAt:u} {ShortWallet(x.WalletAddress)} {x.MovementType} {x.Symbol} ${x.MovementUsd:F0} action={x.Action} delta={x.BiasDelta:F1} {x.IgnoredReason}"));

        return $"""
MEMORY:
Current aggregate bias: {state.Direction} ({state.BiasScore:F1}/100).
Symbol weights: {state.SymbolWeightsJson}.
Summary: {state.Summary}
Recent events: {recentText}
Rule reminder: stable-to-stable moves and moves below {NoiseWalletPercent:F0}% of wallet value are weak/noise unless repeated by multiple tracked wallets.
""";
    }

    public async Task RecordDecisionAsync(
        TransactionEvent transaction,
        AIDecision decision,
        string walletAddress,
        decimal walletBalanceUsd,
        CancellationToken cancellationToken = default)
    {
        var state = await _db.AiBiasStates
            .FirstOrDefaultAsync(x => x.Id == GlobalStateId, cancellationToken);

        if (state == null)
        {
            state = new AiBiasStateEntity { Id = GlobalStateId };
            _db.AiBiasStates.Add(state);
        }

        var symbol = NormalizeSymbol(transaction.NormalizedSymbol);
        if (string.IsNullOrWhiteSpace(symbol))
        {
            symbol = NormalizeSymbol(decision.Symbol);
        }

        var ignoredReason = GetIgnoredReason(transaction, decision, walletBalanceUsd);
        var delta = string.IsNullOrWhiteSpace(ignoredReason)
            ? CalculateBiasDelta(transaction, decision, walletBalanceUsd)
            : 0m;

        state.BiasScore = Math.Clamp(state.BiasScore + delta, -100m, 100m);
        state.Direction = DirectionFromScore(state.BiasScore);
        state.SymbolWeightsJson = UpdateSymbolWeights(state.SymbolWeightsJson, symbol, delta);
        state.EventCount++;
        state.LastEventAt = DateTime.UtcNow;
        state.UpdatedAt = DateTime.UtcNow;
        state.Summary = BuildSummary(state, symbol, delta, decision.Action, ignoredReason);

        _db.AiDecisionEvents.Add(new AiDecisionEventEntity
        {
            TxHash = transaction.TxHash,
            WalletAddress = walletAddress.Trim().ToLowerInvariant(),
            MovementType = transaction.TransactionType,
            Symbol = symbol,
            MovementUsd = transaction.UsdValue,
            WalletBalanceUsd = walletBalanceUsd,
            Action = decision.Action,
            ShouldTrade = decision.ShouldTrade,
            Confidence = decision.ConfidenceScore,
            BiasDelta = delta,
            BiasScoreAfter = state.BiasScore,
            IgnoredReason = ignoredReason,
            Reasoning = decision.Reasoning
        });

        await _db.SaveChangesAsync(cancellationToken);
    }

    private static decimal CalculateBiasDelta(
        TransactionEvent transaction,
        AIDecision decision,
        decimal walletBalanceUsd)
    {
        var valueScore = walletBalanceUsd > 0
            ? Math.Clamp((transaction.UsdValue / walletBalanceUsd) * 100m, 1m, 20m)
            : Math.Clamp(transaction.UsdValue / 10_000m, 1m, 20m);

        var confidenceMultiplier = Math.Clamp(decision.ConfidenceScore <= 0 ? 0.6m : decision.ConfidenceScore / 100m, 0.25m, 1m);
        var baseDelta = valueScore * confidenceMultiplier;

        return decision.Action.ToUpperInvariant() switch
        {
            "LONG" or "OPEN_LONG" => baseDelta,
            "CLOSE_LONG" or "SELL" or "CLOSE" => -baseDelta,
            _ when string.Equals(transaction.Direction, "Incoming", StringComparison.OrdinalIgnoreCase) => baseDelta * 0.5m,
            _ when string.Equals(transaction.Direction, "Outgoing", StringComparison.OrdinalIgnoreCase) => -baseDelta * 0.5m,
            _ => 0m
        };
    }

    private static string GetIgnoredReason(
        TransactionEvent transaction,
        AIDecision decision,
        decimal walletBalanceUsd)
    {
        var symbol = NormalizeSymbol(transaction.NormalizedSymbol);
        if (string.IsNullOrWhiteSpace(symbol))
        {
            symbol = NormalizeSymbol(decision.Symbol);
        }

        if (StableSymbols.Contains(symbol))
        {
            return "stable/no directional symbol";
        }

        if (walletBalanceUsd > 0)
        {
            var pct = transaction.UsdValue / walletBalanceUsd * 100m;
            if (pct < NoiseWalletPercent)
            {
                return $"below {NoiseWalletPercent:F0}% wallet threshold ({pct:F2}%)";
            }
        }

        if (!decision.ShouldTrade && string.Equals(decision.Action, "IGNORE", StringComparison.OrdinalIgnoreCase))
        {
            return "AI ignored movement";
        }

        return string.Empty;
    }

    private static string UpdateSymbolWeights(string json, string symbol, decimal delta)
    {
        if (string.IsNullOrWhiteSpace(symbol) || delta == 0)
        {
            return string.IsNullOrWhiteSpace(json) ? "{}" : json;
        }

        Dictionary<string, decimal> weights;
        try
        {
            weights = JsonSerializer.Deserialize<Dictionary<string, decimal>>(json) ?? new();
        }
        catch
        {
            weights = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        }

        weights[symbol] = Math.Clamp(weights.GetValueOrDefault(symbol) + delta, -100m, 100m);
        weights = weights
            .Where(x => Math.Abs(x.Value) >= 0.5m)
            .OrderByDescending(x => Math.Abs(x.Value))
            .Take(20)
            .ToDictionary(x => x.Key, x => Math.Round(x.Value, 2));

        return JsonSerializer.Serialize(weights);
    }

    private static string BuildSummary(
        AiBiasStateEntity state,
        string symbol,
        decimal delta,
        string action,
        string ignoredReason)
    {
        if (!string.IsNullOrWhiteSpace(ignoredReason))
        {
            return $"Last event did not move bias: {ignoredReason}. Aggregate remains {state.Direction}.";
        }

        var moved = delta >= 0 ? "increased" : "decreased";
        return $"Last {action} event on {symbol} {moved} aggregate bias by {Math.Abs(delta):F1}. Aggregate is {state.Direction}.";
    }

    private static string DirectionFromScore(decimal score)
    {
        return score switch
        {
            >= 25m => "STRONGLY_BULLISH",
            >= 7m => "BULLISH",
            <= -25m => "STRONGLY_BEARISH",
            <= -7m => "BEARISH",
            _ => "NEUTRAL"
        };
    }

    private static string NormalizeSymbol(string? symbol)
    {
        var normalized = (symbol ?? string.Empty).Trim().ToUpperInvariant();
        return normalized switch
        {
            "WETH" => "ETH",
            "WBTC" => "BTC",
            "USDC.E" => "USDC",
            _ => normalized
        };
    }

    private static string ShortWallet(string wallet)
    {
        return wallet.Length < 12 ? wallet : $"{wallet[..6]}...{wallet[^4..]}";
    }
}
