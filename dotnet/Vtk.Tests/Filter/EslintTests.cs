using System.Text.RegularExpressions;
using Vtk.Core.Filter;
using Xunit;

namespace Vtk.Tests.Filter;

/// <summary>Golden tests for the per-file compact listing (#170) on the fixtures inherited from the Go filter (internal/filter/eslint/testdata).</summary>
public class EslintTests
{
    private static readonly string FixtureDir = Path.Combine(
        AppContext.BaseDirectory, "Filter", "testdata", "eslint");

    [Theory]
    [InlineData("multi_rule", 0.30)]
    [InlineData("big_report", 0.70)]
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

    // Every raw "file:line:col" appears in the compact output (#170
    // acceptance a): walk the listing tracking the current file header and
    // collect each "line:col" token from the packed lines.
    [Theory]
    [InlineData("multi_rule")]
    [InlineData("big_report")]
    public void EveryLocationPresent(string name)
    {
        var raw = File.ReadAllText(Path.Combine(FixtureDir, name + ".raw.txt"));
        var got = Eslint.Filter(raw);

        Assert.Equal(RawLocations(raw), CompactLocations(got));
    }

    [Fact]
    public void LocationsListedPerFileInSourceOrder()
    {
        var raw = File.ReadAllText(Path.Combine(FixtureDir, "multi_rule.raw.txt"));
        var got = Eslint.Filter(raw);
        var lines = got.Split('\n');
        Assert.Equal("src/app.js", lines[0]);
        Assert.Equal("  1:1 no-undef  2:10 semi  5:1 no-unused-vars  12:7 semi", lines[1]);
        Assert.Equal("src/util.js", lines[2]);
        Assert.Equal("  3:5 semi  8:1 no-console  14:3 no-undef", lines[3]);
        Assert.StartsWith("rules: no-undef (error) 'foo' is not defined; semi (error) Missing semicolon; ", lines[^1]);
    }

    [Fact]
    public void PackedLinesWrapAtWidthCap()
    {
        var raw = File.ReadAllText(Path.Combine(FixtureDir, "big_report.raw.txt"));
        var got = Eslint.Filter(raw);
        var packed = got.Split('\n').Where(l => l.StartsWith("  ")).ToList();
        Assert.True(packed.Count > 20, "expected wrapped location lines");
        Assert.All(packed, l => Assert.True(l.Length <= 100, $"line exceeds 100 columns: {l}"));
    }

    [Fact]
    public void MixedSeverityRule_MarksEachLocation()
    {
        const string raw =
            "\nsrc/a.js\n" +
            "   1:1  error    Missing semicolon  semi\n" +
            "   4:2  warning  Missing semicolon  semi\n" +
            "   9:3  error    'x' is not defined  no-undef\n" +
            "\n✖ 3 problems (2 errors, 1 warning)\n";
        var got = Eslint.Filter(raw);
        var lines = got.Split('\n');
        Assert.Equal("  1:1 semi(error)  4:2 semi(warning)  9:3 no-undef", lines[1]);
        Assert.Equal("rules: semi (error/warning) Missing semicolon; no-undef (error) 'x' is not defined", lines[^1]);
    }

    [Fact]
    public void NoRuleId_KeepsSentinel()
    {
        const string raw =
            "src/a.js\n" +
            "   1:1  error  Parsing error: Unexpected token\n" +
            "\n✖ 1 problem (1 error, 0 warnings)\n";
        var got = Eslint.Filter(raw);
        Assert.Contains("  1:1 (no rule)", got);
        Assert.Contains("rules: (no rule) (error) Parsing error: Unexpected token", got);
    }

    private static SortedSet<string> RawLocations(string raw)
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        var file = "";
        foreach (var line in raw.Split('\n'))
        {
            var m = Regex.Match(line, @"^\s+(\d+:\d+)\s+(?:error|warning)\s");
            if (m.Success) { set.Add($"{file}:{m.Groups[1].Value}"); continue; }
            var t = line.Trim();
            if (t != "" && !line.StartsWith(' ') && !t.Contains("problem")) file = t;
        }
        return set;
    }

    private static SortedSet<string> CompactLocations(string got)
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        var file = "";
        foreach (var line in got.Split('\n'))
        {
            if (line.StartsWith("  "))
            {
                foreach (Match m in Regex.Matches(line, @"(?:^|\s)(\d+:\d+) "))
                    set.Add($"{file}:{m.Groups[1].Value}");
                continue;
            }
            if (line != "" && !line.StartsWith("rules: ") && !line.Contains("problem")) file = line;
        }
        return set;
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
