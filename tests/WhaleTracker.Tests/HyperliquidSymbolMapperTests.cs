using WhaleTracker.Core.Models;
using Xunit;

namespace WhaleTracker.Tests;

public class HyperliquidSymbolMapperTests
{
    [Theory]
    [InlineData("BTC", "BTC")]
    [InlineData("wld", "WLD")]
    [InlineData("kPEPE", "PEPE")]
    [InlineData("kBONK", "BONK")]
    [InlineData("kSHIB", "SHIB")]
    public void ToOkxSymbol_NormalizesSupportedHyperliquidNames(string source, string expected)
    {
        Assert.Equal(expected, HyperliquidSymbolMapper.ToOkxSymbol(source));
    }

    [Theory]
    [InlineData("xyz:TSLA", "XYZ:TSLA")]
    [InlineData("#1010", "#1010")]
    [InlineData("", "")]
    public void ToOkxSymbol_DoesNotPretendNonCryptoMarketsAreOkxSymbols(string source, string expected)
    {
        Assert.Equal(expected, HyperliquidSymbolMapper.ToOkxSymbol(source));
    }
}
