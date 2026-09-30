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
        // `git log -p` joins the hunk fold (#164): per-commit stats shape above the floor.
        yield return new object[] { "log_p_large_real", (Func<string, string>)Git.Log, 0.97 };
        yield return new object[] { "add_crlf_warning", (Func<string, string>)Git.Add, 0.99 };
        yield return new object[] { "commit_multi", (Func<string, string>)Git.Commit, 0.40 };
        yield return new object[] { "push_new_branch", (Func<string, string>)Git.Push, 0.0 };
        yield return new object[] { "push_progress", (Func<string, string>)Git.Push, 0.60 };
        yield return new object[] { "pull_ff", (Func<string, string>)Git.Pull, 0.40 };
        // Registry reuse (#167): `git merge` (real non-ff capture, ort strategy)
        // and `git grep -n` (real capture from this repo) ride Git.Pull and
        // Files.Grep unchanged; only the registry key is new.
        yield return new object[] { "merge_ort", (Func<string, string>)Git.Pull, 0.60 };
        yield return new object[] { "grep_n", (Func<string, string>)Files.Grep, 0.60 };
        // branch (#166): the `-a` reshape (remotes/ stripped, origin twins collapsed)
        // must clear 30% on a typical listing; plain and -r are near-lossless reformats.
        yield return new object[] { "branch_list", (Func<string, string>)Git.Branch, 0.0 };
        yield return new object[] { "branch_all", (Func<string, string>)Git.Branch, 0.30 };
        yield return new object[] { "branch_local", (Func<string, string>)Git.Branch, 0.0 };
        yield return new object[] { "branch_remote", (Func<string, string>)Git.Branch, 0.0 };
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
        yield return new object[] { "log_p_small", (Func<string, string>)Git.Log };
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
    [InlineData("log_p_large_real")]
    public void HunkFixturesSitAtOrAboveTheFloor(string name)
    {
        var raw = File.ReadAllText(Path.Combine(FixtureDir, name + ".raw.txt"));
        Assert.True(raw.Length >= Fold.FloorBytes, $"fixture below the fold floor: {raw.Length} chars");
    }

    [Fact]
    public void LogWithoutHunksCompactsRegardlessOfFloor()
    {
        // Plain `git log` carries no `diff --git` headers, so the #164 floor
        // never applies: a body grown past Fold.FloorBytes still compacts to
        // the same one-line-per-commit output as the small fixture.
        var raw = File.ReadAllText(Path.Combine(FixtureDir, "log_default.raw.txt"));
        var want = File.ReadAllText(Path.Combine(FixtureDir, "log_default.want.txt"));
        var big = raw + "\n    " + new string('x', Fold.FloorBytes);
        Assert.True(big.Length >= Fold.FloorBytes);
        Assert.Equal(want, Git.Log(big));
    }

    [Fact]
    public void LogHunkShapeKeepsCommitsWithoutADiffAsBareLines()
    {
        // A merge commit in `git log -p` has no diff: its entry is the bare
        // one-liner, and the next commit's stats attach to that commit, not
        // to the merge. Grown to the floor so the hunk shape engages.
        var raw = File.ReadAllText(Path.Combine(FixtureDir, "log_p_small.raw.txt"));
        var at = raw + "\n " + new string('x', Fold.FloorBytes - raw.Length - 2);
        var lines = Git.Log(at).Split('\n');
        Assert.StartsWith("ae71d5f Merge pull request", lines[0]);
        Assert.StartsWith("18ab93e test(spool):", lines[1]);
        Assert.Equal("dotnet/Vtk.Tests/Spool/StoreTests.cs | +1 -1", lines[2]);
        Assert.StartsWith("077b716 test(smoke):", lines[3]);
        Assert.Equal("dotnet/Vtk.Tests/Smoke/InvocationLogSmokeTests.cs | +81 -0", lines[4]);
        Assert.Equal(5, lines.Length);
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

    // ---- git branch shape guard (#166): only the plain listing is reshaped ----

    public static IEnumerable<object[]> BranchPassthroughShapes()
    {
        yield return new object[] { "-vv", File.ReadAllText(Path.Combine(FixtureDir, "branch_vv.raw.txt")) };
        yield return new object[] { "--show-current", "dev\n" };
        yield return new object[] { "--format", "dev origin/dev\nmain \n" };
        yield return new object[] { "-d", "Deleted branch topic (was 1a2b3c4).\n" };
        yield return new object[] { "empty", "" };
    }

    [Theory]
    [MemberData(nameof(BranchPassthroughShapes))]
    public void BranchNonListingShapesPassThrough(string shape, string raw)
    {
        Assert.Equal(raw, Git.Branch(raw));
        _ = shape;
    }

    [Fact]
    public void BranchDetachedHeadKeepsTheMarkerLine()
    {
        const string raw = "* (HEAD detached at 9dcbfbb)\n  master\n  remotes/origin/master\n";
        Assert.Equal("* (HEAD detached at 9dcbfbb)\nmaster (tracked)", Git.Branch(raw));
    }

    [Fact]
    public void BranchCollapsesOnlyOriginTwins()
    {
        // A second remote's copy of a local branch stays listed by remote name;
        // the origin/HEAD symref survives the prefix strip.
        const string raw = "* dev\n  remotes/origin/HEAD -> origin/dev\n  remotes/origin/dev\n  remotes/upstream/dev\n";
        Assert.Equal("* dev (tracked)\norigin/HEAD -> origin/dev\nupstream/dev", Git.Branch(raw));
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
