using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using WhaleTracker.Core.Interfaces;
using WhaleTracker.Core.Models;
using WhaleTracker.Data;
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
    private readonly IWalletActivityService _walletActivityService;
    private readonly ITradeRepository _tradeRepository;
    private readonly IAiBiasMemoryService _biasMemoryService;
    private readonly INotificationService _notificationService;
    private readonly ILiveEventPublisher _liveEvents;
    private readonly WhaleTrackerDbContext _db;
    private readonly ILogger<WhaleTrackerService> _logger;
    private readonly AppSettings _settings;

    public WhaleTrackerService(
        IZerionService zerionService,
        IOkxService okxService,
        IAIService aiService,
        IWalletActivityService walletActivityService,
        ITradeRepository tradeRepository,
        IAiBiasMemoryService biasMemoryService,
        INotificationService notificationService,
        ILiveEventPublisher liveEvents,
        WhaleTrackerDbContext db,
        ILogger<WhaleTrackerService> logger,
        IOptions<AppSettings> settings)
    {
        _zerionService = zerionService;
        _okxService = okxService;
        _aiService = aiService;
        _walletActivityService = walletActivityService;
        _tradeRepository = tradeRepository;
        _biasMemoryService = biasMemoryService;
        _notificationService = notificationService;
        _liveEvents = liveEvents;
        _db = db;
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
        var walletAddresses = await GetActiveWalletAddressesAsync();
        if (walletAddresses.Count == 0)
        {
            _logger.LogWarning("Takip edilecek aktif cüzdan yok. Tarama atlandı.");
            return;
        }

        foreach (var walletAddress in walletAddresses)
        {
            List<TransactionEvent> transactions;
            try
            {
                transactions = await _zerionService.GetRecentTransactionsAsync(
                    walletAddress,
                    limit: 10);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Zerion transaction polling failed for {Wallet}. Falling back to Alchemy transfers.", walletAddress);
                transactions = await _walletActivityService.GetRecentTokenMovementsAsync(
                    walletAddress,
                    limit: 10);
            }

            var newestTransaction = transactions
                .Where(x => !string.IsNullOrWhiteSpace(x.TxHash))
                .OrderByDescending(x => x.BlockTimestamp)
                .FirstOrDefault();

            if (await IsInitialWalletScanAsync(walletAddress))
            {
                foreach (var existingTransaction in transactions.Where(x => !string.IsNullOrWhiteSpace(x.TxHash)))
                {
                    if (!await _tradeRepository.IsTransactionProcessedAsync(existingTransaction.TxHash))
                    {
                        await _tradeRepository.MarkTransactionProcessedAsync(existingTransaction.TxHash, false);
                    }
                }

                await MarkWalletCheckedAsync(walletAddress, newestTransaction?.TxHash);
                _logger.LogInformation(
                    "Initial wallet baseline recorded for {Wallet}. {Count} existing transactions were marked as processed.",
                    walletAddress,
                    transactions.Count);
                continue;
            }

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

                await _liveEvents.PublishAsync(
                    LiveEventTypes.WalletActivityDetected,
                    $"{transaction.Direction} {transaction.Amount:F4} {transaction.NormalizedSymbol} (${transaction.UsdValue:F2})",
                    walletAddress,
                    transaction.TxHash,
                    transaction.NormalizedSymbol,
                    transaction.UsdValue,
                    new
                    {
                        transaction.Chain,
                        transaction.TransactionType,
                        transaction.BlockTimestamp,
                        transaction.TokenSymbol,
                        transaction.Amount
                    });

                var signal = await ProcessTransactionAsync(transaction, walletAddress);
                await _tradeRepository.MarkTransactionProcessedAsync(
                    transaction.TxHash,
                    string.Equals(signal.Decision, "TRADE", StringComparison.OrdinalIgnoreCase));
            }

            await MarkWalletCheckedAsync(walletAddress, newestTransaction?.TxHash);
        }
    }

    private async Task<List<string>> GetActiveWalletAddressesAsync()
    {
        var trackedWallets = await _db.TrackedWallets
            .AsNoTracking()
            .Where(x => x.IsActive)
            .OrderByDescending(x => x.ConfidenceScore)
            .Select(x => x.WalletAddress)
            .ToListAsync();

        if (trackedWallets.Count > 0)
        {
            return trackedWallets
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (string.IsNullOrWhiteSpace(_settings.Zerion.WhaleAddress))
        {
            return new List<string>();
        }

        return new List<string> { _settings.Zerion.WhaleAddress.Trim().ToLowerInvariant() };
    }

    private async Task MarkWalletCheckedAsync(string walletAddress, string? txHash)
    {
        var normalized = walletAddress.Trim().ToLowerInvariant();
        var trackedWallet = await _db.TrackedWallets
            .FirstOrDefaultAsync(x => x.WalletAddress == normalized);

        if (trackedWallet == null)
        {
            return;
        }

        trackedWallet.LastCheckedAt = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(txHash))
        {
            trackedWallet.LastSeenTxHash = txHash;
        }

        trackedWallet.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    private async Task<bool> IsInitialWalletScanAsync(string walletAddress)
    {
        var normalized = walletAddress.Trim().ToLowerInvariant();
        return await _db.TrackedWallets
            .AsNoTracking()
            .AnyAsync(x => x.WalletAddress == normalized && x.LastCheckedAt == null);
    }

    private Task<TradeSignal> ProcessTransactionAsync(TransactionEvent transaction, string walletAddress)
    {
        if (string.IsNullOrWhiteSpace(walletAddress))
        {
            walletAddress = _settings.Zerion.WhaleAddress;
        }

        return ProcessTransactionInternalAsync(transaction, walletAddress);
    }

    public Task<TradeSignal> ProcessTransactionAsync(TransactionEvent transaction)
    {
        return ProcessTransactionInternalAsync(transaction, _settings.Zerion.WhaleAddress);
    }

    private async Task<TradeSignal> ProcessTransactionInternalAsync(TransactionEvent transaction, string whaleAddress)
    {
        _logger.LogInformation(
            "ProcessTransactionAsync çağrıldı: {TxHash}",
            transaction.TxHash);

        var whaleStats = new WhaleStats();
        if (!string.IsNullOrWhiteSpace(whaleAddress))
        {
            try
            {
                whaleStats = await _zerionService.GetWalletPortfolioAsync(whaleAddress);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Zerion portfolio fetch failed for {Wallet}. Continuing with empty whale portfolio context.", whaleAddress);
            }
        }

        var userStats = await _okxService.GetAccountInfoAsync();
        var promptMemory = await _biasMemoryService.BuildPromptMemoryAsync();
        var proportionalAmountUsdt = whaleStats.TotalUsd > 0
            ? transaction.UsdValue / whaleStats.TotalUsd * userStats.TotalUsd
            : 0;
        await _liveEvents.PublishAsync(
            LiveEventTypes.AiAwakened,
            $"AI evaluating {transaction.NormalizedSymbol} movement",
            whaleAddress,
            transaction.TxHash,
            transaction.NormalizedSymbol,
            transaction.UsdValue,
            new
            {
                whaleBalanceUsd = whaleStats.TotalUsd,
                okxBalanceUsd = userStats.TotalUsd,
                okxPositions = userStats.ActivePositions.Count,
                transactionValueUsd = transaction.UsdValue,
                proportionalAmountUsdt
            });

        var aiContext = BuildAiContext(whaleStats, userStats, transaction, promptMemory);
        var aiDecision = await _aiService.AnalyzeMovementAsync(aiContext);
        NormalizeDecisionForExecution(aiDecision, aiContext);
        var signal = MapDecisionToSignal(aiDecision, transaction);
        await _biasMemoryService.RecordDecisionAsync(transaction, aiDecision, whaleAddress, whaleStats.TotalUsd);
        await _liveEvents.PublishAsync(
            LiveEventTypes.AiDecisionCompleted,
            $"{signal.Decision} {signal.Action} {signal.Symbol} confidence {signal.TradeConfidence}",
            whaleAddress,
            transaction.TxHash,
            signal.Symbol,
            transaction.UsdValue,
            new
            {
                aiDecision.Action,
                aiDecision.ShouldTrade,
                aiDecision.AmountUSDT,
                aiDecision.Leverage,
                aiDecision.ConfidenceScore,
                aiDecision.Reasoning,
                aiDecision.ParseSuccess,
                aiDecision.ParseError,
                signal.Decision,
                signal.MarginAmountUSDT,
                signal.Reason
            },
            signal.Decision == "TRADE" ? "warning" : "info");

        TradeResult? result = null;
        var autoTradingEnabled = await _db.RuntimeControls
            .AsNoTracking()
            .Where(x => x.Id == "global")
            .Select(x => x.AutoTradingEnabled)
            .FirstOrDefaultAsync();

        if (string.Equals(signal.Decision, "TRADE", StringComparison.OrdinalIgnoreCase) && autoTradingEnabled)
        {
            result = await _okxService.ExecuteTradeAsync(signal);
            await _liveEvents.PublishAsync(
                result.Success ? LiveEventTypes.TradeSubmitted : LiveEventTypes.TradeRejected,
                result.Success
                    ? $"OKX order submitted for {signal.Symbol}"
                    : $"OKX order rejected for {signal.Symbol}: {result.ErrorMessage}",
                whaleAddress,
                transaction.TxHash,
                signal.Symbol,
                signal.MarginAmountUSDT,
                new
                {
                    result.OrderId,
                    result.Success,
                    result.ExecutedPrice,
                    result.ErrorMessage,
                    signal.Action,
                    signal.Leverage
                },
                result.Success ? "success" : "danger");
        }
        else if (string.Equals(signal.Decision, "TRADE", StringComparison.OrdinalIgnoreCase))
        {
            await _liveEvents.PublishAsync(
                LiveEventTypes.TradeSkipped,
                "Trade skipped: auto trading is disabled.",
                whaleAddress,
                transaction.TxHash,
                signal.Symbol,
                transaction.UsdValue,
                new
                {
                    signal.Decision,
                    signal.Action,
                    signal.TradeConfidence,
                    signal.Reason,
                    autoTradingEnabled
                });
        }
        else
        {
            await _liveEvents.PublishAsync(
                LiveEventTypes.TradeSkipped,
                $"Trade skipped: {signal.Reason}",
                whaleAddress,
                transaction.TxHash,
                signal.Symbol,
                transaction.UsdValue,
                new
                {
                    signal.Decision,
                    signal.Action,
                    signal.TradeConfidence,
                    signal.Reason
                });
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

        await _notificationService.SendAsync(
            "WhaleTracker decision",
            $"Tx: {transaction.TxHash}\nAction: {signal.Decision} {signal.Action} {signal.Symbol}\nMargin: {signal.MarginAmountUSDT:F4} USDT\nOKX: {(result == null ? "not executed" : result.Success ? "success" : $"failed {result.ErrorMessage}")}\nReason: {signal.Reason}");

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

    private static AIContext BuildAiContext(
        WhaleStats whaleStats,
        UserStats userStats,
        TransactionEvent transaction,
        string promptMemory)
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
                RawText = $"{promptMemory}\n\nEVENT: {transaction.Direction} {transaction.Amount} {transaction.TokenSymbol} (${transaction.UsdValue}) via {transaction.TransactionType}"
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

    private static void NormalizeDecisionForExecution(AIDecision decision, AIContext context)
    {
        var rawAction = decision.Action;
        var rawSymbol = decision.Symbol;

        if (string.Equals(decision.Reasoning, "Miktar 0 veya negatif", StringComparison.OrdinalIgnoreCase) &&
            TryReadRawDecision(decision.RawResponse, out var parsedAction, out var parsedSymbol))
        {
            rawAction = parsedAction;
            rawSymbol = parsedSymbol;
        }

        rawAction = rawAction.ToUpperInvariant() switch
        {
            "BUY" => "LONG",
            "OPEN_LONG" => "LONG",
            "SELL" => "CLOSE_LONG",
            "SHORT" => "CLOSE_LONG",
            "CLOSE" => "CLOSE_LONG",
            _ => rawAction.ToUpperInvariant()
        };
        rawSymbol = NormalizeSymbol(rawSymbol, context.NewMovement.Symbol);

        if (rawAction == "CLOSE_LONG")
        {
            var hasMatchingLong = context.OurPositions.Any(position =>
                position.Direction.Contains("LONG", StringComparison.OrdinalIgnoreCase) &&
                (position.Symbol.Equals(rawSymbol, StringComparison.OrdinalIgnoreCase) ||
                 position.Symbol.StartsWith($"{rawSymbol}-", StringComparison.OrdinalIgnoreCase)));

            if (!hasMatchingLong)
            {
                decision.Action = "IGNORE";
                decision.Symbol = rawSymbol;
                decision.ShouldTrade = false;
                decision.AmountUSDT = 0;
                decision.Reasoning = $"Kapatılacak açık {rawSymbol} LONG pozisyonu yok";
                return;
            }
        }

        if ((rawAction == "LONG" || rawAction == "CLOSE_LONG") && decision.AmountUSDT <= 0)
        {
            if (context.WhaleBalanceUSDT <= 0 || context.OurBalanceUSDT <= 0 || context.NewMovement.ValueUSDT <= 0)
            {
                decision.Action = "IGNORE";
                decision.Symbol = rawSymbol;
                decision.ShouldTrade = false;
                decision.Reasoning = "Emir miktarı hesaplanamadı: wallet veya OKX portföy değeri mevcut değil";
                return;
            }

            decision.AmountUSDT = Math.Round(
                context.NewMovement.ValueUSDT / context.WhaleBalanceUSDT * context.OurBalanceUSDT,
                8);
        }

        decision.Action = rawAction;
        decision.Symbol = rawSymbol;
        decision.ShouldTrade = rawAction == "LONG" || rawAction == "CLOSE_LONG";
    }

    private static bool TryReadRawDecision(string rawResponse, out string action, out string symbol)
    {
        action = string.Empty;
        symbol = string.Empty;
        var start = rawResponse.IndexOf('{');
        var end = rawResponse.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(rawResponse[start..(end + 1)]);
            var root = document.RootElement;
            action = root.TryGetProperty("action", out var actionElement)
                ? actionElement.GetString() ?? string.Empty
                : string.Empty;
            symbol = root.TryGetProperty("symbol", out var symbolElement)
                ? symbolElement.GetString() ?? string.Empty
                : string.Empty;
            return !string.IsNullOrWhiteSpace(action);
        }
        catch (JsonException)
        {
            return false;
        }
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
