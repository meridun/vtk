using Vtk.Core.Filter;
using Xunit;

namespace Vtk.Tests;

/// <summary>
/// Table-driven tests for the binary never-filter predicate (#171):
/// `git archive` and `git bundle create` (any target spelling, after git
/// global-option normalization) pass through under the `binary` reason;
/// every other command — including `git cat-file` and the non-create
/// bundle verbs — falls through to normal dispatch.
/// </summary>
public class BinaryDispatchMatchTests
{
    public static IEnumerable<object[]> Cases()
    {
        // argv, expected, description
        yield return new object[] { new[] { "git", "archive", "HEAD" }, true, "the measured #171 shape" };
        yield return new object[] { new[] { "git", "archive", "--format=zip", "HEAD" }, true, "archive with format" };
        yield return new object[] { new[] { "git", "archive", "-o", "x.zip", "HEAD" }, true, "archive to a file" };
        yield return new object[] { new[] { "git", "archive" }, true, "bare archive (usage error, still binary family)" };
        yield return new object[] { new[] { "git", "-C", "dir", "archive", "HEAD" }, true, "-C global option stripped" };
        yield return new object[] { new[] { "git", "--no-pager", "archive", "HEAD" }, true, "--no-pager stripped" };
        yield return new object[] { new[] { "git", "bundle", "create", "-", "HEAD" }, true, "bundle to stdout" };
        yield return new object[] { new[] { "git", "bundle", "create", "x.bundle", "--all" }, true, "bundle to a file" };
        yield return new object[] { new[] { "git", "-C", "dir", "bundle", "create", "-", "HEAD" }, true, "bundle with global option" };
        yield return new object[] { new[] { "git", "bundle", "verify", "x.bundle" }, false, "bundle verify is text" };
        yield return new object[] { new[] { "git", "bundle", "list-heads", "x.bundle" }, false, "bundle list-heads is text" };
        yield return new object[] { new[] { "git", "bundle" }, false, "bare bundle" };
        yield return new object[] { new[] { "git", "cat-file", "-p", "HEAD" }, false, "cat-file stays out (not classifiable)" };
        yield return new object[] { new[] { "git", "status" }, false, "ordinary git subcommand" };
        yield return new object[] { new[] { "git", "--bare", "archive", "HEAD" }, false, "unknown global option: Strip declines, argv[1] is not a subcommand" };
        yield return new object[] { new[] { "gh", "archive" }, false, "not git" };
        yield return new object[] { new[] { "git" }, false, "bare git" };
        yield return new object[] { System.Array.Empty<string>(), false, "empty argv" };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Matches_DispatchShapes(string[] argv, bool expected, string because)
    {
        Assert.Equal(expected, Binary.Matches(argv));
        _ = because;
    }
}
