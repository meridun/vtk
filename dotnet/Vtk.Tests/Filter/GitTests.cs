using Vtk.Core.Filter;
using Xunit;

namespace Vtk.Tests.Filter;

/// <summary>
/// Parity tests against the Go filter's golden fixtures (internal/filter/git/testdata,
/// copied verbatim into Filter/testdata/git). Byte-exact match with the Go
/// .want.txt output plus the same minimum-savings assertion as git_test.go.
/// </summary>
public class GitTests
{
    private static readonly string FixtureDir = Path.Combine(
        AppContext.BaseDirectory, "Filter", "testdata", "git");

    public static IEnumerable<object[]> Cases()
    {
        yield return new object[] { "status_dirty", (Func<string, string>)Git.Status, 0.75 };
        yield return new object[] { "status_clean", (Func<string, string>)Git.Status, 0.20 };
        yield return new object[] { "log_default", (Func<string, string>)Git.Log, 0.75 };
        yield return new object[] { "diff_two_files", (Func<string, string>)Git.Diff, 0.75 };
        yield return new object[] { "show_commit", (Func<string, string>)Git.Show, 0.80 };
        yield return new object[] { "add_crlf_warning", (Func<string, string>)Git.Add, 0.99 };
        yield return new object[] { "commit_multi", (Func<string, string>)Git.Commit, 0.40 };
        yield return new object[] { "push_new_branch", (Func<string, string>)Git.Push, 0.0 };
        yield return new object[] { "push_progress", (Func<string, string>)Git.Push, 0.60 };
        yield return new object[] { "pull_ff", (Func<string, string>)Git.Pull, 0.40 };
        yield return new object[] { "branch_list", (Func<string, string>)Git.Branch, 0.0 };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void MatchesGoldenOutput(string name, Func<string, string> filter, double minSavings)
    {
        var raw = File.ReadAllText(Path.Combine(FixtureDir, name + ".raw.txt"));
        var want = File.ReadAllText(Path.Combine(FixtureDir, name + ".want.txt"));

        var got = filter(raw);

        Assert.Equal(want, got);
        Assert.NotEmpty(raw);
        var savings = 1 - (double)got.Length / raw.Length;
        Assert.True(savings >= minSavings, $"savings {savings:P0} below minimum {minSavings:P0}");
    }

    [Theory]
    [InlineData("Status")]
    [InlineData("Log")]
    [InlineData("Diff")]
    [InlineData("Show")]
    [InlineData("Commit")]
    public void UnrecognizedInputPassesThrough(string filterName)
    {
        const string weird = "some output vtk has never seen\nsecond line\n";
        Func<string, string> f = filterName switch
        {
            "Status" => Git.Status,
            "Log" => Git.Log,
            "Diff" => Git.Diff,
            "Show" => Git.Show,
            "Commit" => Git.Commit,
            _ => throw new ArgumentException(filterName),
        };
        Assert.Equal(weird, f(weird));
    }
}
