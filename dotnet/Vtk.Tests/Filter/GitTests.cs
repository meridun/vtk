using Vtk.Core.Filter;
using Xunit;

namespace Vtk.Tests.Filter;

/// <summary>
/// Parity tests against the Go filter's golden fixtures (internal/filter/git/testdata,
/// copied verbatim into Filter/testdata/git). Byte-exact match with the Go
/// .want.txt output plus the same minimum-savings assertion as git_test.go.
/// The diff/show hunk fold is size-floored (#135): the small Go fixtures now
/// pass through and the golden stats shape is checked against real captures
/// above <see cref="Fold.FloorBytes"/> plus a padded boundary case.
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
        // diff/show fold only at or above Fold.FloorBytes (#135); these two are
        // real captures above it (see HunkFixturesSitAtOrAboveTheFloor).
        yield return new object[] { "diff_large_real", (Func<string, string>)Git.Diff, 0.98 };
        yield return new object[] { "show_large_real", (Func<string, string>)Git.Show, 0.99 };
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

    // ---- Hunk fold floor (#135): below Fold.FloorBytes hunks pass through ----

    public static IEnumerable<object[]> HunkFixtures()
    {
        yield return new object[] { "diff_two_files", (Func<string, string>)Git.Diff };
        yield return new object[] { "show_commit", (Func<string, string>)Git.Show };
    }

    [Theory]
    [MemberData(nameof(HunkFixtures))]
    public void HunkOutputBelowFloorPassesThrough(string name, Func<string, string> filter)
    {
        var raw = File.ReadAllText(Path.Combine(FixtureDir, name + ".raw.txt"));
        Assert.True(raw.Length < Fold.FloorBytes, $"fixture is not below the floor: {raw.Length}");
        Assert.Equal(raw, filter(raw));
    }

    [Theory]
    [MemberData(nameof(HunkFixtures))]
    public void HunkOutputFoldsExactlyAtTheFloor(string name, Func<string, string> filter)
    {
        // Pad the real fixture with one long context line (" " prefix: not a
        // +/- line, so the stats are unchanged) to floor - 1 chars, then add
        // one more: the boundary is "at or above folds".
        var raw = File.ReadAllText(Path.Combine(FixtureDir, name + ".raw.txt"));
        var want = File.ReadAllText(Path.Combine(FixtureDir, name + ".want.txt"));
        var below = raw + "\n " + new string('x', Fold.FloorBytes - raw.Length - 3);
        Assert.Equal(Fold.FloorBytes - 1, below.Length);
        Assert.Equal(below, filter(below));

        var at = below + "x";
        Assert.Equal(Fold.FloorBytes, at.Length);
        Assert.Equal(want, filter(at));
        Assert.True(at.Length - want.Length >= at.Length * 0.99);
    }

    [Theory]
    [InlineData("diff_large_real")]
    [InlineData("show_large_real")]
    public void HunkFixturesSitAtOrAboveTheFloor(string name)
    {
        var raw = File.ReadAllText(Path.Combine(FixtureDir, name + ".raw.txt"));
        Assert.True(raw.Length >= Fold.FloorBytes, $"fixture below the fold floor: {raw.Length} chars");
    }

    [Fact]
    public void StatShapesStillPassThroughRegardlessOfFloor()
    {
        // `git diff --stat` / `--numstat` output has no unified-diff headers:
        // unchanged behavior on both sides of the floor.
        const string stat = " alpha.txt | 1 +\n gamma.txt | 1 +\n 2 files changed, 2 insertions(+)\n";
        Assert.Equal(stat, Git.Diff(stat));
        Assert.Equal(stat, Git.Show(stat));
    }

    [Fact]
    public void ShowHeaderOnlyStillCompacts()
    {
        // `git show -s` / `--stat`: a commit header with no hunks is not the
        // hunk fold and keeps compacting to "hash subject" (+ nothing).
        const string headerOnly = "commit 453dc13aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\nAuthor: A <a@b>\nDate:   Mon Jan 1 00:00:00 2024 +0000\n\n    feat: staged batch\n\n";
        Assert.Equal("453dc13 feat: staged batch", Git.Show(headerOnly));
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
