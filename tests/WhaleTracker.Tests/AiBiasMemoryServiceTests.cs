using Microsoft.EntityFrameworkCore;
using WhaleTracker.Core.Models;
using WhaleTracker.Data;
using WhaleTracker.Infrastructure.Services;
using Xunit;

namespace WhaleTracker.Tests;

public class AiBiasMemoryServiceTests
{
    [Fact]
    public async Task RecordDecision_IgnoresSmallWalletPercentMovement()
    {
        await using var db = CreateDbContext();
        var service = new AiBiasMemoryService(db);

        await service.RecordDecisionAsync(
            new TransactionEvent
            {
                TxHash = "0xsmall",
                Direction = "Incoming",
                TransactionType = "BUY",
                NormalizedSymbol = "ETH",
                TokenSymbol = "ETH",
                UsdValue = 400m,
                Amount = 0.1m,
                BlockTimestamp = DateTime.UtcNow
            },
            new AIDecision
            {
                ShouldTrade = true,
                Action = "LONG",
                Symbol = "ETH",
                ConfidenceScore = 100
            },
            "0xabc",
            walletBalanceUsd: 10_000m);

        var state = await db.AiBiasStates.SingleAsync();
        var evt = await db.AiDecisionEvents.SingleAsync();

        Assert.Equal(0m, state.BiasScore);
        Assert.Equal("NEUTRAL", state.Direction);
        Assert.Contains("below 5% wallet threshold", evt.IgnoredReason);
    }

    [Fact]
    public async Task RecordDecision_IncreasesBiasForLargeLongDecision()
    {
        await using var db = CreateDbContext();
        var service = new AiBiasMemoryService(db);

        await service.RecordDecisionAsync(
            new TransactionEvent
            {
                TxHash = "0xbig",
                Direction = "Incoming",
                TransactionType = "BUY",
                NormalizedSymbol = "WETH",
                TokenSymbol = "WETH",
                UsdValue = 2_000m,
                Amount = 1m,
                BlockTimestamp = DateTime.UtcNow
            },
            new AIDecision
            {
                ShouldTrade = true,
                Action = "LONG",
                Symbol = "ETH",
                ConfidenceScore = 100
            },
            "0xabc",
            walletBalanceUsd: 10_000m);

        var state = await db.AiBiasStates.SingleAsync();
        var evt = await db.AiDecisionEvents.SingleAsync();

        Assert.Equal(20m, state.BiasScore);
        Assert.Equal("BULLISH", state.Direction);
        Assert.Equal(20m, evt.BiasDelta);
        Assert.Contains("ETH", state.SymbolWeightsJson);
    }

    private static WhaleTrackerDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<WhaleTrackerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new WhaleTrackerDbContext(options);
    }
}
