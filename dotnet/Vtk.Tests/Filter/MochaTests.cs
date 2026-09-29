using Vtk.Core.Filter;
using Xunit;

namespace Vtk.Tests.Filter;

/// <summary>Parity tests against the Go filter's golden fixtures (internal/filter/mocha/testdata).</summary>
public class MochaTests
{
    private static readonly string FixtureDir = Path.Combine(
        AppContext.BaseDirectory, "Filter", "testdata", "mocha");

    [Theory]
    [InlineData("pass", 0.85)]
    [InlineData("pending", 0.60)]
    [InlineData("fail", 0.20)]
    [InlineData("fail2", 0.20)] // real mocha 11.7.5, 2 failing -> exit 2 (#138)
    [InlineData("pass-color", 0.85)] // real mocha 11.x --color, summary wrapped in SGR codes (#163)
    [InlineData("fail-color", 0.40)] // real mocha 11.x --color, 2 failing; detail block opens with ESC[0m (#163)
    public void MatchesGoldenOutput(string name, double minSavings)
    {
        var raw = File.ReadAllText(Path.Combine(FixtureDir, name + ".raw.txt"));
        var want = File.ReadAllText(Path.Combine(FixtureDir, name + ".want.txt"));

        var got = Mocha.Filter(raw);

        Assert.Equal(want, got);
        var savings = 1 - (double)got.Length / raw.Length;
        Assert.True(savings >= minSavings, $"savings {savings:P0} below minimum {minSavings:P0}");
    }

    [Fact]
    public void SummaryCounts_Preserved()
    {
        var raw = File.ReadAllText(Path.Combine(FixtureDir, "fail.raw.txt"));
        var got = Mocha.Filter(raw);
        Assert.Contains("5 passing (6ms)", got);
        Assert.Contains("3 failing", got);
    }

    [Fact]
    public void FailureDetail_KeptVerbatim()
    {
        var raw = File.ReadAllText(Path.Combine(FixtureDir, "fail.raw.txt"));
        var got = Mocha.Filter(raw);
        Assert.Contains("1) cart", got);
        Assert.Contains("AssertionError [ERR_ASSERTION]: 12 == 11", got);
        Assert.Contains("Error: gateway timeout", got);
        Assert.Contains(@"at Context.<anonymous> (test\mixed.test.js:5:45)", got);
    }

    [Fact]
    public void PassingSpecs_FoldedAway()
    {
        var raw = File.ReadAllText(Path.Combine(FixtureDir, "pass.raw.txt"));
        var got = Mocha.Filter(raw);
        Assert.DoesNotContain("✔", got);
        Assert.Equal("7 passing (5ms)", got);
    }

    /// <summary>
    /// mocha with `color: true` (the .mocharc.json shape from #163) emits
    /// the summary as `ESC[92m ESC[0mESC[32m 7 passing ESC[0mESC[90m (3ms)ESC[0m`
    /// and opens each failure block with `ESC[0m  1) ...`. The colored run
    /// must compact to the same shape as the uncolored one — no raw
    /// passthrough, no escape codes in the compact view.
    /// </summary>
    [Fact]
    public void ColoredOutput_MatchesUncoloredShape()
    {
        var pass = File.ReadAllText(Path.Combine(FixtureDir, "pass-color.raw.txt"));
        Assert.Contains("\x1b[32m 7 passing\x1b[0m", pass);
        var got = Mocha.Filter(pass);
        Assert.Equal("7 passing (3ms)", got);
        Assert.DoesNotContain("\x1b", got, StringComparison.Ordinal); // ordinal: ICU culture compare treats ESC as ignorable

        var fail = File.ReadAllText(Path.Combine(FixtureDir, "fail-color.raw.txt"));
        Assert.Contains("\x1b[0m  1) cart", fail);
        got = Mocha.Filter(fail);
        Assert.StartsWith("5 passing (4ms)\n2 failing\n\n  1) cart\n", got);
        Assert.Contains("Error: gateway timeout", got);
        Assert.Contains(@"at Context.<anonymous> (test\mixed.test.js:10:36)", got);
        Assert.DoesNotContain("\x1b", got, StringComparison.Ordinal); // ordinal: ICU culture compare treats ESC as ignorable
    }

    /// <summary>
    /// A fatal/config error (real mocha 11.7.5 "No test files found", exit 1)
    /// carries no summary line: the filter returns it byte-identical. With the
    /// 0..255 allowlist (#138) this content gate is what keeps fatal runs raw.
    /// </summary>
    [Fact]
    public void FatalNoSummary_PassesThroughByteIdentical()
    {
        var raw = File.ReadAllText(Path.Combine(FixtureDir, "fatal.raw.txt"));
        Assert.DoesNotMatch(@"\d+\s+(passing|failing|pending)", raw);
        Assert.Equal(raw, Mocha.Filter(raw));
    }

    [Fact]
    public void UnrecognizedInput_PassesThrough()
    {
        const string weird = "some output vtk has never seen\nsecond line\n";
        Assert.Equal(weird, Mocha.Filter(weird));
    }

    [Fact]
    public void EmptyInput_PassesThrough()
    {
        Assert.Equal("", Mocha.Filter(""));
    }
}
