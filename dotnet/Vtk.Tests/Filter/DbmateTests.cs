using Vtk.Core.Filter;
using Xunit;

namespace Vtk.Tests.Filter;

/// <summary>Parity tests against the Go filter's golden fixtures (internal/filter/dbmate/testdata).</summary>
public class DbmateTests
{
    private static readonly string FixtureDir = Path.Combine(
        AppContext.BaseDirectory, "Filter", "testdata", "dbmate");

    [Theory]
    [InlineData("status_mostly_applied", "Status", 0.55)]
    [InlineData("status_all_pending", "Status", 0.0)]
    [InlineData("up_multi", "Migrate", 0.40)]
    [InlineData("rollback_one", "Migrate", 0.40)]
    public void MatchesGoldenOutput(string name, string filterName, double minSavings)
    {
        Func<string, string> filter = filterName switch
        {
            "Status" => Dbmate.Status,
            "Migrate" => Dbmate.Migrate,
            _ => throw new ArgumentException(filterName),
        };
        var raw = File.ReadAllText(Path.Combine(FixtureDir, name + ".raw.txt"));
        var want = File.ReadAllText(Path.Combine(FixtureDir, name + ".want.txt"));
        var got = filter(raw);
        Assert.Equal(want, got);
        var savings = 1 - (double)got.Length / raw.Length;
        Assert.True(savings >= minSavings, $"savings {savings:P0} below minimum {minSavings:P0}");
    }

    [Theory]
    [InlineData("some unrelated output\nwith no status entries\n")]
    public void Status_UnrecognizedShape_PassesThrough(string raw) =>
        Assert.Equal(raw, Dbmate.Status(raw));

    [Fact]
    public void Migrate_Noop_PassesThrough()
    {
        const string raw = "\n\n";
        Assert.Equal(raw, Dbmate.Migrate(raw));
    }

    [Fact]
    public void Migrate_KeepsUnknownLinesVerbatim()
    {
        const string raw = "unexpected output\nwith no result lines\n";
        var got = Dbmate.Migrate(raw);
        Assert.Contains("unexpected output", got);
        Assert.Contains("with no result lines", got);
    }

    [Fact]
    public void Migrate_KeepsErrorAlongsideProgressChatter()
    {
        const string raw =
            "Creating: ./test.sqlite3\n" +
            "Applying: 20260101000001_good.sql\n" +
            "Applied: 20260101000001_good.sql in 2.0008ms\n" +
            "Applying: 20260101000002_bad.sql\n" +
            "Applied: 20260101000002_bad.sql in 0s\n" +
            "Error: near \")\": syntax error\n";
        var got = Dbmate.Migrate(raw);
        Assert.Contains("Error: near", got);
        Assert.DoesNotContain("Creating:", got);
        Assert.DoesNotContain("Applying:", got);
    }
}
