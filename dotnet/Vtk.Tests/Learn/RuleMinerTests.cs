using Vtk.Core.Learn;

namespace Vtk.Tests.Learn;

/// <summary>
/// Rule dedupe, occurrence counting, the 1−0.5^n confidence curve, and the
/// threshold filters behind --min-confidence/--min-occurrences (#43).
/// </summary>
public class RuleMinerTests
{
    private static CorrectionPair Pair(string wrong, string right, ErrorType e = ErrorType.CommandNotFound) =>
        new(wrong, right, CorrectionMiner.BaseCommand(wrong), e);

    [Fact]
    public void Mine_DedupesAndCounts()
    {
        var rules = RuleMiner.Mine(new[]
        {
            Pair("git satus", "git status"),
            Pair("git satus", "git status"),
            Pair("npm run lint --ext .ts", "npm run lint -- --ext .ts", ErrorType.UnknownFlag),
        }, minConfidence: 0, minOccurrences: 1);

        Assert.Equal(2, rules.Count);
        // More occurrences sorts first.
        Assert.Equal("git satus", rules[0].Wrong);
        Assert.Equal(2, rules[0].Occurrences);
        Assert.Equal(0.75, rules[0].Confidence, precision: 10);
        Assert.Equal(ErrorType.CommandNotFound, rules[0].Error);
        Assert.Equal(1, rules[1].Occurrences);
        Assert.Equal(0.5, rules[1].Confidence, precision: 10);
    }

    [Fact]
    public void Mine_MinOccurrencesDropsSingletons()
    {
        var rules = RuleMiner.Mine(new[]
        {
            Pair("git satus", "git status"),
            Pair("git satus", "git status"),
            Pair("npm run l", "npm run lint"),
        }, minConfidence: 0, minOccurrences: 2);

        var r = Assert.Single(rules);
        Assert.Equal("git satus", r.Wrong);
    }

    [Fact]
    public void Mine_MinConfidenceDropsWeakRules()
    {
        var rules = RuleMiner.Mine(new[]
        {
            Pair("git satus", "git status"),
            Pair("git satus", "git status"),
            Pair("npm run l", "npm run lint"),
        }, minConfidence: 0.6, minOccurrences: 1);

        var r = Assert.Single(rules); // 0.5 singleton dropped, 0.75 kept
        Assert.Equal(2, r.Occurrences);
    }

    [Fact]
    public void Mine_TieBreaksOnWrongOrdinal()
    {
        var rules = RuleMiner.Mine(new[]
        {
            Pair("b cmd", "b fixed"),
            Pair("a cmd", "a fixed"),
        }, minConfidence: 0, minOccurrences: 1);

        Assert.Equal("a cmd", rules[0].Wrong);
        Assert.Equal("b cmd", rules[1].Wrong);
    }

    [Fact]
    public void Mine_SameWrongDifferentRight_AreSeparateRules()
    {
        var rules = RuleMiner.Mine(new[]
        {
            Pair("git satus", "git status"),
            Pair("git satus", "git status --short"),
        }, minConfidence: 0, minOccurrences: 1);

        Assert.Equal(2, rules.Count);
    }
}
