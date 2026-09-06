// Port of cmd/vtk/gaps_issues_test.go (#34/#61).
using Vtk.Cli;
using Vtk.Core.Spool;
using Xunit;

namespace Vtk.Tests;

public class GapsIssuesTests
{
    private static readonly IReadOnlySet<string> PairKeyed = new HashSet<string> { "git", "gh", "npx" };
    private static readonly IReadOnlySet<string> NoPairs = new HashSet<string>();

    [Fact]
    public void ParseExistingFilterFamilies_ExtractsFamilies()
    {
        var titles = new[]
        {
            "Filter: cargo (auto-filed from vtk gaps)",
            "Filter: docker",
            "Filter:   kubectl (manual)", // extra spaces tolerated
            "Filter: (auto-filed from vtk gaps)",
            "Add a filter for cargo", // not a Filter issue
            "filter: lowercase",      // prefix is case-sensitive
            "",
        };
        var got = GapsIssues.ParseExistingFilterFamilies(titles, NoPairs);
        Assert.Equal(new HashSet<string> { "cargo", "docker", "kubectl" }, got);
    }

    /// <summary>
    /// #139: a pair-keyed first token takes its subcommand into the family so
    /// dedupe keys match the pair-grained aggregation; an umbrella
    /// `Filter: git` no longer suppresses `git rev-parse`, and non-pair tools
    /// keep the first token even when the title carries more words.
    /// </summary>
    [Fact]
    public void ParseExistingFilterFamilies_IsPairAwareForPairKeyedCommands()
    {
        var titles = new[]
        {
            "Filter: git rev-parse (auto-filed from vtk gaps)",
            "Filter: gh api",
            "Filter: git",                         // umbrella: bare family, no second token
            "Filter: npx (auto-filed from vtk gaps)", // paren stops the family
            "Filter: cargo build (manual)",        // cargo is not pair-keyed
        };
        var got = GapsIssues.ParseExistingFilterFamilies(titles, PairKeyed);
        Assert.Equal(new HashSet<string> { "git rev-parse", "gh api", "git", "npx", "cargo" }, got);
    }

    [Fact]
    public void PlanFilterIssues_SplitsFileVersusSkip()
    {
        var candidates = new List<GapSummary>
        {
            new() { Family = "cargo", Calls = 3, RawBytes = 30000 },
            new() { Family = "docker", Calls = 4, RawBytes = 60000 },
            new() { Family = "kubectl", Calls = 2, RawBytes = 20000 },
        };
        var existing = new HashSet<string> { "docker" };

        var (toFile, skipped, dispatchGaps) = GapsIssues.PlanFilterIssues(candidates, existing, _ => false);

        Assert.Equal(new[] { "cargo", "kubectl" }, toFile.Select(g => g.Family));
        Assert.Equal(new[] { "docker" }, skipped);
        Assert.Empty(dispatchGaps);
    }

    /// <summary>
    /// #139: a family that already resolves in the registry is a dispatch
    /// miss, not a missing filter — it is reported as a dispatch gap and
    /// never proposed or deduped, even when an open Filter issue names it.
    /// </summary>
    [Fact]
    public void PlanFilterIssues_ReportsResolvingFamiliesAsDispatchGaps()
    {
        var candidates = new List<GapSummary>
        {
            new() { Family = "git diff", Calls = 232, RawBytes = 900000 },
            new() { Family = "git rev-parse", Calls = 1312, RawBytes = 120000 },
            new() { Family = "git status", Calls = 5, RawBytes = 50000 },
        };
        var existing = new HashSet<string> { "git status" };
        var registry = new HashSet<string> { "git diff", "git status" };

        var (toFile, skipped, dispatchGaps) = GapsIssues.PlanFilterIssues(candidates, existing, registry.Contains);

        Assert.Equal(new[] { "git rev-parse" }, toFile.Select(g => g.Family));
        Assert.Empty(skipped);
        Assert.Equal(new[] { "git diff", "git status" }, dispatchGaps.Select(g => g.Family));
    }

    /// <summary>A filed title must parse back to its family, or dedupe silently breaks.</summary>
    [Theory]
    [InlineData("cargo")]
    [InlineData("docker")]
    [InlineData("kubectl")]
    [InlineData("git rev-parse")]
    [InlineData("gh api")]
    [InlineData("npx tsc")]
    public void FilterIssueTitle_RoundTrips(string family)
    {
        var title = GapsIssues.FilterIssueTitle(family);
        Assert.StartsWith(GapsIssues.FilterIssueTitlePrefix, title);
        var got = GapsIssues.ParseExistingFilterFamilies(new[] { title }, PairKeyed);
        Assert.Contains(family, got);
    }

    [Fact]
    public void FilterIssueBody_CarriesMetadataOnly()
    {
        var g = new GapSummary { Family = "cargo", Calls = 7, RawBytes = 123456 };
        var body = GapsIssues.FilterIssueBody(g, 51200, 3);
        foreach (var want in new[] { "cargo", "7", "123456", "min-bytes=51200", "min-calls=3", "Exit-code parity" })
        {
            Assert.Contains(want, body);
        }
        Assert.DoesNotContain("Window:", body);
    }

    [Fact]
    public void FilterIssueBody_WithWindow_RecordsSinceBound()
    {
        // #140: a windowed measurement says so in the filed body (metadata
        // only — a UTC timestamp, never output content).
        var g = new GapSummary { Family = "cargo", Calls = 7, RawBytes = 123456 };
        var body = GapsIssues.FilterIssueBody(g, 51200, 3, new DateTime(2026, 8, 22, 0, 0, 0, DateTimeKind.Utc));
        Assert.Contains("- Window: since 2026-08-22T00:00:00Z", body);
    }

    /// <summary>
    /// Pins the usage-error exits for malformed `vtk gaps` args. All cases
    /// fail during parse or the pre-file guard — no store open and no gh
    /// shell-out, so the test is hermetic.
    /// </summary>
    [Theory]
    [InlineData("gaps", "--bogus")]
    [InlineData("gaps", "--file-issues", "--min-bytes")]
    [InlineData("gaps", "--file-issues", "--min-bytes", "abc")]
    [InlineData("gaps", "--file-issues", "--min-calls", "x")]
    [InlineData("gaps", "--yes")]
    [InlineData("gaps", "--min-bytes", "1000")]
    [InlineData("gaps", "--since")]
    [InlineData("gaps", "--since", "0d")]
    [InlineData("gaps", "--since", "bogus")]
    [InlineData("gaps", "--since", "999999d")]
    [InlineData("gaps", "--since", "2147483647d")]
    [InlineData("gaps", "--file-issues", "--since", "14")]
    public void GapsArgErrors_ExitTwo(params string[] args)
    {
        Assert.Equal(2, Vtk.Cli.Program.Run(args));
    }
}
