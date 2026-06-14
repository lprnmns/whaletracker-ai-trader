using WhaleTracker.Core.Models;
using Xunit;

namespace WhaleTracker.Tests;

public class HyperliquidCopySizingMathTests
{
    [Fact]
    public void TargetMargin_PreservesNormalizedExposureAtTargetLeverage()
    {
        var target = HyperliquidCopySizingMath.TargetMarginUsdt(
            traderBudgetUsdt: 10m,
            sourcePositionValueUsd: 1_455_648.75771m,
            sourceAccountValueUsd: 663_022.615795m,
            targetLeverage: 10);

        Assert.InRange(target, 2.1954m, 2.1955m);
    }

    [Fact]
    public void TargetMargin_UsesTargetLeverageInsteadOfSourceMarginAllocation()
    {
        var target = HyperliquidCopySizingMath.TargetMarginUsdt(
            traderBudgetUsdt: 10m,
            sourcePositionValueUsd: 2_795_965.49919m,
            sourceAccountValueUsd: 403_969.70667m,
            targetLeverage: 10);

        Assert.InRange(target, 6.9212m, 6.9213m);
    }

    [Fact]
    public void EligiblePositions_DoNotConsumeTheWholeTraderBudget()
    {
        const decimal accountValue = 663_022.615795m;
        var totalTargetMargin =
            HyperliquidCopySizingMath.TargetMarginUsdt(10m, 1_418_344.63542m, accountValue, 10) +
            HyperliquidCopySizingMath.TargetMarginUsdt(10m, 1_455_648.75771m, accountValue, 10) +
            HyperliquidCopySizingMath.TargetMarginUsdt(10m, 43_086.1925m, accountValue, 10);

        Assert.InRange(totalTargetMargin, 4.3996m, 4.3998m);
    }

    [Fact]
    public void PercentHelpers_ExposeSourceRiskAllocation()
    {
        var exposure = HyperliquidCopySizingMath.ExposurePercent(
            1_455_648.75771m,
            663_022.615795m);
        var margin = HyperliquidCopySizingMath.MarginPercent(
            145_564.875771m,
            663_022.615795m);

        Assert.InRange(exposure, 219.54m, 219.55m);
        Assert.InRange(margin, 21.95m, 21.96m);
    }

    [Fact]
    public void MinimumLot_ClosesOnlyWhenItWouldOverAllocateRisk()
    {
        var oversizedMinimum = new OrderCalculation
        {
            IsValid = true,
            ValidationStatus = OrderValidationStatus.ValidWithWarning,
            MarginDeviationPercent = 23m
        };
        var conservativeRoundDown = new OrderCalculation
        {
            IsValid = true,
            ValidationStatus = OrderValidationStatus.ValidWithWarning,
            MarginDeviationPercent = -15m
        };
        var unrelatedFailure = new OrderCalculation
        {
            IsValid = false,
            ValidationStatus = OrderValidationStatus.LeverageTooHigh
        };

        Assert.True(CopyTradingTargetMath.ShouldFlattenForMinimum(
            oversizedMinimum,
            closeIfBelowMinimum: true,
            maximumUpwardMarginDeviationPercent: 10m));
        Assert.False(CopyTradingTargetMath.ShouldFlattenForMinimum(
            conservativeRoundDown,
            closeIfBelowMinimum: true,
            maximumUpwardMarginDeviationPercent: 10m));
        Assert.False(CopyTradingTargetMath.ShouldFlattenForMinimum(
            unrelatedFailure,
            closeIfBelowMinimum: true,
            maximumUpwardMarginDeviationPercent: 10m));
    }
}
