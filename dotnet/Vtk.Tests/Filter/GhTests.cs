using Vtk.Core.Filter;
using Xunit;

namespace Vtk.Tests.Filter;

/// <summary>Parity tests against the Go filter's golden fixtures (internal/filter/gh/testdata).</summary>
public class GhTests
{
    private static readonly string FixtureDir = Path.Combine(
        AppContext.BaseDirectory, "Filter", "testdata", "gh");

    // Fixed clock matching gh_test.go's `ref`: just after the newest fixture
    // timestamp (2026-07-06) so ages stay stable.
    private static readonly DateTime Ref = new(2026, 7, 6, 21, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("issue_list", 0.30)]
    public void IssueList_MatchesGoldenOutput(string name, double minSavings) =>
        AssertGolden(name, raw => Gh.IssueListAt(raw, Ref), minSavings);

    [Theory]
    [InlineData("pr_list", 0.40)]
    public void PrList_MatchesGoldenOutput(string name, double minSavings) =>
        AssertGolden(name, raw => Gh.PrListAt(raw, Ref), minSavings);

    [Theory]
    [InlineData("run_list", 0.45)]
    public void RunList_MatchesGoldenOutput(string name, double minSavings) =>
        AssertGolden(name, raw => Gh.RunListAt(raw, Ref), minSavings);

    private static void AssertGolden(string name, Func<string, string> filter, double minSavings)
    {
        var raw = File.ReadAllText(Path.Combine(FixtureDir, name + ".raw.txt"));
        var want = File.ReadAllText(Path.Combine(FixtureDir, name + ".want.txt"));
        var got = filter(raw);
        Assert.Equal(want, got);
        var savings = 1 - (double)got.Length / raw.Length;
        Assert.True(savings >= minSavings, $"savings {savings:P0} below minimum {minSavings:P0}");
    }

    // CI-log fold (#96): golden fixtures captured live from real public CI
    // runs (fastify/fastify run 29506411975 for --log and --log-failed;
    // expressjs/express run 29218121905 coverage job, trimmed head+tail, for
    // the no-##[error] failure case). run_log holds three jobs, one failing
    // with an ##[error] marker; run_log_failed is that failing job alone;
    // run_log_failed_nomarker is a failed job whose log carries no marker
    // (HTTP 504 death), exercising the last-lines tail guard.
    [Theory]
    [InlineData("run_log", 0.80)]
    [InlineData("run_log_failed", 0.35)]
    [InlineData("run_log_failed_nomarker", 0.85)]
    public void RunLog_MatchesGoldenOutput(string name, double minSavings) =>
        AssertGolden(name, Gh.Run, minSavings);

    /// <summary>The Run dispatcher routes run-list tables to RunList, not the log fold.</summary>
    [Fact]
    public void Run_RunListShape_IsNotLogShaped()
    {
        var raw = File.ReadAllText(Path.Combine(FixtureDir, "run_list.raw.txt"));
        Assert.False(Gh.TryRunLogFold(raw, out _));
    }

    /// <summary>A short or mixed-shape input never engages the log fold (ambiguous → pass through).</summary>
    [Theory]
    [InlineData("job\tstep\t2026-07-16T14:24:08.7583378Z only one row\n")]
    [InlineData("✓ main CI · 1234567\nTriggered via push about 1 minute ago\n")]
    [InlineData("")]
    public void TryRunLogFold_RejectsNonLogInput(string raw) =>
        Assert.False(Gh.TryRunLogFold(raw, out _));

    [Fact]
    public void JsonPayload_PassesThroughUnchanged()
    {
        var raw = File.ReadAllText(Path.Combine(FixtureDir, "json_passthrough.raw.txt"));
        Assert.Equal(raw, Gh.IssueList(raw));
        Assert.Equal(raw, Gh.PrList(raw));
        Assert.Equal(raw, Gh.RunList(raw));
        Assert.Equal(raw, Gh.Run(raw));
    }

    [Theory]
    [InlineData("some output vtk has never seen\nsecond line\n")]
    [InlineData("title:\tFix the bug\nstate:\tOPEN\nnumber:\t9\n")]
    [InlineData("")]
    [InlineData("notanumber\tOPEN\tTitle\tlabel\t2026-07-06T18:00:00Z\n")]
    public void UnrecognizedInput_PassesThrough(string raw)
    {
        Assert.Equal(raw, Gh.IssueList(raw));
        Assert.Equal(raw, Gh.PrList(raw));
        Assert.Equal(raw, Gh.RunList(raw));
        Assert.Equal(raw, Gh.Run(raw));
    }

    [Theory]
    [InlineData("2026-07-06T20:59:30Z", "now")]
    [InlineData("2026-07-06T20:30:00Z", "30m")]
    [InlineData("2026-07-06T18:00:00Z", "3h")]
    [InlineData("2026-07-04T21:00:00Z", "2d")]
    [InlineData("2026-05-06T21:00:00Z", "2mo")]
    [InlineData("2024-07-06T21:00:00Z", "2y")]
    [InlineData("not-a-timestamp", "")]
    [InlineData("2027-01-01T00:00:00Z", "now")] // future clamps to now
    public void Rel_FormatsRelativeAge(string ts, string want)
    {
        // Rel is private; exercised indirectly through IssueListAt with a
        // single-row fixture carrying the timestamp in field 4.
        var raw = $"9\tOPEN\tTitle\tlabel\t{ts}\n";
        var got = Gh.IssueListAt(raw, Ref);
        var expectedLine = want == "" ? "#9 open Title" : $"#9 open Title ({want})";
        Assert.Equal(expectedLine, got);
    }
}
