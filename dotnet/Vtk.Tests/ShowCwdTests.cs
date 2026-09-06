using Xunit;

namespace Vtk.Tests;

/// <summary>
/// #136: `vtk show` warns when the spool header's cwd differs from the
/// caller's. The comparison is path text only (full path, trailing
/// separators trimmed, case-insensitive on Windows) and never touches the
/// filesystem, so a deleted worktree still compares.
/// </summary>
public class ShowCwdTests
{
    public static IEnumerable<object[]> Cases()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "vtk-wt"));
        var sep = Path.DirectorySeparatorChar;
        yield return new object[] { root, root, true, "identical" };
        yield return new object[] { root, root + sep, true, "trailing separator ignored" };
        yield return new object[] { root, root + sep + "136", false, "child directory differs" };
        yield return new object[] { root + sep + "135", root + sep + "136", false, "sibling worktrees differ" };
        yield return new object[] { root + sep + "136" + sep + ".." + sep + "136", root + sep + "136", true, "dot segments normalized" };
        yield return new object[] { root.ToUpperInvariant(), root.ToLowerInvariant(), OperatingSystem.IsWindows(), "case folds on Windows only" };
        yield return new object[] { "/gone/worktree", "/gone/worktree", true, "nonexistent path still compares by text" };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void SameDirectory_ComparesPathText(string a, string b, bool expected, string because)
    {
        Assert.Equal(expected, Vtk.Cli.Program.SameDirectory(a, b));
        _ = because;
    }
}
