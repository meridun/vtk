// Port of cmd/vtk/gaps_issues_test.go (#34/#61).
using Vtk.Cli;
using Vtk.Core.Spool;
using Xunit;

namespace Vtk.Tests;

public class GapsIssuesTests
{
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
        var got = GapsIssues.ParseExistingFilterFamilies(titles);
        Assert.Equal(new HashSet<string> { "cargo", "docker", "kubectl" }, got);
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

        var (toFile, skipped) = GapsIssues.PlanFilterIssues(candidates, existing);

        Assert.Equal(new[] { "cargo", "kubectl" }, toFile.Select(g => g.Family));
        Assert.Equal(new[] { "docker" }, skipped);
    }

    /// <summary>A filed title must parse back to its family, or dedupe silently breaks.</summary>
    [Theory]
    [InlineData("cargo")]
    [InlineData("docker")]
    [InlineData("kubectl")]
    public void FilterIssueTitle_RoundTrips(string family)
    {
        var title = GapsIssues.FilterIssueTitle(family);
        Assert.StartsWith(GapsIssues.FilterIssueTitlePrefix, title);
        var got = GapsIssues.ParseExistingFilterFamilies(new[] { title });
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
    public void GapsArgErrors_ExitTwo(params string[] args)
    {
        Assert.Equal(2, Vtk.Cli.Program.Run(args));
    }
}
