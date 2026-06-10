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
    }
}
