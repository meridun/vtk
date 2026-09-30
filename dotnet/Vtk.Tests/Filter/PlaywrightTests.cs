using Vtk.Core.Filter;
using Xunit;

namespace Vtk.Tests.Filter;

/// <summary>
/// Fixture tests for the playwright list-reporter fold (#169). Fixtures are
/// real `@playwright/test` 1.55.1 captures under pipe redirection on win32
/// (`ok` / `x` / `-` markers), paths scrubbed.
/// </summary>
public class PlaywrightTests
{
    private static readonly string FixtureDir = Path.Combine(
        AppContext.BaseDirectory, "Filter", "testdata", "playwright");

    private static string Raw(string name) => File.ReadAllText(Path.Combine(FixtureDir, name + ".raw.txt"));

    [Theory]
    [InlineData("green", 0.85)]
    [InlineData("green_color", 0.85)] // FORCE_COLOR=1: every line SGR-wrapped
    [InlineData("green_noproj", 0.85)] // no `projects` in config: no [chromium] tag
    [InlineData("green_stdout", 0.80)] // console output from a test stays inline
    [InlineData("fail", 0.25)] // 2 failures, exit 1
    [InlineData("flaky", 0.45)] // retries: 2, first attempt fails, retry passes
    public void MatchesGoldenOutput(string name, double minSavings)
    {
        var raw = Raw(name);
        var want = File.ReadAllText(Path.Combine(FixtureDir, name + ".want.txt"));

        var got = Playwright.Filter(raw);

        Assert.Equal(want, got);
        var savings = 1 - (double)got.Length / raw.Length;
        Assert.True(savings >= minSavings, $"savings {savings:P0} below minimum {minSavings:P0}");
    }

    [Fact]
    public void PassingAndSkippedLines_FoldedAway()
    {
        var got = Playwright.Filter(Raw("green"));
        Assert.DoesNotContain("  ok ", got);
        Assert.DoesNotContain("  -  ", got);
        Assert.Equal("Running 7 tests using 2 workers\n\n  1 skipped\n  6 passed (686ms)", got);
    }

    [Fact]
    public void ColoredRun_EmittedStripped()
    {
        var raw = Raw("green_color");
        // Ordinal char checks: culture-sensitive string IndexOf treats ESC
        // as ignorable and would "find" it at position 0.
        Assert.True(raw.Contains('\x1b'));
        var got = Playwright.Filter(raw);
        Assert.False(got.Contains('\x1b'));
        Assert.Contains("  6 passed (604ms)", got);
    }

    [Fact]
    public void FailingLines_DetailBlocks_AndSummary_Kept()
    {
        var got = Playwright.Filter(Raw("fail"));
        // Both `x` lines survive, in order, with the passing lines between them gone.
        Assert.Contains("  x   2 [chromium] › tests\\checkout.spec.js:3:3 › checkout › charges the card (6ms)\n  x   9 [chromium]", got);
        Assert.DoesNotContain("  ok ", got);
        // Detail blocks verbatim (ANSI-stripped): header, assertion, snippet, stack.
        Assert.Contains("  1) [chromium] › tests\\checkout.spec.js:3:3 › checkout › charges the card ", got);
        Assert.Contains("    Error: expect(received).toBe(expected) // Object.is equality", got);
        Assert.Contains("    Expected: 11\n    Received: 12", got);
        Assert.Contains("    > 3 |   test('charges the card', async () => { expect(12).toBe(11); });", got);
        Assert.Contains("        at C:\\work\\shop\\tests\\checkout.spec.js:3:53", got);
        Assert.Contains("    Error: gateway timeout", got);
        // Summary block verbatim, listing lines included.
        Assert.EndsWith("  2 failed\n    [chromium] › tests\\checkout.spec.js:3:3 › checkout › charges the card ──────────────────────────\n    [chromium] › tests\\checkout.spec.js:4:3 › checkout › sends a receipt ───────────────────────────\n  1 skipped\n  7 passed (1.6s)", got);
    }

    /// <summary>
    /// A flaky test prints a failing first attempt, its detail block, and a
    /// passing "(retry #1)" line. The failing attempt and block stay; the
    /// passing retry folds like any pass — the `1 flaky` listing names it.
    /// </summary>
    [Fact]
    public void FlakyRetry_FirstAttemptAndListingKept_PassingRetryFolded()
    {
        var raw = Raw("flaky");
        Assert.Contains("(retry #1)", raw);
        var got = Playwright.Filter(raw);
        Assert.Contains("  x  1 [chromium] › tests\\flaky.spec.js:2:1 › loads the map on retry (5ms)", got);
        Assert.Contains("  1) [chromium] › tests\\flaky.spec.js:2:1 › loads the map on retry ", got);
        Assert.DoesNotContain("(retry #1)", got);
        Assert.Contains("  1 flaky\n    [chromium] › tests\\flaky.spec.js:2:1 › loads the map on retry ", got);
        Assert.EndsWith("  1 skipped\n  6 passed (1.1s)", got);
    }

    [Fact]
    public void TestStdout_StaysInline()
    {
        var got = Playwright.Filter(Raw("green_stdout"));
        Assert.Contains("seeded 3 rows\nwarn: slow query\n", got);
    }

    /// <summary>
    /// The line and dot reporters end in the same summary block but print
    /// no per-test list lines, so nothing folds and the input passes
    /// through byte-identical (cursor-control ANSI included) — reporter
    /// passthrough is by content, not by parsing `--reporter`.
    /// </summary>
    [Theory]
    [InlineData("line")]
    [InlineData("dot")]
    public void OtherReporters_PassThroughByteIdentical(string name)
    {
        var raw = Raw(name);
        Assert.Matches(@"\d+ passed \(", raw);
        Assert.Equal(raw, Playwright.Filter(raw));
    }

    /// <summary>A fatal run ("Error: No tests found", exit 1) carries no summary block and returns byte-identical — the content gate for the {0, 1} allowlist.</summary>
    [Fact]
    public void FatalNoSummary_PassesThroughByteIdentical()
    {
        var raw = Raw("fatal");
        Assert.DoesNotMatch(@"\d+\s+(passed|failed|flaky|skipped)", raw);
        Assert.Equal(raw, Playwright.Filter(raw));
    }

    [Fact]
    public void UnrecognizedInput_PassesThrough()
    {
        const string weird = "some output vtk has never seen\nsecond line\n";
        Assert.Equal(weird, Playwright.Filter(weird));
    }

    /// <summary>A summary-shaped tail alone (json-ish or hand-rolled output with "3 passed" text) with no list lines to fold is not touched.</summary>
    [Fact]
    public void SummaryWithoutListLines_PassesThrough()
    {
        const string s = "build ok\n  3 passed (1s)\n";
        Assert.Equal(s, Playwright.Filter(s));
    }

    [Fact]
    public void EmptyInput_PassesThrough()
    {
        Assert.Equal("", Playwright.Filter(""));
    }
}
