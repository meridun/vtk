using Vtk.Core.Learn;
using Vtk.Core.Session;

namespace Vtk.Tests.Learn;

/// <summary>
/// Fail→succeed pair detection (#43): same-base pairing, flaky-retry
/// exclusion, the lookahead window, and base-command extraction.
/// </summary>
public class CorrectionMinerTests
{
    public static IEnumerable<object[]> BaseCases()
    {
        // command, expectedBase
        yield return new object[] { "git status", "git" };
        yield return new object[] { "cd C:/x && git commit -m msg", "git" };
        yield return new object[] { "cd a && cd b", "cd" };
        yield return new object[] { "FOO=1 npm test", "npm" };
        yield return new object[] { "  dotnet   build  ", "dotnet" };
        yield return new object[] { "", "" };
        yield return new object[] { "   ", "" };
    }

    [Theory]
    [MemberData(nameof(BaseCases))]
    public void BaseCommand_SkipsPrefixes(string command, string expected)
    {
        Assert.Equal(expected, CorrectionMiner.BaseCommand(command));
    }

    private static CommandEvent Ok(string cmd) => new(cmd, "fine", false);
    private static CommandEvent Fail(string cmd, string output = "bash: x: command not found") =>
        new(cmd, output, true);

    [Fact]
    public void FindPairs_PairsFailWithNextSameBaseSuccess()
    {
        var pairs = CorrectionMiner.FindPairs(new[]
        {
            Fail("git satus", "git: 'satus' is not a git command."),
            Ok("ls -la"), // different base, skipped over
            Ok("git status"),
        });

        var p = Assert.Single(pairs);
        Assert.Equal("git satus", p.Wrong);
        Assert.Equal("git status", p.Right);
        Assert.Equal("git", p.Base);
        Assert.Equal(ErrorType.CommandNotFound, p.Error);
    }

    [Fact]
    public void FindPairs_IdenticalRetryIsNotACorrection()
    {
        var pairs = CorrectionMiner.FindPairs(new[]
        {
            Fail("dotnet test", "transient crash"),
            Ok("dotnet test"),
        });
        Assert.Empty(pairs);
    }

    [Fact]
    public void FindPairs_NoSameBaseSuccess_NoPair()
    {
        var pairs = CorrectionMiner.FindPairs(new[]
        {
            Fail("git satus"),
            Ok("npm test"),
        });
        Assert.Empty(pairs);
    }

    [Fact]
    public void FindPairs_SuccessBeyondWindow_NoPair()
    {
        var pairs = CorrectionMiner.FindPairs(new[]
        {
            Fail("git satus"),
            Ok("ls"),
            Ok("pwd"),
            Ok("git status"), // 3rd lookahead, window is 2
        }, window: 2);
        Assert.Empty(pairs);
    }

    [Fact]
    public void FindPairs_ChainedFailures_EachPairsWithTheFix()
    {
        var pairs = CorrectionMiner.FindPairs(new[]
        {
            Fail("git satus", "git: 'satus' is not a git command."),
            Fail("git stauts", "git: 'stauts' is not a git command."),
            Ok("git status"),
        });

        Assert.Equal(2, pairs.Count);
        Assert.Equal("git satus", pairs[0].Wrong);
        Assert.Equal("git stauts", pairs[1].Wrong);
        Assert.All(pairs, p => Assert.Equal("git status", p.Right));
    }

    [Fact]
    public void FindPairs_FirstSameBaseSuccessSettles_NoLaterRepairing()
    {
        // The identical retry succeeds first: the failure is settled as
        // flaky, and a later different same-base command must not pair.
        var pairs = CorrectionMiner.FindPairs(new[]
        {
            Fail("git status"),
            Ok("git status"),
            Ok("git status --short"),
        });
        Assert.Empty(pairs);
    }
}
