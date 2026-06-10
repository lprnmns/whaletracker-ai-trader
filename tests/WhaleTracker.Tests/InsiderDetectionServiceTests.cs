using WhaleTracker.Core.Models;
using WhaleTracker.Infrastructure.Services;
using Xunit;

namespace WhaleTracker.Tests;

public class InsiderDetectionServiceTests
{
    [Fact]
    public void Analyze_FindsWalletThatSellsBeforeCrashAndBuysDip()
    {
        var service = new InsiderDetectionService();
        var request = new InsiderDetectionRequest
        {
            PreCrashStartUtc = DateTime.Parse("2025-10-10T14:00:00Z").ToUniversalTime(),
            PreCrashEndUtc = DateTime.Parse("2025-10-10T20:50:00Z").ToUniversalTime(),
            DipBuyStartUtc = DateTime.Parse("2025-10-10T21:30:00Z").ToUniversalTime(),
            DipBuyEndUtc = DateTime.Parse("2025-10-11T03:00:00Z").ToUniversalTime(),
            MinimumProfitUsd = 1000m,
            Swaps =
            {
                new HistoricalSwap
                {
                    WalletAddress = "0xABC0000000000000000000000000000000000001",
                    TxHash = "0xsell",
                    TimestampUtc = DateTime.Parse("2025-10-10T20:35:00Z").ToUniversalTime(),
                    TokenInSymbol = "WETH",
                    TokenInAmount = 100m,
                    TokenOutSymbol = "USDC",
                    TokenOutAmount = 420000m,
                    UsdValue = 420000m
                },
                new HistoricalSwap
                {
                    WalletAddress = "0xabc0000000000000000000000000000000000001",
                    TxHash = "0xbuy",
                    TimestampUtc = DateTime.Parse("2025-10-10T21:42:00Z").ToUniversalTime(),
                    TokenInSymbol = "USDC",
                    TokenInAmount = 300000m,
                    TokenOutSymbol = "WETH",
                    TokenOutAmount = 100m,
                    UsdValue = 300000m
                },
                new HistoricalSwap
                {
                    WalletAddress = "0xnoise000000000000000000000000000000000001",
                    TxHash = "0xnoise",
                    TimestampUtc = DateTime.Parse("2025-10-10T20:40:00Z").ToUniversalTime(),
                    TokenInSymbol = "USDC",
                    TokenInAmount = 1000m,
                    TokenOutSymbol = "USDT",
                    TokenOutAmount = 999m,
                    UsdValue = 1000m
                }
            }
        };

        var result = service.Analyze(request);

        Assert.Equal(3, result.ScannedSwapCount);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("0xabc0000000000000000000000000000000000001", candidate.WalletAddress);
        Assert.Equal("ETH", candidate.AssetSymbol);
        Assert.Equal(120000m, candidate.EstimatedProfitUsd);
        Assert.Contains("0xsell", candidate.EvidenceTxHashes);
        Assert.Contains("0xbuy", candidate.EvidenceTxHashes);
    }

    [Fact]
    public void Analyze_IgnoresUnprofitableRoundTrips()
    {
        var service = new InsiderDetectionService();
        var request = new InsiderDetectionRequest
        {
            PreCrashStartUtc = DateTime.Parse("2025-10-10T14:00:00Z").ToUniversalTime(),
            PreCrashEndUtc = DateTime.Parse("2025-10-10T20:50:00Z").ToUniversalTime(),
            DipBuyStartUtc = DateTime.Parse("2025-10-10T21:30:00Z").ToUniversalTime(),
            DipBuyEndUtc = DateTime.Parse("2025-10-11T03:00:00Z").ToUniversalTime(),
            MinimumProfitUsd = 1000m,
            Swaps =
            {
                new HistoricalSwap
                {
                    WalletAddress = "0xabc",
                    TxHash = "0xsell",
                    TimestampUtc = DateTime.Parse("2025-10-10T20:35:00Z").ToUniversalTime(),
                    TokenInSymbol = "ETH",
                    TokenInAmount = 1m,
                    TokenOutSymbol = "USDC",
                    TokenOutAmount = 3000m,
                    UsdValue = 3000m
                },
                new HistoricalSwap
                {
                    WalletAddress = "0xabc",
                    TxHash = "0xbuy",
                    TimestampUtc = DateTime.Parse("2025-10-10T21:42:00Z").ToUniversalTime(),
                    TokenInSymbol = "USDC",
                    TokenInAmount = 3500m,
                    TokenOutSymbol = "ETH",
                    TokenOutAmount = 1m,
                    UsdValue = 3500m
                }
            }
        };

        var result = service.Analyze(request);

        Assert.Empty(result.Candidates);
    }
}
