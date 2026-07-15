using Vtk.Core.Analytics;

namespace Vtk.Tests.Analytics;

/// <summary>
/// Table-driven tests for the dollarization helpers (#58): bytes/4 token
/// heuristic and the checked-in model→$ input-price table (decision #46/#58).
/// </summary>
public class EconomicsTests
{
    public static IEnumerable<object[]> TokenCases()
    {
        // bytes, expectedTokens
        yield return new object[] { 0L, 0L };
        yield return new object[] { 3L, 0L };
        yield return new object[] { 4L, 1L };
        yield return new object[] { 4096L, 1024L };
        yield return new object[] { 1_000_000L, 250_000L };
    }

    [Theory]
    [MemberData(nameof(TokenCases))]
    public void TokensFromBytes_UsesBytesOver4(long bytes, long expected)
    {
        Assert.Equal(expected, Economics.TokensFromBytes(bytes));
    }

    public static IEnumerable<object[]> PriceCases()
    {
        // model, usdPerMTokInput
        yield return new object[] { "claude-opus-4.1", 15.00 };
        yield return new object[] { "claude-sonnet-4.5", 3.00 };
        yield return new object[] { "claude-haiku-4.5", 1.00 };
    }

    [Theory]
    [MemberData(nameof(PriceCases))]
    public void PriceFor_MatchesCheckedInTable(string model, double expected)
    {
        Assert.Equal(expected, Economics.PriceFor(model));
    }

    [Fact]
    public void DefaultModel_IsInTheTable()
    {
        Assert.True(Economics.UsdPerMTokInput.ContainsKey(Economics.DefaultModel));
    }

    [Fact]
    public void PriceFor_UnknownModel_FallsBackToDefault()
    {
        Assert.Equal(Economics.PriceFor(Economics.DefaultModel), Economics.PriceFor("gpt-99-mystery"));
    }

    public static IEnumerable<object[]> CostCases()
    {
        // tokens, model, expectedUsd
        yield return new object[] { 1_000_000L, "claude-sonnet-4.5", 3.00 };
        yield return new object[] { 500_000L, "claude-opus-4.1", 7.50 };
        yield return new object[] { 0L, "claude-haiku-4.5", 0.0 };
    }

    [Theory]
    [MemberData(nameof(CostCases))]
    public void CostUsd_PricesTokensAtInputRate(long tokens, string model, double expected)
    {
        Assert.Equal(expected, Economics.CostUsd(tokens, model), precision: 10);
    }

    [Fact]
    public void SavedUsd_ComposesHeuristicAndPrice()
    {
        // 4,000,000 bytes -> 1,000,000 tokens -> $3.00 at the default (sonnet) rate.
        Assert.Equal(3.00, Economics.SavedUsd(4_000_000L), precision: 10);
    }
}
