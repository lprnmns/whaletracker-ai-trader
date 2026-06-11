using WhaleTracker.Core.Models;
using Xunit;

namespace WhaleTracker.Tests;

public class TraderPerformanceMathTests
{
    [Fact]
    public void AdjustedProfit_RemovesExternalDeposits()
    {
        var profit = TraderPerformanceMath.AdjustedProfit(
            startingValue: 100_000m,
            endingValue: 180_000m,
            receivedExternal: 50_000m,
            sentExternal: 10_000m);

        Assert.Equal(40_000m, profit);
        Assert.Equal(40m, TraderPerformanceMath.AdjustedReturnPercent(100_000m, profit));
    }

    [Fact]
    public void Score_CombinesProfitAndReturnWithoutExceedingOneHundred()
    {
        Assert.Equal(100m, TraderPerformanceMath.Score(500_000m, 250m));
        Assert.Equal(0m, TraderPerformanceMath.Score(-1m, -1m));
    }
}
