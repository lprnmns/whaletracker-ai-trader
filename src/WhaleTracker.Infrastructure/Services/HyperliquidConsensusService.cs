using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WhaleTracker.Core.Interfaces;
using WhaleTracker.Core.Models;
using WhaleTracker.Data;
using WhaleTracker.Data.Entities;

namespace WhaleTracker.Infrastructure.Services;

public sealed class HyperliquidConsensusService : IHyperliquidConsensusService
{
    private const int WindowDays = 30;
    private const string GeneralCoin = "__GENERAL__";
    private readonly WhaleTrackerDbContext _db;
    private readonly ILogger<HyperliquidConsensusService> _logger;

    public HyperliquidConsensusService(
        WhaleTrackerDbContext db,
        ILogger<HyperliquidConsensusService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<HyperliquidConsensusImportResponse> ImportWatchlistRunAsync(
        HyperliquidConsensusImportRequest request,
        CancellationToken cancellationToken = default)
    {
        var run = ResolveRun(request.RunId);
        if (run == null)
        {
            throw new DirectoryNotFoundException($"Hyperliquid report run not found: {request.RunId}");
        }

        var watchlist = ReadCsv(Path.Combine(run.FullName, "historical_scoreboard", "historical_scoreboard.csv"))
            .Where(row => row.TryGetValue("watchlist_eligible", out var eligible) &&
                eligible.Equals("yes", StringComparison.OrdinalIgnoreCase))
            .OrderBy(row => IntValue(row, "rank") == 0 ? int.MaxValue : IntValue(row, "rank"))
            .ThenByDescending(row => DecimalValue(row, "historical_quality_score"))
            .Take(Math.Clamp(request.Take, 1, 500))
            .ToList();

        var addresses = watchlist
            .Select(row => NormalizeAddress(row.GetValueOrDefault("address") ?? string.Empty))
            .Where(IsAddress)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var synced = 0;
        if (request.SyncTraders)
        {
            synced = await SyncWatchlistTradersAsync(watchlist, request.PreserveRealExecution, cancellationToken);
        }

        var profilesWritten = 0;
        if (request.RebuildProfiles)
        {
            profilesWritten = await RebuildProfilesAsync(run, watchlist, addresses, cancellationToken);
        }

        var snapshot = request.RefreshConsensus
            ? await RefreshConsensusAsync(cancellationToken)
            : await GetSnapshotAsync(cancellationToken);

        return new HyperliquidConsensusImportResponse
        {
            RunId = run.Name,
            WatchlistCount = addresses.Count,
            SyncedTraders = synced,
            ProfilesWritten = profilesWritten,
            ExposuresWritten = snapshot.Exposures.Count,
            ConsensusCoins = snapshot.Coins.Count,
            WatchlistAddresses = addresses
        };
    }

    public async Task<HyperliquidConsensusSnapshotResponse> RefreshConsensusAsync(
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var profiles = await _db.TraderCoinProfiles
            .AsNoTracking()
            .Where(x => x.WindowDays == WindowDays)
            .ToListAsync(cancellationToken);
        var generalProfiles = profiles
            .Where(x => x.Coin == GeneralCoin)
            .ToDictionary(x => x.TraderAddress, StringComparer.OrdinalIgnoreCase);
        var coinProfiles = profiles
            .Where(x => x.Coin != GeneralCoin)
            .ToDictionary(x => (x.TraderAddress.ToLowerInvariant(), x.Coin.ToUpperInvariant()));

        var trackedAddresses = generalProfiles.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var livePositions = await _db.HyperliquidLivePositions
            .AsNoTracking()
            .Where(x =>
                (x.Status == "LIVE_OPEN" || x.Status == "BASELINE_OPEN") &&
                x.IsOkxTradable &&
                trackedAddresses.Contains(x.TraderAddress))
            .ToListAsync(cancellationToken);

        var activeByTrader = livePositions
            .GroupBy(x => x.TraderAddress, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                x => x.Key,
                x => x.Sum(p => Math.Abs(p.CurrentNotionalUsd)),
                StringComparer.OrdinalIgnoreCase);

        await _db.TraderCoinCurrentExposures.ExecuteDeleteAsync(cancellationToken);
        var exposures = new List<TraderCoinCurrentExposureEntity>();
        foreach (var position in livePositions)
        {
            if (!generalProfiles.TryGetValue(position.TraderAddress, out var general))
            {
                continue;
            }

            var coin = NormalizeCoin(position.OkxSymbol, position.Coin);
            coinProfiles.TryGetValue((position.TraderAddress.ToLowerInvariant(), coin), out var coinProfile);

            var accountValue = position.LatestSourceAccountValueUsd > 0
                ? position.LatestSourceAccountValueUsd
                : position.SourceAccountValueAtOpen;
            if (accountValue <= 0)
            {
                continue;
            }

            var allocPct = position.CurrentNotionalUsd / accountValue * 100m;
            var normalizedExposure = Clamp(allocPct / SaturationPct(coin), 0m, 1m);
            var allocationConviction = AllocationConviction(allocPct, coinProfile);
            var coinSkill = coinProfile?.CoinSkillScore ?? Clamp(general.CoinSkillScore * 0.55m, 0.05m, 0.35m);
            var sampleConfidence = coinProfile?.SampleConfidence ?? 0.2m;
            var freshness = FreshnessScore(position.LastSeenAt, now);
            var riskAdjustment = RiskAdjustment(activeByTrader.GetValueOrDefault(position.TraderAddress), accountValue);
            var direction = position.Side.Equals("LONG", StringComparison.OrdinalIgnoreCase) ? 1m : -1m;
            var weightedSignal = direction *
                normalizedExposure *
                general.CoinSkillScore *
                coinSkill *
                sampleConfidence *
                allocationConviction *
                freshness *
                riskAdjustment;

            exposures.Add(new TraderCoinCurrentExposureEntity
            {
                TraderAddress = position.TraderAddress,
                Coin = coin,
                Side = position.Side.ToUpperInvariant(),
                CurrentNotionalUsd = position.CurrentNotionalUsd,
                CurrentAccountValueUsd = accountValue,
                CurrentAllocPct = allocPct,
                UnrealizedPnlUsd = position.UnrealizedPnlUsd,
                EntryPrice = position.EntryPrice,
                OpenedAt = position.OpenedAt,
                LastSeenAt = position.LastSeenAt,
                NormalizedExposure = normalizedExposure,
                AllocationConviction = allocationConviction,
                CoinSkillScore = coinSkill,
                SampleConfidence = sampleConfidence,
                FreshnessScore = freshness,
                RiskAdjustment = riskAdjustment,
                WeightedSignal = weightedSignal,
                IsBaseline = !position.OpenedFromTracking,
                UpdatedAt = now
            });
        }

        if (exposures.Count > 0)
        {
            await _db.TraderCoinCurrentExposures.AddRangeAsync(exposures, cancellationToken);
        }

        var previousConsensusCutoff = now.AddHours(-2);
        await _db.AggregateSignalContributions
            .Where(x => x.CreatedAt < previousConsensusCutoff)
            .ExecuteDeleteAsync(cancellationToken);
        await _db.CoinConsensusSnapshots
            .Where(x => x.Timestamp < previousConsensusCutoff)
            .ExecuteDeleteAsync(cancellationToken);

        var totalPower = exposures.Sum(x => Math.Abs(x.WeightedSignal));
        foreach (var group in exposures.GroupBy(x => x.Coin))
        {
            var rows = group.ToList();
            var longPower = rows.Where(x => x.WeightedSignal > 0).Sum(x => x.WeightedSignal);
            var shortPower = rows.Where(x => x.WeightedSignal < 0).Sum(x => Math.Abs(x.WeightedSignal));
            var gross = longPower + shortPower;
            if (gross <= 0)
            {
                continue;
            }

            var netSignal = (longPower - shortPower) / gross;
            var participation = totalPower <= 0 ? 0 : gross / totalPower;
            var conflict = Math.Min(longPower, shortPower) / Math.Max(longPower, shortPower);
            var directionScore = 100m * netSignal * Sqrt(participation) * (1m - conflict);
            var quality = QualityScore(rows, conflict);
            var side = directionScore switch
            {
                >= 10m => "LONG",
                <= -10m => "SHORT",
                _ => "FLAT"
            };
            var action = DecideAction(directionScore, quality, conflict, participation, out var skipReason);
            var snapshot = new CoinConsensusSnapshotEntity
            {
                Coin = group.Key,
                Timestamp = now,
                LongPower = longPower,
                ShortPower = shortPower,
                NetSignal = netSignal,
                Participation = participation,
                ConflictRatio = conflict,
                DirectionScore = directionScore,
                QualityScore = quality,
                TargetSide = side,
                TargetNotionalUsd = side == "FLAT" ? 0 : Clamp(Math.Abs(directionScore) / 100m * 25m, 0m, 25m),
                Action = action,
                SkipReason = skipReason,
                ContributorCount = rows.Count,
                TopContributorsJson = JsonSerializer.Serialize(rows
                    .OrderByDescending(x => Math.Abs(x.WeightedSignal))
                    .Take(5)
                    .Select(x => new
                    {
                        trader = x.TraderAddress,
                        side = x.Side,
                        notional = x.CurrentNotionalUsd,
                        allocPct = x.CurrentAllocPct,
                        signal = x.WeightedSignal,
                        baseline = x.IsBaseline
                    }))
            };
            _db.CoinConsensusSnapshots.Add(snapshot);
            await _db.SaveChangesAsync(cancellationToken);

            _db.AggregateSignalContributions.AddRange(rows.Select(row => new AggregateSignalContributionEntity
            {
                CoinConsensusSnapshotId = snapshot.Id,
                TraderAddress = row.TraderAddress,
                Coin = row.Coin,
                SourceSide = row.Side,
                SourcePositionNotionalUsd = row.CurrentNotionalUsd,
                SourceAccountValueUsd = row.CurrentAccountValueUsd,
                ExposureUnit = row.NormalizedExposure,
                TraderWeight = generalProfiles.TryGetValue(row.TraderAddress, out var gp) ? gp.CoinSkillScore : 0,
                CoinSkillScore = row.CoinSkillScore,
                AllocationConviction = row.AllocationConviction,
                WeightedSignal = row.WeightedSignal,
                CreatedAt = now
            }));
        }

        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation(
            "Hyperliquid consensus refreshed: {ExposureCount} exposures, {CoinCount} coins.",
            exposures.Count,
            exposures.Select(x => x.Coin).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        return await GetSnapshotAsync(cancellationToken);
    }

    public async Task<HyperliquidConsensusSnapshotResponse> GetSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        var traders = await _db.HyperliquidCopyTraders
            .AsNoTracking()
            .ToDictionaryAsync(x => x.Address, StringComparer.OrdinalIgnoreCase, cancellationToken);

        var latestSnapshots = (await _db.CoinConsensusSnapshots
                .AsNoTracking()
                .OrderByDescending(x => x.Timestamp)
                .ThenByDescending(x => x.Id)
                .Take(500)
                .ToListAsync(cancellationToken))
            .GroupBy(x => x.Coin, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .OrderByDescending(x => Math.Abs(x.DirectionScore))
            .ToList();

        var exposures = await _db.TraderCoinCurrentExposures
            .AsNoTracking()
            .OrderByDescending(x => Math.Abs(x.WeightedSignal))
            .Take(300)
            .ToListAsync(cancellationToken);

        var profiles = await _db.TraderCoinProfiles
            .AsNoTracking()
            .Where(x => x.WindowDays == WindowDays && x.Coin != GeneralCoin)
            .OrderByDescending(x => x.CoinSkillScore)
            .ThenByDescending(x => x.NetPnlUsd)
            .Take(100)
            .ToListAsync(cancellationToken);

        return new HyperliquidConsensusSnapshotResponse
        {
            CheckedAt = DateTime.UtcNow,
            Coins = latestSnapshots.Select(MapCoin).ToList(),
            Exposures = exposures.Select(x => MapExposure(x, traders.GetValueOrDefault(x.TraderAddress))).ToList(),
            TopProfiles = profiles.Select(x => MapProfile(x, traders.GetValueOrDefault(x.TraderAddress))).ToList()
        };
    }

    private async Task<int> SyncWatchlistTradersAsync(
        IReadOnlyList<Dictionary<string, string>> watchlist,
        bool preserveRealExecution,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var synced = 0;
        foreach (var row in watchlist)
        {
            var address = NormalizeAddress(row.GetValueOrDefault("address") ?? string.Empty);
            if (!IsAddress(address))
            {
                continue;
            }

            var rank = IntValue(row, "rank");
            var trader = await _db.HyperliquidCopyTraders
                .FirstOrDefaultAsync(x => x.Address == address, cancellationToken);
            if (trader == null)
            {
                trader = new HyperliquidCopyTraderEntity
                {
                    Address = address,
                    CreatedAt = now,
                    ExecuteOrders = false
                };
                _db.HyperliquidCopyTraders.Add(trader);
            }
            else if (!preserveRealExecution)
            {
                trader.ExecuteOrders = false;
            }

            trader.Label = $"HL Consensus #{rank:00} {ShortAddress(address)}";
            trader.IsEnabled = true;
            trader.MarginPerTraderUsdt = trader.MarginPerTraderUsdt <= 0 ? 10m : trader.MarginPerTraderUsdt;
            trader.Leverage = trader.Leverage <= 0 ? 10 : trader.Leverage;
            trader.CopyActiveOnEnable = true;
            trader.AdoptActiveOnlyWhenNegative = true;
            trader.LastError = string.Empty;
            trader.UpdatedAt = now;
            synced++;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return synced;
    }

    private async Task<int> RebuildProfilesAsync(
        DirectoryInfo run,
        IReadOnlyList<Dictionary<string, string>> watchlist,
        IReadOnlyList<string> addresses,
        CancellationToken cancellationToken)
    {
        await _db.TraderCoinProfiles
            .Where(x => addresses.Contains(x.TraderAddress) && x.WindowDays == WindowDays)
            .ExecuteDeleteAsync(cancellationToken);

        var rows = new List<TraderCoinProfileEntity>();
        foreach (var row in watchlist)
        {
            var address = NormalizeAddress(row.GetValueOrDefault("address") ?? string.Empty);
            if (!IsAddress(address))
            {
                continue;
            }

            rows.Add(GeneralProfile(address, row));
            var closedPath = Path.Combine(run.FullName, address, "closed_positions.csv");
            var closedRows = ReadCsv(closedPath)
                .Where(IsOkxTradableRow)
                .Where(x => !string.IsNullOrWhiteSpace(x.GetValueOrDefault("coin")))
                .ToList();

            foreach (var coinGroup in closedRows.GroupBy(x => (x.GetValueOrDefault("coin") ?? string.Empty).ToUpperInvariant()))
            {
                var profile = CoinProfile(address, coinGroup.Key, coinGroup.ToList(), row);
                if (profile.ClosedPositions > 0)
                {
                    rows.Add(profile);
                }
            }
        }

        await _db.TraderCoinProfiles.AddRangeAsync(rows, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return rows.Count;
    }

    private static TraderCoinProfileEntity GeneralProfile(string address, Dictionary<string, string> row)
    {
        var closed = IntValue(row, "okx_tradable_closed_positions");
        var wins = IntValue(row, "okx_tradable_winning_positions");
        var losses = IntValue(row, "okx_tradable_losing_positions");
        var quality = DecimalValue(row, "historical_quality_score");
        var confidence = DecimalValue(row, "confidence_score");
        var netPnl = DecimalValue(row, "okx_tradable_net_pnl_usd");
        var profitFactor = DecimalValue(row, "profit_factor");
        return new TraderCoinProfileEntity
        {
            TraderAddress = address,
            Coin = GeneralCoin,
            WindowDays = WindowDays,
            ComputedAt = DateTime.UtcNow,
            ClosedPositions = closed,
            WinningPositions = wins,
            LosingPositions = losses,
            WinRate = DecimalValue(row, "okx_tradable_win_rate_pct"),
            NetPnlUsd = netPnl,
            GrossProfitUsd = Math.Max(netPnl, 0),
            GrossLossUsd = Math.Min(netPnl, 0),
            ProfitFactor = profitFactor,
            AvgAllocPct = DecimalValue(row, "max_fill_balance_pct"),
            MaxAllocPct = DecimalValue(row, "max_fill_balance_pct"),
            AvgHoldSeconds = DecimalValue(row, "avg_okx_tradable_holding_hours") * 3600m,
            CoinSkillScore = Clamp(quality / 100m, 0m, 1m),
            SampleConfidence = Clamp(confidence / 100m, 0m, 1m),
            HistoricalQualityScore = quality,
            HistoricalConfidenceScore = confidence,
            OneTradePnlConcentration = DecimalValue(row, "one_trade_pnl_concentration_pct") / 100m,
            ApproximationQuality = "historical_scoreboard"
        };
    }

    private static TraderCoinProfileEntity CoinProfile(
        string address,
        string coin,
        List<Dictionary<string, string>> rows,
        Dictionary<string, string> generalRow)
    {
        var pnls = rows.Select(x => DecimalValue(x, "net_pnl_usd")).ToList();
        var notionals = rows.Select(x => DecimalValue(x, "entry_notional_usd")).Where(x => x > 0).ToList();
        var allocs = rows.Select(x => DecimalValue(x, "max_fill_balance_pct")).Where(x => x >= 0).ToList();
        var holds = rows.Select(x => DecimalValue(x, "holding_hours") * 3600m).Where(x => x >= 0).ToList();
        var grossProfit = pnls.Where(x => x > 0).Sum();
        var grossLoss = pnls.Where(x => x < 0).Sum();
        var netPnl = pnls.Sum();
        var closed = rows.Count;
        var wins = pnls.Count(x => x > 0);
        var losses = pnls.Count(x => x < 0);
        var profitFactor = grossLoss == 0 ? (grossProfit > 0 ? 999m : 0m) : grossProfit / Math.Abs(grossLoss);
        var oneTrade = grossProfit <= 0 ? 1m : Clamp(pnls.Where(x => x > 0).DefaultIfEmpty(0).Max() / grossProfit, 0m, 1m);
        var accountValue = Math.Max(DecimalValue(generalRow, "account_value_usd"), 30_000m);
        var pnlScore = Clamp((netPnl / accountValue + 0.10m) / 0.25m, 0m, 1m);
        var sampleScore = Clamp(closed / 10m, 0m, 1m);
        var winRate = closed == 0 ? 0 : wins / (decimal)closed * 100m;
        var winrateQuality = Clamp(winRate / 100m, 0m, 1m) * sampleScore;
        var pfScore = Clamp((Math.Min(profitFactor, 10m) - 1m) / 4m, 0m, 1m);
        var consistency = 1m - oneTrade;
        var rawSkill = 0.30m * pnlScore +
            0.25m * winrateQuality +
            0.20m * sampleScore +
            0.15m * pfScore +
            0.10m * consistency;

        return new TraderCoinProfileEntity
        {
            TraderAddress = address,
            Coin = coin,
            WindowDays = WindowDays,
            ComputedAt = DateTime.UtcNow,
            ClosedPositions = closed,
            WinningPositions = wins,
            LosingPositions = losses,
            WinRate = winRate,
            NetPnlUsd = netPnl,
            GrossProfitUsd = grossProfit,
            GrossLossUsd = grossLoss,
            ProfitFactor = profitFactor,
            TotalEntryNotionalUsd = notionals.Sum(),
            AvgEntryNotionalUsd = Average(notionals),
            MedianEntryNotionalUsd = Percentile(notionals, 0.50m),
            AvgAllocPct = Average(allocs),
            MedianAllocPct = Percentile(allocs, 0.50m),
            P75AllocPct = Percentile(allocs, 0.75m),
            P90AllocPct = Percentile(allocs, 0.90m),
            MaxAllocPct = allocs.DefaultIfEmpty(0).Max(),
            AvgHoldSeconds = Average(holds),
            MedianHoldSeconds = Percentile(holds, 0.50m),
            BestTradePnlUsd = pnls.DefaultIfEmpty(0).Max(),
            WorstTradePnlUsd = pnls.DefaultIfEmpty(0).Min(),
            OneTradePnlConcentration = oneTrade,
            CoinSkillScore = Clamp(rawSkill, 0m, 1m),
            SampleConfidence = Clamp(sampleScore * 0.7m + Clamp(DecimalValue(generalRow, "confidence_score") / 100m, 0m, 1m) * 0.3m, 0m, 1m),
            HistoricalQualityScore = DecimalValue(generalRow, "historical_quality_score"),
            HistoricalConfidenceScore = DecimalValue(generalRow, "confidence_score"),
            ApproximationQuality = "closed_positions_fill_balance_pct_proxy"
        };
    }

    private static bool IsOkxTradableRow(Dictionary<string, string> row)
    {
        var okx = row.GetValueOrDefault("okx_tradable") ?? string.Empty;
        var copyable = row.GetValueOrDefault("copyable_major") ?? string.Empty;
        return okx.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
            okx.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            copyable.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
            copyable.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private static decimal AllocationConviction(decimal currentAllocPct, TraderCoinProfileEntity? profile)
    {
        if (profile == null || profile.P90AllocPct <= 0)
        {
            return Clamp(currentAllocPct / 20m, 0.15m, 0.7m);
        }

        if (currentAllocPct >= profile.P90AllocPct)
        {
            return 1m;
        }

        if (currentAllocPct >= profile.P75AllocPct)
        {
            return 0.8m;
        }

        if (currentAllocPct >= profile.MedianAllocPct)
        {
            return 0.65m;
        }

        return 0.5m;
    }

    private static decimal FreshnessScore(DateTime lastSeenAt, DateTime now)
    {
        var age = now - lastSeenAt;
        if (age <= TimeSpan.FromMinutes(5)) return 1m;
        if (age <= TimeSpan.FromMinutes(30)) return 0.8m;
        if (age <= TimeSpan.FromHours(2)) return 0.6m;
        if (age <= TimeSpan.FromHours(12)) return 0.4m;
        return 0.25m;
    }

    private static decimal RiskAdjustment(decimal activeNotional, decimal accountValue)
    {
        if (accountValue <= 0) return 0.5m;
        var grossPct = activeNotional / accountValue * 100m;
        if (grossPct <= 150m) return 1m;
        if (grossPct <= 300m) return 0.85m;
        if (grossPct <= 600m) return 0.65m;
        return 0.45m;
    }

    private static decimal QualityScore(List<TraderCoinCurrentExposureEntity> rows, decimal conflict)
    {
        var total = rows.Sum(x => Math.Abs(x.WeightedSignal));
        if (total <= 0)
        {
            return 0;
        }

        decimal Weighted(Func<TraderCoinCurrentExposureEntity, decimal> selector) =>
            rows.Sum(x => Math.Abs(x.WeightedSignal) * selector(x)) / total;

        var quality = 100m * (
            0.35m * Weighted(x => x.CoinSkillScore) +
            0.25m * Weighted(x => x.SampleConfidence) +
            0.15m * (1m - conflict) +
            0.15m * 1m +
            0.10m * Weighted(x => x.FreshnessScore));
        return Clamp(quality, 0m, 100m);
    }

    private static string DecideAction(
        decimal directionScore,
        decimal quality,
        decimal conflict,
        decimal participation,
        out string skipReason)
    {
        skipReason = string.Empty;
        if (Math.Abs(directionScore) < 25m)
        {
            skipReason = "weak_direction";
            return "WATCH";
        }

        if (quality < 45m)
        {
            skipReason = "low_quality";
            return "WATCH";
        }

        if (conflict > 0.45m)
        {
            skipReason = "high_conflict";
            return "WATCH";
        }

        if (participation < 0.08m)
        {
            skipReason = "low_participation";
            return "WATCH";
        }

        return directionScore > 0 ? "OPEN_LONG" : "OPEN_SHORT";
    }

    private static decimal SaturationPct(string coin) => coin switch
    {
        "BTC" or "ETH" => 30m,
        "SOL" => 25m,
        "HYPE" => 20m,
        "XRP" or "SUI" or "AVAX" or "LINK" or "DOGE" or "BNB" => 20m,
        _ => 12m
    };

    private static DirectoryInfo? ResolveRun(string runId)
    {
        if (runId.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '_' && ch != '-'))
        {
            return null;
        }

        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current != null)
        {
            var directory = new DirectoryInfo(Path.Combine(
                current.FullName,
                "data",
                "reports",
                "hyperliquid_profiles",
                runId));
            if (directory.Exists)
            {
                return directory;
            }

            current = current.Parent;
        }

        var fallback = new DirectoryInfo(Path.Combine(
            Directory.GetCurrentDirectory(),
            "data",
            "reports",
            "hyperliquid_profiles",
            runId));
        return fallback.Exists ? fallback : null;
    }

    private static List<Dictionary<string, string>> ReadCsv(string path)
    {
        if (!File.Exists(path))
        {
            return new List<Dictionary<string, string>>();
        }

        var lines = File.ReadAllLines(path);
        if (lines.Length == 0)
        {
            return new List<Dictionary<string, string>>();
        }

        var headers = ParseCsvLine(lines[0]);
        var result = new List<Dictionary<string, string>>();
        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var values = ParseCsvLine(line);
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < headers.Count; i++)
            {
                row[headers[i]] = i < values.Count ? values[i] : string.Empty;
            }
            result.Add(row);
        }

        return result;
    }

    private static List<string> ParseCsvLine(string line)
    {
        var values = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
                continue;
            }

            if (ch == ',' && !inQuotes)
            {
                values.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(ch);
        }

        values.Add(current.ToString());
        return values;
    }

    private static int IntValue(Dictionary<string, string> row, string key) =>
        int.TryParse(row.GetValueOrDefault(key), NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;

    private static decimal DecimalValue(Dictionary<string, string> row, string key) =>
        decimal.TryParse(row.GetValueOrDefault(key), NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0m;

    private static decimal Average(IReadOnlyList<decimal> values) =>
        values.Count == 0 ? 0 : values.Average();

    private static decimal Percentile(IReadOnlyList<decimal> values, decimal percentile)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var sorted = values.OrderBy(x => x).ToList();
        var index = (sorted.Count - 1) * percentile;
        var lower = (int)Math.Floor(index);
        var upper = (int)Math.Ceiling(index);
        if (lower == upper)
        {
            return sorted[lower];
        }

        var weight = index - lower;
        return sorted[lower] * (1 - weight) + sorted[upper] * weight;
    }

    private static decimal Clamp(decimal value, decimal min, decimal max) =>
        Math.Min(Math.Max(value, min), max);

    private static decimal Sqrt(decimal value) =>
        value <= 0 ? 0 : (decimal)Math.Sqrt((double)value);

    private static string NormalizeAddress(string value) => value.Trim().ToLowerInvariant();

    private static bool IsAddress(string value) =>
        value.Length == 42 &&
        value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
        value.Skip(2).All(Uri.IsHexDigit);

    private static string NormalizeCoin(string okxSymbol, string sourceCoin)
    {
        var coin = string.IsNullOrWhiteSpace(okxSymbol) ? sourceCoin : okxSymbol;
        coin = coin.ToUpperInvariant();
        return coin.EndsWith("-USDT-SWAP", StringComparison.OrdinalIgnoreCase)
            ? coin[..^10]
            : coin;
    }

    private static string ShortAddress(string address) =>
        address.Length <= 12 ? address : $"{address[..6]}...{address[^4..]}";

    private static CoinConsensusView MapCoin(CoinConsensusSnapshotEntity entity) => new()
    {
        Id = entity.Id,
        Coin = entity.Coin,
        Timestamp = entity.Timestamp,
        LongPower = entity.LongPower,
        ShortPower = entity.ShortPower,
        NetSignal = entity.NetSignal,
        Participation = entity.Participation,
        ConflictRatio = entity.ConflictRatio,
        DirectionScore = entity.DirectionScore,
        QualityScore = entity.QualityScore,
        TargetSide = entity.TargetSide,
        TargetNotionalUsd = entity.TargetNotionalUsd,
        Action = entity.Action,
        SkipReason = entity.SkipReason,
        ContributorCount = entity.ContributorCount,
        TopContributorsJson = entity.TopContributorsJson
    };

    private static TraderCoinExposureView MapExposure(
        TraderCoinCurrentExposureEntity entity,
        HyperliquidCopyTraderEntity? trader) => new()
    {
        TraderAddress = entity.TraderAddress,
        Label = trader?.Label ?? ShortAddress(entity.TraderAddress),
        Coin = entity.Coin,
        Side = entity.Side,
        CurrentNotionalUsd = entity.CurrentNotionalUsd,
        CurrentAccountValueUsd = entity.CurrentAccountValueUsd,
        CurrentAllocPct = entity.CurrentAllocPct,
        UnrealizedPnlUsd = entity.UnrealizedPnlUsd,
        EntryPrice = entity.EntryPrice,
        OpenedAt = entity.OpenedAt,
        LastSeenAt = entity.LastSeenAt,
        NormalizedExposure = entity.NormalizedExposure,
        AllocationConviction = entity.AllocationConviction,
        CoinSkillScore = entity.CoinSkillScore,
        SampleConfidence = entity.SampleConfidence,
        FreshnessScore = entity.FreshnessScore,
        RiskAdjustment = entity.RiskAdjustment,
        WeightedSignal = entity.WeightedSignal,
        IsBaseline = entity.IsBaseline
    };

    private static TraderCoinProfileView MapProfile(
        TraderCoinProfileEntity entity,
        HyperliquidCopyTraderEntity? trader) => new()
    {
        TraderAddress = entity.TraderAddress,
        Label = trader?.Label ?? ShortAddress(entity.TraderAddress),
        Coin = entity.Coin,
        WindowDays = entity.WindowDays,
        ClosedPositions = entity.ClosedPositions,
        WinRate = entity.WinRate,
        NetPnlUsd = entity.NetPnlUsd,
        ProfitFactor = entity.ProfitFactor,
        AvgAllocPct = entity.AvgAllocPct,
        MedianAllocPct = entity.MedianAllocPct,
        P90AllocPct = entity.P90AllocPct,
        CoinSkillScore = entity.CoinSkillScore,
        SampleConfidence = entity.SampleConfidence,
        HistoricalQualityScore = entity.HistoricalQualityScore,
        HistoricalConfidenceScore = entity.HistoricalConfidenceScore
    };
}
