using Vtk.Core.Filter;
using Xunit;

namespace Vtk.Tests.Filter;

/// <summary>Parity tests against the Go filter's fixtures (internal/filter/npm/testdata).</summary>
public class NpmTests
{
    private static readonly string FixtureDir = Path.Combine(
        AppContext.BaseDirectory, "Filter", "testdata", "npm");

    [Theory]
    [InlineData("no_inner_match", new[] { "node", "scripts/sync-claude-config.mjs", "--check" }, "in sync", "isekai-rpg@0.0.1", 40)]
    [InlineData("lint_eslint", new[] { "eslint", ".", "--cache" }, "7 problems", "> eslint . --cache", 40)]
    public void StripBanner_MatchesGoFixture(string name, string[] wantInner, string wantBody, string wantNoBody, int minStripped)
    {
        var raw = File.ReadAllText(Path.Combine(FixtureDir, name + ".raw.txt"));
        var result = Npm.StripBanner(raw);

        Assert.True(result.Ok);
        Assert.Equal(wantInner, result.Inner);
        Assert.Contains(wantBody, result.Body);
        Assert.DoesNotContain(wantNoBody, result.Body);
        Assert.True(raw.Length - result.Body.Length >= minStripped);
    }

    [Fact]
    public void Body_DropsSeparatorBlank()
    {
        const string raw = "> pkg@1.0.0 build\n> tsc -p .\n\nsrc/x.ts: error TS1005\n";
        var result = Npm.StripBanner(raw);
        Assert.True(result.Ok);
        Assert.Equal(new[] { "tsc", "-p", "." }, result.Inner);
        Assert.Equal("src/x.ts: error TS1005\n", result.Body);
    }

    [Fact]
    public void LeadingBlank_Tolerated()
    {
        const string raw = "\n> pkg@1.0.0 lint\n> eslint .\n\nclean\n";
        var result = Npm.StripBanner(raw);
        Assert.True(result.Ok);
        Assert.Equal(new[] { "eslint", "." }, result.Inner);
        Assert.Equal("clean\n", result.Body);
    }

    [Theory]
    [InlineData("")]
    [InlineData("just some output\nno banner here\n")]
    [InlineData("> only one banner line\nbody\n")]
    [InlineData("> pkg@1.0.0 script\n  indented not-a-banner")]
    public void NonBanner_NotClaimed(string raw)
    {
        Assert.False(Npm.StripBanner(raw).Ok);
    }

    [Fact]
    public void EmptyCommandLine_NotClaimed()
    {
        Assert.False(Npm.StripBanner("> pkg@1.0.0 script\n> \nbody\n").Ok);
    }
}
