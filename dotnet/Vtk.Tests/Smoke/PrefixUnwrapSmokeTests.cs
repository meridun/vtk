using Xunit;

namespace Vtk.Tests.Smoke;

/// <summary>
/// End-to-end smoke for the #97 transparent-launcher prefix unwrap: the real
/// published vtk binary wrapping fake tools resolved through PATH, exercising
/// the telemetry-evidenced shapes (`cross-env VAR=x mocha ...`, npm scripts
/// expanding to cross-env, `npx --yes eslint`) plus the must-not-unwrap case
/// (`npx -p ...`). Asserts on every path: exit-code parity, filter engagement
/// (or byte-identical passthrough), unwrapped telemetry attribution, and
/// original-argv spool provenance.
/// </summary>
[Collection("Smoke")]
public class PrefixUnwrapSmokeTests : IDisposable
{
    private readonly SmokeHarness _h = new();
    public void Dispose() => _h.Dispose();

    [Fact]
    public void CrossEnvMochaFailingRunEngagesMochaFilterWithParityAndUnwrappedAttribution()
    {
        // The #97 telemetry shape: cross-env sets a var, mocha fails (exit 1).
        // The assignment travels through cross-env itself, like real traffic.
        var argv = new[] { "cross-env", "VTK_FAKE_MOCHA_CODE=1", "mocha" };
        var (raw, rawCode) = _h.RunRawTool(null, "cross-env", argv[1], argv[2]);
        Assert.Equal(1, rawCode);

        var (outp, err, code) = _h.RunFaked(_h.Repo, null, argv);
        Assert.Equal(rawCode, code); // parity through the unwrap path
        var id = SmokeHarness.MustOkId(outp);
        Assert.True(outp.Length < raw.Length, $"compact ({outp.Length}) not smaller than raw ({raw.Length}); stderr: {err}");

        // The mocha filter engaged: passing specs folded, failures + summary kept.
        Assert.DoesNotContain("starts empty", outp);
        Assert.DoesNotContain("✔", outp);
        foreach (var want in new[] { "25 passing", "3 failing", "charges card", "gateway timeout" })
            Assert.Contains(want, outp);

        // Spool provenance keeps the ORIGINAL wrapped argv (execution was untouched).
        var (shown, _, showCode) = _h.Run(_h.Repo, "show", id);
        Assert.Equal(0, showCode);
        Assert.Contains("# cmd: cross-env VTK_FAKE_MOCHA_CODE=1 mocha", shown);
        Assert.Contains("starts empty", shown); // full raw recoverable

        // Telemetry attribution follows the unwrapped inner tool.
        var log = _h.InvocationLog();
        Assert.Contains("\"cmd\":\"mocha\"", log);
        Assert.DoesNotContain("\"cmd\":\"cross-env", log);
        // Invariant 3: no output content in the metadata log.
        Assert.DoesNotContain("gateway timeout", log);
    }

    [Fact]
    public void CrossEnvMochaGreenRunCollapsesWithExitZeroParity()
    {
        var (outp, _, code) = _h.RunFaked(_h.Repo, null, "cross-env", "VTK_FAKE_MOCHA_CODE=0", "mocha");
        Assert.Equal(0, code); // success-path parity
        Assert.Contains("27 passing", outp);
        Assert.DoesNotContain("✔", outp);
    }

    [Fact]
    public void NpxYesEslintEngagesEslintFilterWithParity()
    {
        var env = new Dictionary<string, string> { ["VTK_FAKE_ESLINT_CODE"] = "1" };
        var (raw, rawCode) = _h.RunRawTool(env, "npx", "--yes", "eslint", ".");
        Assert.Equal(1, rawCode);

        var (outp, err, code) = _h.RunFaked(_h.Repo, env, "npx", "--yes", "eslint", ".");
        Assert.Equal(rawCode, code); // parity
        SmokeHarness.MustOkId(outp);
        Assert.True(outp.Length < raw.Length, $"compact ({outp.Length}) not smaller than raw ({raw.Length}); stderr: {err}");
        // Per-rule rollup, not raw stylish lines: the eslint filter engaged.
        Assert.Contains("7x error semi", outp);
        Assert.Contains("12 problems (10 errors, 2 warnings)", outp);
        // Attribution is the unwrapped inner argv.
        Assert.Contains("\"cmd\":\"eslint .\"", _h.InvocationLog());
    }

    [Fact]
    public void NpxNonTransparentFlagStaysRawGapLoggedWithParity()
    {
        // `npx -p <pkg> <cmd>` may run something other than the next token:
        // unwrap must decline, and vtk passes through byte-identically.
        var (raw, rawCode) = _h.RunRawTool(null, "npx", "-p", "typescript", "tsc");
        Assert.Equal(1, rawCode);

        var (outp, err, code) = _h.RunFaked(_h.Repo, null, "npx", "-p", "typescript", "tsc");
        Assert.Equal(rawCode, code); // parity
        SmokeAssert.NoOk(outp + err);
        Assert.Equal(raw, outp + err); // output unaltered
        // Gap-logged (never silent) under the un-unwrapped npx command.
        var log = _h.InvocationLog();
        Assert.Contains("\"cmd\":\"npx -p typescript tsc\"", log);
        Assert.Contains("\"reason\":\"no-filter\"", log);
    }

    [Fact]
    public void NpmRunScriptExpandingToCrossEnvDelegatesToMochaFilter()
    {
        // The npm dispatch layer: `npm run itest` expands to
        // `> cross-env INTEGRATION=1 mocha --reporter spec` in the banner —
        // the exact shape the 2026-07-17 telemetry showed arriving unfiltered.
        var env = new Dictionary<string, string> { ["VTK_FAKE_MOCHA_CODE"] = "1" };
        var (outp, err, code) = _h.RunFaked(_h.Repo, env, "npm", "run", "itest");
        Assert.Equal(1, code); // parity through npm dispatch + unwrap
        var id = SmokeHarness.MustOkId(outp);

        // Banner stripped and the mocha filter engaged on the body.
        Assert.DoesNotContain("demo@1.0.0", outp);
        Assert.DoesNotContain("> cross-env", outp);
        Assert.DoesNotContain("✔", outp);
        Assert.Contains("25 passing", outp);
        Assert.Contains("3 failing", outp);

        // Attribution to mocha — not npm, not cross-env.
        var log = _h.InvocationLog();
        Assert.Contains("\"cmd\":\"mocha", log);
        Assert.DoesNotContain("\"cmd\":\"cross-env", log);
        Assert.DoesNotContain("\"cmd\":\"npm", log);

        // Full raw (banner included) recoverable from the spool.
        var (shown, _, showCode) = _h.Run(_h.Repo, "show", id);
        Assert.Equal(0, showCode);
        Assert.Contains("> cross-env INTEGRATION=1 mocha --reporter spec", shown);
        Assert.Contains("starts empty", shown);
        Assert.True(err == "", $"unexpected stderr: {err}");
    }
}
