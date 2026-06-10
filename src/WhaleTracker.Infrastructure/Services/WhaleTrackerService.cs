using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WhaleTracker.Core.Interfaces;
using WhaleTracker.Core.Models;
using WhaleTracker.Data.Entities;
using WhaleTracker.Data.Repositories;

namespace WhaleTracker.Infrastructure.Services;

/// <summary>
/// Ana Orkestrasyon Servisi
/// Tüm akışı yönetir: Zerion -> AI -> OKX -> Database
/// 
/// Background Service olarak çalışır
/// </summary>
public class WhaleTrackerService : BackgroundService, IWhaleTrackerService
{
    private readonly IZerionService _zerionService;
    private readonly IOkxService _okxService;
    private readonly IAIService _aiService;
    private readonly ITradeRepository _tradeRepository;
    private readonly ILogger<WhaleTrackerService> _logger;
    private readonly AppSettings _settings;

    public WhaleTrackerService(
        IZerionService zerionService,
        IOkxService okxService,
        IAIService aiService,
        ITradeRepository tradeRepository,
        ILogger<WhaleTrackerService> logger,
        IOptions<AppSettings> settings)
    {
        _zerionService = zerionService;
        _okxService = okxService;
        _aiService = aiService;
        _tradeRepository = tradeRepository;
        _logger = logger;
        _settings = settings.Value;
    }

    /// <summary>
    /// Background service ana döngüsü
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("WhaleTracker servisi başlatıldı");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ScanAndProcessAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ScanAndProcessAsync hatası!");
            }

            // Belirlenen aralıkta bekle
            await Task.Delay(
                TimeSpan.FromSeconds(_settings.Zerion.PollingIntervalSeconds),
                stoppingToken);
        }

        _logger.LogInformation("WhaleTracker servisi durduruluyor");
    }

    /// <summary>
    /// Balina cüzdanını tara ve yeni işlemleri işle
    /// </summary>
    public async Task ScanAndProcessAsync()
    {
        if (string.IsNullOrWhiteSpace(_settings.Zerion.WhaleAddress))
        {
            _logger.LogWarning("Zerion:WhaleAddress boş. Tarama atlandı.");
            return;
        }

        var transactions = await _zerionService.GetRecentTransactionsAsync(
            _settings.Zerion.WhaleAddress,
            limit: 10);

        foreach (var transaction in transactions.OrderBy(x => x.BlockTimestamp))
        {
            if (string.IsNullOrWhiteSpace(transaction.TxHash))
            {
                continue;
            }

            if (await _tradeRepository.IsTransactionProcessedAsync(transaction.TxHash))
            {
                continue;
            }

            var signal = await ProcessTransactionAsync(transaction);
            await _tradeRepository.MarkTransactionProcessedAsync(
                transaction.TxHash,
                string.Equals(signal.Decision, "TRADE", StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Tek bir işlemi işle
    /// </summary>
    public async Task<TradeSignal> ProcessTransactionAsync(TransactionEvent transaction)
    {
        _logger.LogInformation(
            "ProcessTransactionAsync çağrıldı: {TxHash}",
            transaction.TxHash);

        var whaleAddress = _settings.Zerion.WhaleAddress;
        var whaleStats = string.IsNullOrWhiteSpace(whaleAddress)
            ? new WhaleStats()
            : await _zerionService.GetWalletPortfolioAsync(whaleAddress);

        var userStats = await _okxService.GetAccountInfoAsync();
        var aiDecision = await _aiService.AnalyzeMovementAsync(BuildAiContext(whaleStats, userStats, transaction));
        var signal = MapDecisionToSignal(aiDecision, transaction);

        TradeResult? result = null;
        if (string.Equals(signal.Decision, "TRADE", StringComparison.OrdinalIgnoreCase))
        {
            result = await _okxService.ExecuteTradeAsync(signal);
        }

        await _tradeRepository.SaveTradeLogAsync(new TradeLogEntity
        {
            WhaleTxHash = transaction.TxHash,
            OkxOrderId = result?.OrderId,
            Symbol = signal.Symbol,
            Action = signal.Action,
            Leverage = signal.Leverage,
            MarginUsdt = signal.MarginAmountUSDT,
            ExecutedPrice = result?.ExecutedPrice,
            IsSuccess = result?.Success ?? signal.Decision == "IGNORE",
            ErrorMessage = result?.ErrorMessage,
            Confidence = signal.TradeConfidence,
            AiReason = signal.Reason
        });

        return signal;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("WhaleTracker başlatılıyor...");
        return base.StartAsync(cancellationToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("WhaleTracker durduruluyor...");
        return base.StopAsync(cancellationToken);
    }

    private static AIContext BuildAiContext(WhaleStats whaleStats, UserStats userStats, TransactionEvent transaction)
    {
        return new AIContext
        {
            OurBalanceUSDT = userStats.TotalUsd,
            WhaleBalanceUSDT = whaleStats.TotalUsd,
            OurPositions = userStats.ActivePositions.Select(x => new OurPosition
            {
                Symbol = x.Symbol,
                Direction = x.Direction,
                MarginUSDT = x.MarginUsd,
                EntryPrice = x.EntryPrice,
                UnrealizedPnL = x.UnrealizedPnl,
                Leverage = userStats.Leverage
            }).ToList(),
            NewMovement = new WhaleMovement
            {
                TxHash = transaction.TxHash,
                Chain = transaction.Chain,
                Type = NormalizeMovementType(transaction),
                Symbol = transaction.NormalizedSymbol,
                Amount = transaction.Amount,
                ValueUSDT = transaction.UsdValue,
                Timestamp = transaction.BlockTimestamp,
                RawText = $"{transaction.Direction} {transaction.Amount} {transaction.TokenSymbol} (${transaction.UsdValue}) via {transaction.TransactionType}"
            }
        };
    }

    private static TradeSignal MapDecisionToSignal(AIDecision decision, TransactionEvent transaction)
    {
        var action = decision.Action.ToUpperInvariant() switch
        {
            "LONG" => TradeAction.OPEN_LONG,
            "BUY" => TradeAction.OPEN_LONG,
            "SELL" => TradeAction.CLOSE_LONG,
            "SHORT" => TradeAction.CLOSE_LONG,
            "CLOSE" => TradeAction.CLOSE_LONG,
            "CLOSE_LONG" => TradeAction.CLOSE_LONG,
            "OPEN_LONG" => TradeAction.OPEN_LONG,
            _ => TradeAction.IGNORE
        };

        var shouldTrade = decision.ShouldTrade &&
                          action != TradeAction.IGNORE &&
                          decision.AmountUSDT > 0 &&
                          !string.IsNullOrWhiteSpace(decision.Symbol);

        return new TradeSignal
        {
            Decision = shouldTrade ? "TRADE" : "IGNORE",
            Action = shouldTrade ? action : TradeAction.IGNORE,
            Symbol = NormalizeSymbol(decision.Symbol, transaction.NormalizedSymbol),
            MarginAmountUSDT = decision.AmountUSDT,
            Leverage = decision.Leverage,
            TradeConfidence = decision.ConfidenceScore,
            Reason = string.IsNullOrWhiteSpace(decision.Reasoning)
                ? decision.ParseError ?? "AI decision produced no reasoning."
                : decision.Reasoning,
            SourceTxHash = transaction.TxHash
        };
    }

    private static string NormalizeMovementType(TransactionEvent transaction)
    {
        if (string.Equals(transaction.Direction, "Incoming", StringComparison.OrdinalIgnoreCase))
        {
            return "BUY";
        }

        if (string.Equals(transaction.Direction, "Outgoing", StringComparison.OrdinalIgnoreCase))
        {
            return "SELL";
        }

        return string.IsNullOrWhiteSpace(transaction.TransactionType)
            ? "TRADE"
            : transaction.TransactionType.ToUpperInvariant();
    }

    private static string NormalizeSymbol(string preferred, string fallback)
    {
        var symbol = string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;
        return symbol.ToUpperInvariant() switch
        {
            "WETH" => "ETH",
            "WBTC" => "BTC",
            "USDC" => "USDT",
            _ => symbol.ToUpperInvariant()
        };
    }
}
