using Microsoft.EntityFrameworkCore;
using WhaleTracker.Data.Entities;

namespace WhaleTracker.Data;

/// <summary>
/// Ana veritabanı bağlam sınıfı
/// PostgreSQL bağlantısı
/// </summary>
public class WhaleTrackerDbContext : DbContext
{
    public WhaleTrackerDbContext(DbContextOptions<WhaleTrackerDbContext> options)
        : base(options)
    {
    }

    /// <summary>
    /// İşlem logları
    /// </summary>
    public DbSet<TradeLogEntity> TradeLogs => Set<TradeLogEntity>();

    /// <summary>
    /// Pozisyon anlık görüntüleri
    /// </summary>
    public DbSet<PositionSnapshotEntity> PositionSnapshots => Set<PositionSnapshotEntity>();

    /// <summary>
    /// PnL geçmişi
    /// </summary>
    public DbSet<PnlHistoryEntity> PnlHistory => Set<PnlHistoryEntity>();

    /// <summary>
    /// İşlenen transaction'lar
    /// </summary>
    public DbSet<ProcessedTransactionEntity> ProcessedTransactions => Set<ProcessedTransactionEntity>();

    public DbSet<HistoricalScanEntity> HistoricalScans => Set<HistoricalScanEntity>();

    public DbSet<InsiderCandidateEntity> InsiderCandidates => Set<InsiderCandidateEntity>();

    public DbSet<TrackedWalletEntity> TrackedWallets => Set<TrackedWalletEntity>();

    public DbSet<AiBiasStateEntity> AiBiasStates => Set<AiBiasStateEntity>();

    public DbSet<AiDecisionEventEntity> AiDecisionEvents => Set<AiDecisionEventEntity>();

    public DbSet<RuntimeControlEntity> RuntimeControls => Set<RuntimeControlEntity>();

    public DbSet<LiveEventEntity> LiveEvents => Set<LiveEventEntity>();

    public DbSet<TraderScanEntity> TraderScans => Set<TraderScanEntity>();

    public DbSet<TraderCandidateEntity> TraderCandidates => Set<TraderCandidateEntity>();

    public DbSet<TraderDiscoveryRunEntity> TraderDiscoveryRuns => Set<TraderDiscoveryRunEntity>();

    public DbSet<TraderDiscoveryCandidateEntity> TraderDiscoveryCandidates =>
        Set<TraderDiscoveryCandidateEntity>();

    public DbSet<CopyTraderPositionEntity> CopyTraderPositions => Set<CopyTraderPositionEntity>();

    public DbSet<CopyLedgerEventEntity> CopyLedgerEvents => Set<CopyLedgerEventEntity>();

    public DbSet<HyperliquidCopyTraderEntity> HyperliquidCopyTraders =>
        Set<HyperliquidCopyTraderEntity>();

    public DbSet<HyperliquidCopyPositionEntity> HyperliquidCopyPositions =>
        Set<HyperliquidCopyPositionEntity>();

    public DbSet<HyperliquidCopyEventEntity> HyperliquidCopyEvents =>
        Set<HyperliquidCopyEventEntity>();

    public DbSet<HyperliquidLiveFillEntity> HyperliquidLiveFills =>
        Set<HyperliquidLiveFillEntity>();

    public DbSet<HyperliquidLivePositionEntity> HyperliquidLivePositions =>
        Set<HyperliquidLivePositionEntity>();

    public DbSet<HyperliquidLiveScoreSnapshotEntity> HyperliquidLiveScoreSnapshots =>
        Set<HyperliquidLiveScoreSnapshotEntity>();

    public DbSet<TraderCoinProfileEntity> TraderCoinProfiles => Set<TraderCoinProfileEntity>();

    public DbSet<TraderCoinCurrentExposureEntity> TraderCoinCurrentExposures =>
        Set<TraderCoinCurrentExposureEntity>();

    public DbSet<CoinConsensusSnapshotEntity> CoinConsensusSnapshots =>
        Set<CoinConsensusSnapshotEntity>();

    public DbSet<AggregateSignalContributionEntity> AggregateSignalContributions =>
        Set<AggregateSignalContributionEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Trade Logs tablosu
        modelBuilder.Entity<TradeLogEntity>(entity =>
        {
            entity.HasIndex(e => e.WhaleTxHash);
            entity.HasIndex(e => e.CreatedAt);
            entity.HasIndex(e => e.Symbol);
        });

        // Position Snapshots tablosu
        modelBuilder.Entity<PositionSnapshotEntity>(entity =>
        {
            entity.HasIndex(e => e.SnapshotTime);
            entity.HasIndex(e => e.Symbol);
        });

        // PnL History tablosu
        modelBuilder.Entity<PnlHistoryEntity>(entity =>
        {
            entity.HasIndex(e => e.RecordedAt);
        });

        // Processed Transactions tablosu
        modelBuilder.Entity<ProcessedTransactionEntity>(entity =>
        {
            entity.HasKey(e => e.TxHash);
        });

        modelBuilder.Entity<HistoricalScanEntity>(entity =>
        {
            entity.HasIndex(e => e.CreatedAt);
            entity.HasIndex(e => e.Provider);
            entity.HasMany(e => e.Candidates)
                .WithOne(e => e.HistoricalScan)
                .HasForeignKey(e => e.HistoricalScanId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<InsiderCandidateEntity>(entity =>
        {
            entity.HasIndex(e => e.WalletAddress);
            entity.HasIndex(e => e.InsiderScore);
            entity.HasIndex(e => e.EstimatedProfitUsd);
        });

        modelBuilder.Entity<TrackedWalletEntity>(entity =>
        {
            entity.HasIndex(e => e.WalletAddress).IsUnique();
            entity.HasIndex(e => e.IsActive);
            entity.HasIndex(e => e.ConfidenceScore);
            entity.HasIndex(e => e.InsiderCandidateId);
        });

        modelBuilder.Entity<AiDecisionEventEntity>(entity =>
        {
            entity.HasIndex(e => e.TxHash);
            entity.HasIndex(e => e.WalletAddress);
            entity.HasIndex(e => e.CreatedAt);
            entity.HasIndex(e => e.Symbol);
        });

        modelBuilder.Entity<LiveEventEntity>(entity =>
        {
            entity.HasIndex(e => e.CreatedAt);
            entity.HasIndex(e => e.Type);
            entity.HasIndex(e => e.WalletAddress);
            entity.HasIndex(e => e.TxHash);
        });

        modelBuilder.Entity<TraderScanEntity>(entity =>
        {
            entity.HasIndex(e => e.CreatedAt);
            entity.HasMany(e => e.Candidates)
                .WithOne(e => e.TraderScan)
                .HasForeignKey(e => e.TraderScanId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TraderCandidateEntity>(entity =>
        {
            entity.HasIndex(e => e.WalletAddress);
            entity.HasIndex(e => e.Score);
            entity.HasIndex(e => e.AdjustedProfitUsd);
        });

        modelBuilder.Entity<TraderDiscoveryRunEntity>(entity =>
        {
            entity.HasIndex(e => e.CreatedAt);
            entity.HasIndex(e => e.ExecutionId);
            entity.HasMany(e => e.Candidates)
                .WithOne(e => e.TraderDiscoveryRun)
                .HasForeignKey(e => e.TraderDiscoveryRunId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TraderDiscoveryCandidateEntity>(entity =>
        {
            entity.HasIndex(e => e.WalletAddress);
            entity.HasIndex(e => e.ApprovedNotionalUsd);
            entity.HasIndex(e => new { e.TraderDiscoveryRunId, e.WalletAddress }).IsUnique();
        });

        modelBuilder.Entity<CopyTraderPositionEntity>(entity =>
        {
            entity.HasIndex(e => new { e.TraderId, e.Symbol, e.Side }).IsUnique();
            entity.HasIndex(e => new { e.Symbol, e.Side, e.IsActive });
            entity.HasIndex(e => e.UpdatedAt);
        });

        modelBuilder.Entity<CopyLedgerEventEntity>(entity =>
        {
            entity.HasIndex(e => e.CreatedAt);
            entity.HasIndex(e => new { e.Symbol, e.Side });
            entity.HasIndex(e => e.TraderId);
            entity.HasIndex(e => e.SourceEventId);
        });

        modelBuilder.Entity<HyperliquidCopyTraderEntity>(entity =>
        {
            entity.HasIndex(e => e.Address).IsUnique();
            entity.HasIndex(e => e.IsEnabled);
            entity.HasIndex(e => e.LastSyncAt);
        });

        modelBuilder.Entity<HyperliquidCopyPositionEntity>(entity =>
        {
            entity.HasIndex(e => new { e.TraderAddress, e.Symbol, e.Side }).IsUnique();
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.UpdatedAt);
        });

        modelBuilder.Entity<HyperliquidCopyEventEntity>(entity =>
        {
            entity.HasIndex(e => e.CreatedAt);
            entity.HasIndex(e => e.TraderAddress);
            entity.HasIndex(e => new { e.Symbol, e.Side });
            entity.HasIndex(e => e.SourceEventId);
        });

        modelBuilder.Entity<HyperliquidLiveFillEntity>(entity =>
        {
            entity.HasIndex(e => e.DedupeKey).IsUnique();
            entity.HasIndex(e => e.ExchangeTime);
            entity.HasIndex(e => new { e.TraderAddress, e.Coin });
        });

        modelBuilder.Entity<HyperliquidLivePositionEntity>(entity =>
        {
            entity.HasIndex(e => new { e.TraderAddress, e.OkxSymbol, e.Side, e.Status });
            entity.HasIndex(e => e.OpenedAt);
            entity.HasIndex(e => e.ClosedAt);
        });

        modelBuilder.Entity<HyperliquidLiveScoreSnapshotEntity>(entity =>
        {
            entity.HasIndex(e => new { e.TraderAddress, e.ScoredAt });
            entity.HasIndex(e => e.LiveScore);
            entity.HasIndex(e => e.ScoredAt);
        });

        modelBuilder.Entity<TraderCoinProfileEntity>(entity =>
        {
            entity.HasIndex(e => new { e.TraderAddress, e.Coin, e.WindowDays }).IsUnique();
            entity.HasIndex(e => e.CoinSkillScore);
            entity.HasIndex(e => e.ComputedAt);
        });

        modelBuilder.Entity<TraderCoinCurrentExposureEntity>(entity =>
        {
            entity.HasIndex(e => new { e.TraderAddress, e.Coin }).IsUnique();
            entity.HasIndex(e => e.WeightedSignal);
            entity.HasIndex(e => e.UpdatedAt);
        });

        modelBuilder.Entity<CoinConsensusSnapshotEntity>(entity =>
        {
            entity.HasIndex(e => new { e.Coin, e.Timestamp });
            entity.HasIndex(e => e.DirectionScore);
            entity.HasIndex(e => e.Timestamp);
        });

        modelBuilder.Entity<AggregateSignalContributionEntity>(entity =>
        {
            entity.HasIndex(e => e.CoinConsensusSnapshotId);
            entity.HasIndex(e => new { e.TraderAddress, e.Coin });
            entity.HasIndex(e => e.CreatedAt);
        });
    }
}
