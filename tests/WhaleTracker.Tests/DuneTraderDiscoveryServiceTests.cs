using WhaleTracker.Core.Models;
using WhaleTracker.Infrastructure.Services;
using Xunit;

namespace WhaleTracker.Tests;

public class DuneTraderDiscoveryServiceTests
{
    [Fact]
    public void BuildDiscoverySql_DeduplicatesExecutionLegsByTransaction()
    {
        var sql = DuneTraderDiscoveryService.BuildDiscoverySql(new TraderDiscoveryRequest());

        Assert.Contains("FROM dex.trades", sql);
        Assert.Contains("GROUP BY blockchain, tx_hash, wallet", sql);
        Assert.Contains("max(amount_usd)", sql);
        Assert.DoesNotContain("dex_aggregator.trades", sql);
    }

    [Fact]
    public void BuildDiscoverySql_UsesInvariantNumbersAndClampedLimits()
    {
        var request = new TraderDiscoveryRequest
        {
            LookbackDays = 2,
            MinimumActiveWeeks = 99,
            MinimumMeaningfulSwaps = 0,
            MinimumSwapUsd = 1_500.25m,
            CandidateLimit = 5_000
        };

        var sql = DuneTraderDiscoveryService.BuildDiscoverySql(request);

        Assert.Equal(7, request.LookbackDays);
        Assert.Equal(1, request.MinimumActiveWeeks);
        Assert.Equal(1, request.MinimumMeaningfulSwaps);
        Assert.Equal(500, request.CandidateLimit);
        Assert.Contains("amount_usd >= 1500.25", sql);
        Assert.Contains("block_date >=", sql);
        Assert.Contains("LIMIT 500", sql);
    }

    [Fact]
    public void BuildDiscoverySql_RestrictsDiscoveryToCopyableMajors()
    {
        var sql = DuneTraderDiscoveryService.BuildDiscoverySql(new TraderDiscoveryRequest());

        Assert.Contains("'BTC', 'WBTC', 'CBBTC', 'ETH', 'WETH', 'SOL', 'LINK', 'AVAX'", sql);
        Assert.Contains("'USDT', 'USDC', 'DAI', 'USDE'", sql);
        Assert.Contains("FROM labels.addresses", sql);
        Assert.Contains("l.blockchain = s.blockchain", sql);
        Assert.Contains("l.address = s.wallet", sql);
        Assert.Contains("'cex', 'bridge', 'mev', 'bot'", sql);
        Assert.DoesNotContain("'dex', 'dao'", sql);
        Assert.Contains("'diagnostics' AS row_kind", sql);
    }
}
