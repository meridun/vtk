using System.Text.Json;
using Xunit;

namespace Vtk.Tests.Smoke;

/// <summary>
/// End-to-end smoke for the playwright list-reporter fold (#169): the real
/// published vtk binary wrapping a fake `playwright` (and `npx playwright`)
/// resolved through PATH. Asserts on every path: exit-code parity, the
/// pass/skip lines folded with failures + summary kept (or byte-identical
/// passthrough when there is no summary block), spool recovery of the raw,
/// metadata-only invocation rows, and the #97 launcher unwrap attribution.
/// </summary>
[Collection("Smoke")]
public class PlaywrightSmokeTests : IDisposable
{
    private readonly SmokeHarness _h = new();
    public void Dispose() => _h.Dispose();

    private static Dictionary<string, string> Code(int n) => new() { ["VTK_FAKE_PLAYWRIGHT_CODE"] = n.ToString() };

    private string[] Rows()
    {
        var path = Path.Combine(_h.Home, "vtk", "invocations.jsonl");
        if (!File.Exists(path)) return Array.Empty<string>();
        return File.ReadAllLines(path).Where(l => l.Length > 0).ToArray();
    }

    private static string Cmd(string row)
    {
        using var doc = JsonDocument.Parse(row);
        return doc.RootElement.GetProperty("cmd").GetString()!;
    }

    [Fact]
    public void NpxPlaywrightFailingRunFoldsPassesKeepsFailuresWithParity()
    {
        var (raw, rawCode) = _h.RunRawTool(Code(1), "npx", "playwright", "test");
        Assert.Equal(1, rawCode);

        var (outp, err, code) = _h.RunFaked(_h.Repo, Code(1), "npx", "playwright", "test");
        Assert.Equal(rawCode, code); // parity: playwright "some tests failed"
        var id = SmokeHarness.MustOkId(outp);
        Assert.True(outp.Length < raw.Length, $"compact ({outp.Length}) not smaller than raw ({raw.Length}); stderr: {err}");

        // Pass and skip lines folded away; header, failing lines, detail
        // blocks and the summary block kept, ANSI-stripped.
        Assert.DoesNotContain("  ok ", outp);
        Assert.DoesNotContain("applies a coupon", outp);
        Assert.False(outp.Contains('\x1b'), "compact output carries ANSI");
        foreach (var want in new[] { "Running 10 tests using 2 workers", "x   2 [chromium]", "1) [chromium]", "2) [chromium]", "Error: gateway timeout", "2 failed", "1 skipped", "7 passed (1.6s)" })
            Assert.Contains(want, outp);

        // The spool recovers the full raw, folded lines included.
        var (shown, _, showCode) = _h.Run(_h.Repo, "show", id);
        Assert.Equal(0, showCode);
        Assert.Contains("# cmd: npx playwright test", shown);
        Assert.Contains("applies a coupon", shown);

        // #97 unwrap: attribution follows the inner tool; invariant 3: the
        // metadata log carries no output content.
        var log = _h.InvocationLog();
        Assert.Contains("\"cmd\":\"playwright test\"", log);
        Assert.Contains("\"reason\":\"playwright test\"", log);
        Assert.DoesNotContain("gateway timeout", log);
        Assert.True(err == "", $"unexpected stderr: {err}");
    }

    [Fact]
    public void PlaywrightGreenRunCollapsesToHeaderAndSummary()
    {
        var (outp, _, code) = _h.RunFaked(_h.Repo, Code(0), "playwright", "test");
        Assert.Equal(0, code); // success-path parity
        SmokeHarness.MustOkId(outp);
        Assert.DoesNotContain("  ok ", outp);
        Assert.DoesNotContain("adds an item", outp);
        Assert.Contains("Running 7 tests using 2 workers", outp);
        Assert.Contains("1 skipped", outp);
        Assert.Contains("6 passed (686ms)", outp);
    }

    /// <summary>"No tests found" (exit 1) has no summary block: the content gate keeps it byte-identical even though exit 1 is allowlisted.</summary>
    [Fact]
    public void Exit1FatalNoSummaryStaysRaw()
    {
        var env = new Dictionary<string, string> { ["VTK_FAKE_PLAYWRIGHT_FATAL"] = "1" };
        var (raw, rawCode) = _h.RunRawTool(env, "npx", "playwright", "test");
        Assert.Equal(1, rawCode);
        var (outp, err, code) = _h.RunFaked(_h.Repo, env, "npx", "playwright", "test");
        Assert.Equal(rawCode, code); // parity
        SmokeAssert.NoOk(outp);
        Assert.Equal(raw, outp + err);
    }

    /// <summary>Other playwright subcommands are not filtered: the pair key makes them their own gap row (#139), not a `playwright test` miss.</summary>
    [Fact]
    public void OtherSubcommandPassesThroughAsOwnGapRow()
    {
        var (outp, err, code) = _h.RunFaked(_h.Repo, null, "playwright", "show-report");
        Assert.Equal(64, code); // parity: the fake refuses non-`test` with 64
        SmokeAssert.NoOk(outp);
        Assert.Contains("only `test` is faked", err);
        var rows = Rows();
        Assert.Single(rows);
        Assert.Equal("playwright show-report", Cmd(rows[0]));
    }

    /// <summary>Two `vtk npx playwright test` at once (the concurrent-invocation exercise verify requires): parity on both, every row logged, both attributed to the inner tool.</summary>
    [Fact]
    public async Task ConcurrentNpxPlaywrightPairLogsBothRows()
    {
        var t1 = Task.Run(() => _h.RunFaked(_h.Repo, Code(1), "npx", "playwright", "test"));
        var t2 = Task.Run(() => _h.RunFaked(_h.Repo, Code(0), "npx", "playwright", "test", "tests/green.spec.js"));
        var results = await Task.WhenAll(t1, t2);

        Assert.Equal(1, results[0].code);
        Assert.Equal(0, results[1].code);
        foreach (var r in results)
        {
            Assert.DoesNotContain("vtk: gap log failed:", r.stderr);
            Assert.True(r.stdout.Contains("OK ") || r.stdout.Contains("  ok "), "incomplete output:\n" + r.stdout);
        }

        var rows = Rows();
        Assert.Equal(2, rows.Length);
        Assert.All(rows, row => Assert.StartsWith("playwright test", Cmd(row)));
    }
}
