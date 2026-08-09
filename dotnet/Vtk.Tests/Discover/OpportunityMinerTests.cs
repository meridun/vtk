using Vtk.Core.Discover;
using Vtk.Core.Session;
using Vtk.Core.Spool;

namespace Vtk.Tests.Discover;

/// <summary>
/// Table-driven tests for the discover opportunity miner (#44): command
/// segmentation, vtk/error/cd skips, shipped-filter dedupe (coverage beats
/// rules), single-attribution of compound commands, ranking, and the
/// wrapped-invocation subtraction (#115).
/// </summary>
public class OpportunityMinerTests
{
    /// <summary>Fake coverage probe: argv matching "git status" or "git log" is covered.</summary>
    private static bool FakeCovered(string[] argv, out string name)
    {
        if (argv.Length >= 2 && argv[0] == "git" && (argv[1] == "status" || argv[1] == "log"))
        {
            name = $"git {argv[1]}";
            return true;
        }
        name = "";
        return false;
    }

    private static CommandEvent Ev(
        string command, string output, bool isError = false, DateTime? at = null) =>
        new(command, output, isError, at);

    public static IEnumerable<object[]> SegmentCases()
    {
        // command, expected segments (argv joined by spaces)
        yield return new object[] { "git status", new[] { "git status" } };
        yield return new object[] { "cd x && git status", new[] { "git status" } };
        yield return new object[] { "git log --oneline | head -5", new[] { "git log --oneline", "head -5" } };
        yield return new object[] { "dotnet test Vtk.sln 2>&1", new[] { "dotnet test Vtk.sln 2>&1" } };
        yield return new object[] { "git commit -m \"a && b\"", new[] { "git commit -m \"a && b\"" } };
        yield return new object[] { "echo 'x; y'", new[] { "echo 'x; y'" } };
        yield return new object[] { "vtk git status && git diff", new[] { "git diff" } };
        yield return new object[] { "FOO=1 npx mocha test", new[] { "mocha test" } };
        yield return new object[] { "a; b\nc", new[] { "a", "b", "c" } };
        yield return new object[] { "", Array.Empty<string>() };
    }

    [Theory]
    [MemberData(nameof(SegmentCases))]
    public void Segments_SplitsQuoteAware_SkipsCdAndVtk(string command, string[] expected)
    {
        var segments = OpportunityMiner.Segments(command).Select(a => string.Join(" ", a));
        Assert.Equal(expected, segments);
    }

    [Fact]
    public void Mine_CoveredShape_ReportsUnwrapped()
    {
        var opps = OpportunityMiner.Mine(
            new[] { Ev("git status", "0123456789"), Ev("cd x && git status", "01234") },
            FakeCovered, DiscoverRules.Default());

        var o = Assert.Single(opps);
        Assert.Equal("git status", o.Name);
        Assert.Equal(OpportunityClass.Unwrapped, o.Class);
        Assert.Equal(2, o.Calls);
        Assert.Equal(15, o.OutputChars);
        Assert.Equal("git status", o.Example);
    }

    [Fact]
    public void Mine_RuleShape_ReportsCandidate()
    {
        var opps = OpportunityMiner.Mine(
            new[] { Ev("dotnet test Vtk.sln", "build output here") },
            FakeCovered, DiscoverRules.Default());

        var o = Assert.Single(opps);
        Assert.Equal("dotnet-test", o.Name);
        Assert.Equal(OpportunityClass.Candidate, o.Class);
        Assert.Equal(1, o.Calls);
        Assert.Equal(17, o.OutputChars);
    }

    [Fact]
    public void Mine_CoverageBeatsRules_DedupesShippedFilters()
    {
        // A rule that would also match a covered shape must not produce a
        // Candidate: coverage is probed first (dedupe against shipped filters).
        var rules = new[]
        {
            new DiscoverRule("git-status-rule",
                new System.Text.RegularExpressions.Regex("^git status"), "hint"),
        };
        var opps = OpportunityMiner.Mine(
            new[] { Ev("git status", "xx") }, FakeCovered, rules);

        var o = Assert.Single(opps);
        Assert.Equal(OpportunityClass.Unwrapped, o.Class);
        Assert.Equal("git status", o.Name);
    }

    [Fact]
    public void Mine_SkipsErrorsAndVtkWrapped()
    {
        var opps = OpportunityMiner.Mine(
            new[]
            {
                Ev("git status", "boom", isError: true),
                Ev("vtk git status", "OK 2e3f"),
                Ev("dotnet build bad.sln", "error", isError: true),
            },
            FakeCovered, DiscoverRules.Default());

        Assert.Empty(opps);
    }

    [Fact]
    public void Mine_CompoundCommand_AttributesOutputOnce()
    {
        // Both segments classify; only the first claims the event.
        var opps = OpportunityMiner.Mine(
            new[] { Ev("git status && dotnet test x", "0123456789") },
            FakeCovered, DiscoverRules.Default());

        var o = Assert.Single(opps);
        Assert.Equal("git status", o.Name);
        Assert.Equal(10, o.OutputChars);
    }

    [Fact]
    public void Mine_RanksByOutputCharsThenCallsThenName()
    {
        var opps = OpportunityMiner.Mine(
            new[]
            {
                Ev("git status", "12345"),
                Ev("dotnet test x", "123456789"),
                Ev("git log", "12345"),
                Ev("git log", ""),
            },
            FakeCovered, DiscoverRules.Default());

        Assert.Equal(3, opps.Count);
        Assert.Equal("dotnet-test", opps[0].Name); // 9 chars
        Assert.Equal("git log", opps[1].Name);     // 5 chars, 2 calls
        Assert.Equal("git status", opps[2].Name);  // 5 chars, 1 call
    }

    [Fact]
    public void Mine_WrappedMatch_SubtractsOneToOne()
    {
        // Two bare `git status` events, one logged vtk invocation: the
        // matched event is subtracted, the other still reports (#115).
        var t = new DateTime(2026, 7, 21, 10, 0, 0, DateTimeKind.Utc);
        var wrapped = new WrappedInvocations(new[]
        {
            new Invocation { Cmd = "git status", Time = t },
        });

        var opps = OpportunityMiner.Mine(
            new[]
            {
                Ev("git status", "0123456789", at: t.AddSeconds(1)),
                Ev("git status", "01234", at: t.AddSeconds(30)),
            },
            FakeCovered, DiscoverRules.Default(), wrapped.TryConsume);

        var o = Assert.Single(opps);
        Assert.Equal("git status", o.Name);
        Assert.Equal(1, o.Calls);
        Assert.Equal(5, o.OutputChars);
        Assert.Equal(1, wrapped.Matched);
    }

    [Fact]
    public void Mine_WrappedSegmentInCompound_LaterSegmentClaimsOutput()
    {
        // The wrapped first segment is skipped like a vtk-prefixed one, so
        // the remaining segment claims the event's output.
        var t = new DateTime(2026, 7, 21, 10, 0, 0, DateTimeKind.Utc);
        var wrapped = new WrappedInvocations(new[]
        {
            new Invocation { Cmd = "git status", Time = t },
        });

        var opps = OpportunityMiner.Mine(
            new[] { Ev("git status && dotnet test x", "0123456789", at: t) },
            FakeCovered, DiscoverRules.Default(), wrapped.TryConsume);

        var o = Assert.Single(opps);
        Assert.Equal("dotnet-test", o.Name);
        Assert.Equal(10, o.OutputChars);
    }

    [Fact]
    public void Mine_NoWrappedLookup_BehavesAsBefore()
    {
        var opps = OpportunityMiner.Mine(
            new[] { Ev("git status", "0123456789") }, FakeCovered, DiscoverRules.Default());

        var o = Assert.Single(opps);
        Assert.Equal(OpportunityClass.Unwrapped, o.Class);
    }

    [Fact]
    public void DefaultRules_MatchExpectedShapes()
    {
        var rules = DiscoverRules.Default();
        Assert.Contains(rules, r => r.Match.IsMatch("dotnet test Vtk.sln"));
        Assert.Contains(rules, r => r.Match.IsMatch("winget install Git.Git"));
        Assert.Contains(rules, r => r.Match.IsMatch("npm ci"));
        Assert.DoesNotContain(rules, r => r.Match.IsMatch("git status"));
        Assert.DoesNotContain(rules, r => r.Match.IsMatch("makeshift-tool run"));
    }
}
