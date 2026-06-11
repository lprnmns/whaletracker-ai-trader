namespace WhaleTracker.Core.Models;

public sealed class TraderFinderRequest
{
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    public decimal MinimumStartingValueUsd { get; set; } = 100_000m;
    public int Top { get; set; } = 10;
    public bool IncludeTrackedWallets { get; set; } = true;
    public List<string> CandidateWallets { get; set; } = new();
}

public sealed class TraderPerformance
{
    public string WalletAddress { get; set; } = string.Empty;
    public decimal StartingValueUsd { get; set; }
    public decimal EndingValueUsd { get; set; }
    public decimal ReceivedExternalUsd { get; set; }
    public decimal SentExternalUsd { get; set; }
    public decimal TotalFeesUsd { get; set; }
    public decimal AdjustedProfitUsd { get; set; }
    public decimal AdjustedReturnPercent { get; set; }
    public decimal RealizedGainUsd { get; set; }
    public decimal Score { get; set; }
    public DateTime StartPointUtc { get; set; }
    public DateTime EndPointUtc { get; set; }
    public string ChartPeriod { get; set; } = string.Empty;
    public string Status { get; set; } = "completed";
    public string Error { get; set; } = string.Empty;
}

public sealed class TraderFinderResult
{
    public int EvaluatedWalletCount { get; set; }
    public int QualifiedWalletCount { get; set; }
    public List<TraderPerformance> Candidates { get; set; } = new();
}

public static class TraderPerformanceMath
{
    public static decimal AdjustedProfit(
        decimal startingValue,
        decimal endingValue,
        decimal receivedExternal,
        decimal sentExternal) =>
        endingValue - startingValue - receivedExternal + sentExternal;

    public static decimal AdjustedReturnPercent(decimal startingValue, decimal adjustedProfit) =>
        startingValue > 0 ? adjustedProfit / startingValue * 100m : 0m;

    public static decimal Score(decimal adjustedProfit, decimal adjustedReturnPercent)
    {
        var profitScore = Math.Clamp(adjustedProfit / 250_000m, 0m, 1m) * 45m;
        var returnScore = Math.Clamp(adjustedReturnPercent / 100m, 0m, 1m) * 55m;
        return Math.Round(profitScore + returnScore, 2);
    }
}
