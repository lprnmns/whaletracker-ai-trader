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
        Assert.Contains("FROM dex.sandwiches", sql);
        Assert.Contains("l.blockchain = s.blockchain", sql);
        Assert.Contains("l.address = s.wallet", sql);
        Assert.Contains("'cex', 'bridge', 'mev', 'bot'", sql);
        Assert.Contains("'contract', 'token_contract', 'oracle'", sql);
        Assert.DoesNotContain("'dex', 'defi'", sql);
        Assert.Contains("'diagnostics' AS row_kind", sql);
        Assert.Contains("maximum_daily_swaps <= 50", sql);
        Assert.Contains("copyability_score", sql);
        Assert.Contains("current_copyable_value_usd", sql);
        Assert.Contains("tokens_ethereum.balances", sql);
        Assert.Contains("current_copyable_token_whitelist", sql);
        Assert.Contains("a0b86991c6218b36c1d19d4a2e9eb0ce3606eb48", sql);
        Assert.Contains("coalesce(lower(to_hex(b.token_address))", sql);
        Assert.Contains("current_copyable_value_usd, 0) BETWEEN 100000 AND 100000000", sql);
    }
}
