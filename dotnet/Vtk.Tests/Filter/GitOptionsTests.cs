using Vtk.Core.Filter;
using Xunit;

namespace Vtk.Tests.Filter;

public class GitOptionsTests
{
    public static TheoryData<string[], string[]> StripCases() => new()
    {
        // the dogfooding-evidenced shapes (#135 / #152)
        { new[] { "git", "-C", "dir", "status" }, new[] { "git", "status" } },
        { new[] { "git", "-c", "core.quotepath=off", "commit", "-m", "x" }, new[] { "git", "commit", "-m", "x" } },
        { new[] { "git", "--no-pager", "diff" }, new[] { "git", "diff" } },
        { new[] { "git", "--git-dir=.git", "branch", "-a" }, new[] { "git", "branch", "-a" } },
        // value as the following token
        { new[] { "git", "--git-dir", ".git", "push" }, new[] { "git", "push" } },
        { new[] { "git", "--work-tree", "wt", "pull" }, new[] { "git", "pull" } },
        { new[] { "git", "--work-tree=wt", "add", "." }, new[] { "git", "add", "." } },
        // pager flags
        { new[] { "git", "-P", "log" }, new[] { "git", "log" } },
        { new[] { "git", "-p", "show" }, new[] { "git", "show" } },
        { new[] { "git", "--paginate", "status" }, new[] { "git", "status" } },
        // stacked options
        {
            new[] { "git", "-C", "dir", "-c", "a=b", "--no-pager", "status", "--short" },
            new[] { "git", "status", "--short" }
        },
        // subcommand options after the subcommand are untouched
        { new[] { "git", "-C", "dir", "status", "-C" }, new[] { "git", "status", "-C" } },
    };

    [Theory]
    [MemberData(nameof(StripCases))]
    public void Strip_NormalizesGlobalOptions(string[] argv, string[] want)
    {
        Assert.Equal(want, GitOptions.Strip(argv));
    }

    public static TheoryData<string[]> NoStripCases() => new()
    {
        // already bare: nothing to do
        { new[] { "git", "status" } },
        { new[] { "git", "status", "--short" } },
        // not git
        { new[] { "gh", "-R", "o/r", "issue", "list" } },
        // unknown leading option: don't guess
        { new[] { "git", "--bare", "status" } },
        { new[] { "git", "-C", "dir", "--exec-path=x", "status" } },
        // value-taking option without its value
        { new[] { "git", "-C" } },
        { new[] { "git", "-c", "a=b" } },
        // options only, no subcommand
        { new[] { "git", "--no-pager" } },
        { new[] { "git", "-C", "dir", "--no-pager" } },
        // degenerate
        { new[] { "git" } },
        { System.Array.Empty<string>() },
    };

    [Theory]
    [MemberData(nameof(NoStripCases))]
    public void Strip_LeavesOtherShapesUnchanged(string[] argv)
    {
        Assert.Same(argv, GitOptions.Strip(argv));
    }

    /// <summary>The executed argv is never touched: Strip returns a new array and leaves its input intact.</summary>
    [Fact]
    public void Strip_DoesNotMutateInput()
    {
        var argv = new[] { "git", "-C", "dir", "status" };
        var copy = (string[])argv.Clone();
        GitOptions.Strip(argv);
        Assert.Equal(copy, argv);
    }
}
