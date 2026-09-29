using System.Diagnostics;
using Xunit;

namespace Vtk.Tests.Smoke;

/// <summary>
/// End-to-end smoke for the node --test filter (#168) through the real
/// published vtk binary and the real installed node: a green run folds to
/// the "ℹ" summary block, a failing run keeps the tree "✖" lines plus the
/// "✖ failing tests:" detail region, a bad option (exit 9) passes through
/// raw, and `npm test` whose script is `node --test` reaches the filter via
/// the inner-tool dispatch. Exit-code parity is asserted against bare node
/// on every path; the invocation log never carries test titles or
/// assertion text (invariant 3). Requires `node` (and `npm`) on PATH — the
/// verify host's prerequisite, like git for the harness itself.
/// </summary>
[Collection("Smoke")]
public class NodeTestSmokeTests : IDisposable
{
    private readonly SmokeHarness _h = new();
    private readonly string _proj;

    public NodeTestSmokeTests()
    {
        _proj = Path.Combine(Path.GetTempPath(), "vtk-smoke-node-" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(_proj, "test"));
        File.WriteAllText(Path.Combine(_proj, "package.json"),
            "{\"name\":\"scratch\",\"version\":\"1.0.0\",\"scripts\":{\"test\":\"node --test\"}}\n");
        File.WriteAllText(Path.Combine(_proj, "test", "cart.test.js"),
            "const { describe, it } = require('node:test');\n" +
            "const assert = require('node:assert');\n" +
            "describe('cart', () => {\n" +
            "  for (let i = 0; i < 12; i++) it('adds item ' + i, () => assert.strictEqual(i + 1, i + 1));\n" +
            "  it('skips shipping', { skip: 'no shipping yet' }, () => {});\n" +
            "  it('todo tax', { todo: true }, () => {});\n" +
            "});\n");
        // VTK_SMOKE_BREAK flips one assertion so the same tree fails.
        File.WriteAllText(Path.Combine(_proj, "test", "orders.test.js"),
            "const { describe, it } = require('node:test');\n" +
            "const assert = require('node:assert');\n" +
            "describe('orders', () => {\n" +
            "  for (let i = 0; i < 10; i++) it('creates order ' + i, () => assert.ok(true));\n" +
            "  it('computes the total', () => assert.strictEqual(12, process.env.VTK_SMOKE_BREAK ? 11 : 12));\n" +
            "});\n");
    }

    public void Dispose()
    {
        _h.Dispose();
        try { Directory.Delete(_proj, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static Dictionary<string, string> Break() => new() { ["VTK_SMOKE_BREAK"] = "1" };

    /// <summary>
    /// The full path of npm's launcher: on Windows it is a .cmd shim whose
    /// %~dp0 resolves to the working directory when CreateProcess is handed
    /// the bare name, so it must be located on PATH here first.
    /// </summary>
    private static string NpmPath()
    {
        if (!OperatingSystem.IsWindows()) return "npm";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(dir, "npm.cmd");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("npm.cmd not found on PATH");
    }

    /// <summary>Runs a tool without vtk (UTF-8 pipes): the parity baseline.</summary>
    private (string combined, int code) Raw(IDictionary<string, string>? env, string tool, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = tool == "npm" ? NpmPath() : tool,
            WorkingDirectory = _proj,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env != null)
            foreach (var (k, v) in env)
                psi.EnvironmentVariables[k] = v;
        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return (stdout + stderr, proc.ExitCode);
    }

    /// <summary>Runs vtk in the scratch project with the harness's isolated home and UTF-8 pipes (the tree glyphs are multi-byte).</summary>
    private (string stdout, string stderr, int code) Vtk(IDictionary<string, string>? env, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _h.Bin,
            WorkingDirectory = _proj,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.EnvironmentVariables["LOCALAPPDATA"] = _h.Home;
        psi.EnvironmentVariables["XDG_CACHE_HOME"] = _h.Home;
        psi.EnvironmentVariables["HOME"] = _h.Home;
        if (env != null)
            foreach (var (k, v) in env)
                psi.EnvironmentVariables[k] = v;
        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return (stdout, stderr, proc.ExitCode);
    }

    [Fact]
    public void GreenRunFoldsToSummaryBlockWithParityAndRecovery()
    {
        var (raw, rawCode) = Raw(null, "node", "--test");
        Assert.Equal(0, rawCode);
        Assert.Contains("✔ adds item 0", raw);

        var (outp, err, code) = Vtk(null, "node", "--test");
        Assert.Equal(rawCode, code); // parity
        Assert.True(err == "", $"unexpected stderr: {err}");
        var id = SmokeHarness.MustOkId(outp);

        // Only the summary block survives: no suite headers, passes, or skips.
        Assert.StartsWith("ℹ tests 25\n", outp);
        Assert.Contains("\nℹ pass 23\n", outp);
        Assert.Contains("\nℹ skipped 1\n", outp);
        Assert.Contains("\nℹ todo 1\n", outp);
        Assert.DoesNotContain("✔", outp);
        Assert.DoesNotContain("▶", outp);
        Assert.DoesNotContain("﹣", outp);
        Assert.True(outp.Length * 5 < raw.Length, $"compact ({outp.Length}) not under 20% of raw ({raw.Length})");

        // The raw tree survives in the spool and is recoverable in full.
        var (shown, _, showCode) = Vtk(null, "show", id);
        Assert.Equal(0, showCode);
        Assert.Contains("# cmd: node --test", shown);
        Assert.Contains("▶ cart", shown);
        Assert.Contains("✔ adds item 0", shown);
        Assert.Contains("﹣ skips shipping", shown);

        // Attributed to the filter by its static name; no output content in the log (invariant 3).
        var log = _h.InvocationLog();
        Assert.Contains("\"cmd\":\"node --test\"", log);
        Assert.Contains("\"filtered\":true", log);
        Assert.Contains("\"reason\":\"node --test\"", log);
        Assert.DoesNotContain("adds item", log);
    }

    [Fact]
    public void FailingRunKeepsTreeFailuresSummaryAndDetailWithParity()
    {
        var (raw, rawCode) = Raw(Break(), "node", "--test");
        Assert.Equal(1, rawCode); // node: some tests failed

        var (outp, _, code) = Vtk(Break(), "node", "--test");
        Assert.Equal(rawCode, code); // parity: a failing run is a report
        SmokeHarness.MustOkId(outp);

        // Tree "✖" lines stay (which suite failed); passing siblings fold.
        Assert.Contains("✖ computes the total", outp);
        Assert.Contains("\n✖ orders", outp);
        Assert.DoesNotContain("creates order", outp);
        Assert.DoesNotContain("▶ orders", outp);
        // Summary block verbatim.
        Assert.Contains("ℹ tests 25\nℹ suites 2\nℹ pass 22\nℹ fail 1\n", outp);
        // Detail region verbatim: location, assertion, diff, stack.
        Assert.Contains("✖ failing tests:", outp);
        Assert.Contains("orders.test.js", outp);
        Assert.Contains("AssertionError [ERR_ASSERTION]", outp);
        Assert.Contains("12 !== 11", outp);
        Assert.Contains("at TestContext.<anonymous>", outp);
        Assert.True(outp.Length < raw.Length, $"compact ({outp.Length}) not smaller than raw ({raw.Length})");

        Assert.DoesNotContain("ERR_ASSERTION", _h.InvocationLog());
    }

    /// <summary>A bad option exits 9 outside the {0, 1} allowlist: raw passthrough, no spool, parity.</summary>
    [Fact]
    public void BadOptionPassesThroughRawWithParity()
    {
        var (raw, rawCode) = Raw(null, "node", "--test", "--bogus-flag");
        Assert.Equal(9, rawCode);
        Assert.Contains("bad option: --bogus-flag", raw);

        var (outp, err, code) = Vtk(null, "node", "--test", "--bogus-flag");
        Assert.Equal(rawCode, code); // parity
        Assert.Contains("bad option: --bogus-flag", outp + err);
        SmokeAssert.NoOk(outp);

        var log = _h.InvocationLog();
        Assert.Contains("\"cmd\":\"node --test --bogus-flag\"", log);
        Assert.Contains("\"filtered\":false", log);
        Assert.Contains("\"reason\":\"nonzero-exit\"", log);
    }

    /// <summary>`npm test` with a `node --test` script reaches the filter through the inner-tool dispatch (banner stripped, attributed to node --test).</summary>
    [Fact]
    public void NpmTestDelegatesToNodeTestFilterWithParity()
    {
        var (_, rawCode) = Raw(null, "npm", "test");
        Assert.Equal(0, rawCode);
        var (_, rawFailCode) = Raw(Break(), "npm", "test");
        Assert.Equal(1, rawFailCode);

        var (outp, _, code) = Vtk(null, "npm", "test");
        Assert.Equal(rawCode, code); // parity
        SmokeHarness.MustOkId(outp);
        Assert.StartsWith("ℹ tests 25\n", outp);
        Assert.DoesNotContain("> node --test", outp);
        Assert.DoesNotContain("✔", outp);
        Assert.Contains("\"cmd\":\"node --test\"", _h.InvocationLog());

        var (failOut, _, failCode) = Vtk(Break(), "npm", "test");
        Assert.Equal(rawFailCode, failCode); // parity
        Assert.Contains("✖ failing tests:", failOut);
        Assert.Contains("12 !== 11", failOut);
    }

    /// <summary>Two identical runs at once: both exit 0, both fold, both rows land in the invocation log.</summary>
    [Fact]
    public async Task ConcurrentRunsBothFoldAndLog()
    {
        var a = Task.Run(() => Vtk(null, "node", "--test"));
        var b = Task.Run(() => Vtk(null, "node", "--test"));
        var results = await Task.WhenAll(a, b);
        foreach (var (outp, _, code) in results)
        {
            Assert.Equal(0, code);
            Assert.StartsWith("ℹ tests 25\n", outp);
            Assert.DoesNotContain("✔", outp);
            SmokeHarness.MustOkId(outp);
        }
        var rows = _h.InvocationLog().Split('\n').Count(l => l.Contains("\"reason\":\"node --test\""));
        Assert.Equal(2, rows);
    }
}
