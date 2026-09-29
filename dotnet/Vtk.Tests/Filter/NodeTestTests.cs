using Vtk.Core.Filter;
using Xunit;

namespace Vtk.Tests.Filter;

/// <summary>
/// Golden-fixture tests for the node --test spec-reporter filter (#168).
/// Fixtures were captured from real `node --test` (v24.18.0) runs of a
/// scratch project; absolute paths scrubbed to C:\proj\shop.
/// </summary>
public class NodeTestTests
{
    private static readonly string FixtureDir = Path.Combine(
        AppContext.BaseDirectory, "Filter", "testdata", "nodetest");

    private static string Raw(string name) =>
        File.ReadAllText(Path.Combine(FixtureDir, name + ".raw.txt"));

    [Theory]
    [InlineData("pass", 0.80)]         // green run, 34 tests: only the summary block survives
    [InlineData("fail", 0.20)]         // 3 failures (exit 1): tree folds, detail blocks kept verbatim
    [InlineData("crash_module", 0.00)] // module-load crash before the summary: stack kept, nothing to fold (0%)
    public void MatchesGoldenOutput(string name, double minSavings)
    {
        var raw = Raw(name);
        var want = File.ReadAllText(Path.Combine(FixtureDir, name + ".want.txt"));

        var got = NodeTest.Filter(raw);

        Assert.Equal(want, got);
        var savings = 1 - (double)got.Length / raw.Length;
        Assert.True(savings >= minSavings, $"savings {savings:P0} below minimum {minSavings:P0}");
    }

    [Fact]
    public void PassingRun_OnlySummaryBlockSurvives()
    {
        var got = NodeTest.Filter(Raw("pass"));
        Assert.DoesNotContain("✔", got);
        Assert.DoesNotContain("▶", got);
        Assert.DoesNotContain("﹣", got);
        Assert.StartsWith("ℹ tests 34\n", got);
        Assert.Contains("\nℹ pass 31\n", got);
        Assert.Contains("\nℹ skipped 2\n", got);
        Assert.EndsWith("ℹ duration_ms 96.3927", got);
    }

    [Fact]
    public void FailingRun_KeepsTreeFailuresSummaryAndDetailVerbatim()
    {
        var got = NodeTest.Filter(Raw("fail"));
        // Tree "✖" lines stay (which suite failed); passing siblings fold.
        Assert.Contains("  ✖ computes the total (0.6799ms)\n", got);
        Assert.Contains("✖ orders (3.2807ms)\n", got);
        Assert.DoesNotContain("creates an order", got);
        Assert.DoesNotContain("▶ orders", got);
        // Summary block verbatim.
        Assert.Contains("ℹ tests 38\nℹ suites 10\nℹ pass 32\nℹ fail 3\n", got);
        // Detail region verbatim: location, assertion, diff, stack.
        Assert.Contains("✖ failing tests:", got);
        Assert.Contains(@"test at test\orders.test.js:6:3", got);
        Assert.Contains("AssertionError [ERR_ASSERTION]: Expected values to be strictly equal:", got);
        Assert.Contains("12 !== 11", got);
        Assert.Contains("Error: gateway timeout", got);
        Assert.Contains("  +     'b'\n  -     'c'\n", got);
        Assert.Contains(@"at TestContext.<anonymous> (C:\proj\shop\test\orders.test.js:9:12)", got);
        Assert.EndsWith("  }", got);
    }

    /// <summary>
    /// A test file that crashes on load prints its stack *before* the
    /// summary (forwarded stderr). Unrecognized lines above the summary stay
    /// inline, so the cause survives — only tree lines fold.
    /// </summary>
    [Fact]
    public void ModuleCrash_KeepsForwardedStderrAboveSummary()
    {
        var got = NodeTest.Filter(Raw("crash_module"));
        Assert.StartsWith("node:internal/modules/cjs/loader:1520\n", got);
        Assert.Contains("Error: Cannot find module 'does-not-exist'", got);
        Assert.Contains(@"✖ crash\broken.test.js (45.7186ms)", got);
        Assert.Contains("ℹ fail 1\n", got);
        Assert.EndsWith("  'test failed'", got);
    }

    /// <summary>
    /// FORCE_COLOR wraps "✔"/"ℹ" lines in SGR sequences; matching strips them
    /// so the tree still folds, and kept lines are emitted byte-verbatim
    /// (ANSI intact).
    /// </summary>
    [Fact]
    public void ColoredRun_FoldsTreeAndKeepsSummaryVerbatim()
    {
        var raw = Raw("pass_color");
        Assert.Contains("\x1b[32m✔", raw);
        var got = NodeTest.Filter(raw);
        Assert.DoesNotContain("✔", got);
        Assert.DoesNotContain("▶", got);
        Assert.StartsWith("\x1b[34mℹ tests 34\x1b[39m\n", got);
        Assert.EndsWith("\x1b[34mℹ duration_ms 100.6114\x1b[39m", got);
        var savings = 1 - (double)got.Length / raw.Length;
        Assert.True(savings >= 0.80, $"savings {savings:P0}");
    }

    /// <summary>
    /// A fatal error (bad option, exit 9) carries no "ℹ tests N" block: the
    /// filter returns it byte-identical — content, not exit code, is the
    /// raw gate.
    /// </summary>
    [Fact]
    public void FatalNoSummary_PassesThroughByteIdentical()
    {
        var raw = Raw("fatal");
        Assert.DoesNotContain("ℹ tests", raw);
        Assert.Equal(raw, NodeTest.Filter(raw));
    }

    [Fact]
    public void TapReporter_NoSpecSummary_PassesThrough()
    {
        const string tap = "TAP version 13\n# Subtest: a\nok 1 - a\n1..1\n# tests 1\n# suites 0\n# pass 1\n# fail 0\n# duration_ms 5\n";
        Assert.Equal(tap, NodeTest.Filter(tap));
    }

    [Fact]
    public void DiagnosticInfoLine_IsNotASummaryAnchor()
    {
        // t.diagnostic() prints an "ℹ <message>" line inside the tree; it is
        // kept inline and never mistaken for the summary block.
        const string raw = "▶ s\n  ✔ a (1ms)\n  ℹ took a while\n✔ s (2ms)\nℹ tests 1\nℹ suites 1\nℹ pass 1\nℹ fail 0\nℹ cancelled 0\nℹ skipped 0\nℹ todo 0\nℹ duration_ms 3\n";
        Assert.Equal("  ℹ took a while\nℹ tests 1\nℹ suites 1\nℹ pass 1\nℹ fail 0\nℹ cancelled 0\nℹ skipped 0\nℹ todo 0\nℹ duration_ms 3",
            NodeTest.Filter(raw));
    }

    [Fact]
    public void CrlfBody_KeepsBytesAndFolds()
    {
        const string raw = "▶ s\r\n  ✔ a (1ms)\r\n✔ s (2ms)\r\nℹ tests 1\r\nℹ pass 1\r\nℹ fail 0\r\nℹ duration_ms 3\r\n";
        Assert.Equal("ℹ tests 1\r\nℹ pass 1\r\nℹ fail 0\r\nℹ duration_ms 3\r", NodeTest.Filter(raw));
    }

    [Fact]
    public void UnrecognizedInput_PassesThrough()
    {
        const string weird = "some output vtk has never seen\nsecond line\n";
        Assert.Equal(weird, NodeTest.Filter(weird));
    }

    [Fact]
    public void EmptyInput_PassesThrough()
    {
        Assert.Equal("", NodeTest.Filter(""));
    }
}
