namespace WhaleTracker.Core.Models;

public static class HyperliquidSymbolMapper
{
    private static readonly IReadOnlyDictionary<string, string> Aliases =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["kBONK"] = "BONK",
            ["kFLOKI"] = "FLOKI",
            ["kNEIRO"] = "NEIRO",
            ["kPEPE"] = "PEPE",
            ["kSHIB"] = "SHIB"
        };

    public static string ToOkxSymbol(string? hyperliquidSymbol)
    {
        var value = hyperliquidSymbol?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            return string.Empty;
        }

        return Aliases.TryGetValue(value, out var alias)
            ? alias
            : value.ToUpperInvariant();
    }
}
