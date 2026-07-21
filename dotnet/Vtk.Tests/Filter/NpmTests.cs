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

    // ---- FoldTail (#93, size-floored fold) ---------------------------------

    [Fact]
    public void FoldTail_RealNpm11NoBannerCapture_MatchesGolden()
    {
        // Captured from real `npm run <script> > file` under npm 11.16.0:
        // modern npm emits its banner only on a TTY, so the strip must not
        // claim the output and the fold tail is the generic summary slice.
        var raw = File.ReadAllText(Path.Combine(FixtureDir, "no_banner_real.raw.txt"));
        Assert.False(Npm.StripBanner(raw).Ok);
        var want = File.ReadAllText(Path.Combine(FixtureDir, "no_banner_real.want.txt"));
        Assert.Equal(want, Npm.FoldTail(raw));
    }

    [Fact]
    public void FoldTail_KeepsAtMostFiveTrailingLines()
    {
        var body = string.Join("\n", Enumerable.Range(1, 8).Select(i => $"line {i}")) + "\n";
        Assert.Equal("line 4\nline 5\nline 6\nline 7\nline 8", Npm.FoldTail(body));
    }

    [Fact]
    public void FoldTail_DropsTrailingBlankLines()
    {
        Assert.Equal("alpha\nbeta", Npm.FoldTail("alpha\nbeta\n\n   \n\n"));
    }

    [Fact]
    public void FoldTail_ByteCapTrimsToFittingLines()
    {
        // 200-char lines: the last two fit (200 + 1 + 200 = 401 <= 512), a
        // third would overflow (602 > 512).
        var wide = new string('x', 200);
        var body = string.Join("\n", Enumerable.Repeat(wide, 5)) + "\n";
        Assert.Equal(wide + "\n" + wide, Npm.FoldTail(body));
    }

    [Fact]
    public void FoldTail_OversizedFinalLine_YieldsEmpty()
    {
        Assert.Equal("", Npm.FoldTail(new string('y', Npm.FoldTailMaxBytes + 1) + "\n"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n\n\n")]
    public void FoldTail_BlankBody_YieldsEmpty(string body)
    {
        Assert.Equal("", Npm.FoldTail(body));
    }
}
