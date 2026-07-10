using Vtk.Core.Filter;
using Xunit;

namespace Vtk.Tests.Filter;

/// <summary>Parity tests against the Go filter's golden fixtures (internal/filter/eslint/testdata).</summary>
public class EslintTests
{
    private static readonly string FixtureDir = Path.Combine(
        AppContext.BaseDirectory, "Filter", "testdata", "eslint");

    [Theory]
    [InlineData("multi_rule", 0.30)]
    [InlineData("big_report", 0.90)]
    public void MatchesGoldenOutput(string name, double minSavings)
    {
        var raw = File.ReadAllText(Path.Combine(FixtureDir, name + ".raw.txt"));
        var want = File.ReadAllText(Path.Combine(FixtureDir, name + ".want.txt"));

        var got = Eslint.Filter(raw);

        Assert.Equal(want, got);
        var savings = 1 - (double)got.Length / raw.Length;
        Assert.True(savings >= minSavings, $"savings {savings:P0} below minimum {minSavings:P0}");
    }

    [Fact]
    public void SummaryLine_Preserved()
    {
        var raw = File.ReadAllText(Path.Combine(FixtureDir, "multi_rule.raw.txt"));
        var got = Eslint.Filter(raw);
        Assert.Contains("7 problems (5 errors, 2 warnings)", got);
    }

    [Fact]
    public void RulesAreRolledUp_HighestCountFirst()
    {
        var raw = File.ReadAllText(Path.Combine(FixtureDir, "multi_rule.raw.txt"));
        var got = Eslint.Filter(raw);
        var lines = got.TrimEnd('\n').Split('\n');
        Assert.Equal(5, lines.Length); // 4 distinct rules + summary
        Assert.StartsWith("3x error semi", lines[0]);
    }

    [Fact]
    public void UnrecognizedInput_PassesThrough()
    {
        const string weird = "some output vtk has never seen\nsecond line\n";
        Assert.Equal(weird, Eslint.Filter(weird));
    }

    [Fact]
    public void EmptyInput_PassesThrough()
    {
        Assert.Equal("", Eslint.Filter(""));
    }
}
