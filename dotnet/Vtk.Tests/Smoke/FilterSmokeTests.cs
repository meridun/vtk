using System.Text.RegularExpressions;
using Xunit;

namespace Vtk.Tests.Smoke;

// Per-filter end-to-end smoke: the real published vtk binary wrapping fake
// external tools resolved through PATH (see SmokeHarness.FakeBinDir /
// Vtk.FakeTool). C# recreation of the deleted Go per-filter smoke suite
// (test/smoke/{eslint,gh,mocha,npm,dbmate,files}_smoke_test.go, pre-#59
// history). Payload sizes differ from the Go originals where the #52 savings
// gate would otherwise suppress the `OK <id>` spool signal the assertions
// depend on.
public static class SmokeAssert
{
    private static readonly Regex OkRe = new(@"(?m)^OK [0-9a-f]{4}$", RegexOptions.Compiled);

    public static void NoOk(string output) =>
        Assert.False(OkRe.IsMatch(output), $"unexpected OK <id> line in output:\n{output}");

    /// <summary>Every packed row (before the OK line, excluding "(+N more)" tails) fits the 96-char pack width.</summary>
    public static void PackRowsMaxWidth(string output)
    {
        foreach (var line in output.TrimEnd('\n').Split('\n'))
        {
            if (OkRe.IsMatch(line) || line.StartsWith("(+")) continue;
            Assert.True(line.Length <= 96, $"packed row exceeds 96 chars ({line.Length}): {line}");
        }
    }
}

/// <summary>Port of test/smoke/eslint_smoke_test.go: the eslint exit-code allowlist ({0,1} filters, 2+ stays raw) with exit-code parity on every path.</summary>
[Collection("Smoke")]
public class EslintSmokeTests : IDisposable
{
    private readonly SmokeHarness _h = new();
    public void Dispose() => _h.Dispose();

    private static Dictionary<string, string> Code(int n) => new() { ["VTK_FAKE_ESLINT_CODE"] = n.ToString() };

    [Fact]
    public void Exit1ProblemsReportCompactedWithParityAndRecovery()
    {
        var (raw, rawCode) = _h.RunRawTool(Code(1), "eslint");
        Assert.Equal(1, rawCode);

        var (outp, err, code) = _h.RunFaked(_h.Repo, Code(1), "eslint", "src/");
        Assert.Equal(rawCode, code); // parity
        var id = SmokeHarness.MustOkId(outp);
        Assert.True(outp.Length < raw.Length, $"compact ({outp.Length}) not smaller than raw ({raw.Length}); stderr: {err}");

        // Per-rule rollup, not the raw stylish lines.
        Assert.Contains("7x error semi", outp);
        Assert.Contains("3x error no-undef", outp);
        Assert.Contains("12 problems (10 errors, 2 warnings)", outp);

        // The raw report survives in the spool and is recoverable in full.
        var (shown, _, showCode) = _h.Run(_h.Repo, "show", id);
        Assert.Equal(0, showCode);
        Assert.Contains("# cmd: eslint src/", shown);
        Assert.Contains("'foo' is not defined", shown);

        // Invariant 3: the gap/invocation log carries no output content.
        Assert.DoesNotContain("is not defined", _h.InvocationLog());
    }

    [Fact]
    public void Exit0CleanRunPassesThroughWithNoOk()
    {
        var (outp, _, code) = _h.RunFaked(_h.Repo, Code(0), "eslint", "src/");
        Assert.Equal(0, code);
        Assert.Equal("", outp.Trim());
        SmokeAssert.NoOk(outp);
    }

    [Fact]
    public void Exit2FatalErrorStaysRawWithParity()
    {
        var (raw, rawCode) = _h.RunRawTool(Code(2), "eslint");
        Assert.Equal(2, rawCode);

        var (outp, err, code) = _h.RunFaked(_h.Repo, Code(2), "eslint", "src/");
        Assert.Equal(rawCode, code); // parity
        SmokeAssert.NoOk(outp + err);
        // Fatal output survives unaltered (invariant 2: out-of-allowlist
        // degrades to raw, never lost output).
        Assert.Equal(raw, outp + err);
        Assert.Contains("\"reason\":\"nonzero-exit\"", _h.InvocationLog());
    }
}

/// <summary>Port of test/smoke/gh_smoke_test.go: gh list families compact on success, view/JSON shapes pass through intact, parity holds on failure.</summary>
[Collection("Smoke")]
public class GhSmokeTests : IDisposable
{
    private readonly SmokeHarness _h = new();
    public void Dispose() => _h.Dispose();

    private static Dictionary<string, string> Code(int n) => new() { ["VTK_FAKE_GH_CODE"] = n.ToString() };

    [Fact]
    public void IssueListCompactedLabelsDroppedParity()
    {
        var (raw, rawCode) = _h.RunRawTool(null, "gh", "issue", "list");
        Assert.Equal(0, rawCode);

        var (outp, err, code) = _h.RunFaked(_h.Repo, null, "gh", "issue", "list");
        Assert.Equal(rawCode, code);
        var id = SmokeHarness.MustOkId(outp);
        Assert.True(outp.Length < raw.Length, $"compact ({outp.Length}) not smaller than raw ({raw.Length}); stderr: {err}");
        Assert.Contains("#119 open Ticket 119: improve the flux capacitor coverage in module 119", outp);
        Assert.Contains("#120 closed Ticket 120", outp);
        // Label noise dropped (AC: drop label noise unless requested).
        Assert.DoesNotContain("priority:high", outp);
        Assert.DoesNotContain("needs-triage", outp);

        // Raw survives in the spool, recoverable in full with provenance.
        var (shown, _, showCode) = _h.Run(_h.Repo, "show", id);
        Assert.Equal(0, showCode);
        Assert.Contains("# cmd: gh issue list", shown);
        Assert.Contains("bug,priority:high,needs-triage", shown);

        // Invariant 3: the invocation/gap log carries no output content.
        Assert.DoesNotContain("flux capacitor", _h.InvocationLog());
    }

    [Fact]
    public void PrListCompactedHeadBranchDropped()
    {
        var (outp, _, code) = _h.RunFaked(_h.Repo, null, "gh", "pr", "list");
        Assert.Equal(0, code);
        SmokeHarness.MustOkId(outp);
        Assert.Contains("#59 open Wire up coverage for the number 59 filter family", outp);
        Assert.Contains("#60 merged Wire up coverage for the number 60 filter family", outp);
        Assert.DoesNotContain("some-longish-branch-name", outp);
    }

    [Fact]
    public void RunListCompactedLowSignalColumnsDropped()
    {
        var (outp, _, code) = _h.RunFaked(_h.Repo, null, "gh", "run", "list");
        Assert.Equal(0, code);
        SmokeHarness.MustOkId(outp);
        Assert.Contains("success CI · build.yml", outp);
        // In-progress run has no conclusion: falls back to the status field.
        Assert.Contains("in_progress CI · build.yml", outp);
        // runID / elapsed / event dropped as low-signal.
        Assert.DoesNotContain("7788990011", outp);
        Assert.DoesNotContain("45s", outp);
        Assert.DoesNotContain("push", outp);
    }

    [Fact]
    public void IssueViewNonListShapePassesThroughUnchanged()
    {
        var (raw, _) = _h.RunRawTool(null, "gh", "issue", "view", "12");
        var (outp, _, code) = _h.RunFaked(_h.Repo, null, "gh", "issue", "view", "12");
        Assert.Equal(0, code);
        SmokeAssert.NoOk(outp);
        Assert.Equal(raw, outp);
    }

    [Fact]
    public void JsonFormPassesThroughStructurallyIntact()
    {
        var (raw, _) = _h.RunRawTool(null, "gh", "issue", "list", "--json", "number,state,title");
        var (outp, _, code) = _h.RunFaked(_h.Repo, null, "gh", "issue", "list", "--json", "number,state,title");
        Assert.Equal(0, code);
        SmokeAssert.NoOk(outp);
        Assert.Equal(raw, outp);
        Assert.Contains("\"number\":112", outp);
    }

    [Fact]
    public void NonzeroExitStaysRawWithParity()
    {
        var (raw, rawCode) = _h.RunRawTool(Code(1), "gh", "issue", "list");
        Assert.Equal(1, rawCode);

        var (outp, err, code) = _h.RunFaked(_h.Repo, Code(1), "gh", "issue", "list");
        Assert.Equal(rawCode, code); // parity
        SmokeAssert.NoOk(outp + err);
        // Failure output survives unaltered (invariant 2).
        Assert.Equal(raw, outp + err);
    }
}

/// <summary>Port of test/smoke/mocha_smoke_test.go: fold passing specs, keep failures + summary, recover raw via show, parity across success/failure/config-error.</summary>
[Collection("Smoke")]
public class MochaSmokeTests : IDisposable
{
    private readonly SmokeHarness _h = new();
    public void Dispose() => _h.Dispose();

    private static Dictionary<string, string> Code(int n) => new() { ["VTK_FAKE_MOCHA_CODE"] = n.ToString() };

    [Fact]
    public void NpxMochaFailingRunFoldsPassingSpecsKeepsFailuresWithParity()
    {
        var (outp, err, code) = _h.RunFaked(_h.Repo, Code(1), "npx", "mocha");
        Assert.Equal(1, code); // parity: mocha "tests failed"
        var id = SmokeHarness.MustOkId(outp);

        // Passing specs folded away.
        Assert.DoesNotContain("starts empty", outp);
        Assert.DoesNotContain("✔", outp);
        // Summary + every failure block kept.
        foreach (var want in new[] { "25 passing", "3 failing", "computes total", "applies discount", "charges card", "gateway timeout" })
            Assert.Contains(want, outp);

        // The spool recovers the full raw, folded specs included.
        var (shown, _, showCode) = _h.Run(_h.Repo, "show", id);
        Assert.Equal(0, showCode);
        Assert.Contains("starts empty", shown);
        Assert.Contains("sends receipt", shown);
        Assert.Contains("# cmd: npx mocha", shown);
        Assert.True(err == "", $"unexpected stderr: {err}");
    }

    [Fact]
    public void NpxMochaGreenRunCollapsesToOneSummaryLine()
    {
        var (outp, _, code) = _h.RunFaked(_h.Repo, Code(0), "npx", "mocha");
        Assert.Equal(0, code);
        Assert.Contains("27 passing", outp);
        Assert.DoesNotContain("✔", outp);
        Assert.DoesNotContain("adds small numbers", outp);
        // One content line (the summary) plus the OK line.
        var lines = outp.Trim().Split('\n');
        Assert.True(lines.Length == 2, $"green run should be summary + OK (2 lines), got {lines.Length}:\n{outp}");
    }

    [Fact]
    public void NpmRunTestDelegatesToMochaFilterWithMochaAttribution()
    {
        var (outp, _, code) = _h.RunFaked(_h.Repo, Code(1), "npm", "run", "test");
        Assert.Equal(1, code); // parity through the npm dispatch layer
        var id = SmokeHarness.MustOkId(outp);
        Assert.Contains("25 passing", outp);
        Assert.Contains("charges card", outp);
        // npm banner stripped, passing specs folded.
        Assert.DoesNotContain("demo@1.0.0", outp);
        Assert.DoesNotContain("> mocha", outp);
        Assert.DoesNotContain("✔", outp);
        // Attribution is to mocha, not npm.
        Assert.Contains("\"cmd\":\"mocha", _h.InvocationLog());
        // The spool recovers the full raw, banner + folded specs included.
        var (shown, _, showCode) = _h.Run(_h.Repo, "show", id);
        Assert.Equal(0, showCode);
        Assert.Contains("> mocha --reporter spec", shown);
        Assert.Contains("starts empty", shown);
    }

    [Fact]
    public void Exit2ConfigErrorStaysRawAllowlistPassthrough()
    {
        var (outp, err, code) = _h.RunFaked(_h.Repo, Code(2), "npx", "mocha");
        Assert.Equal(2, code); // parity
        SmokeAssert.NoOk(outp);
        // Exit 2 is outside the {0,1} allowlist: raw output, no fold.
        Assert.Contains("✔", outp);
        Assert.Contains("starts empty", outp);
        Assert.True(err == "", $"unexpected stderr: {err}");
    }
}

/// <summary>Port of test/smoke/npm_smoke_test.go: strip the npm banner, delegate to the inner tool's filter, attribute gaps to the inner tool.</summary>
[Collection("Smoke")]
public class NpmDispatchSmokeTests : IDisposable
{
    private readonly SmokeHarness _h = new();
    public void Dispose() => _h.Dispose();

    [Fact]
    public void NpmRunLintDelegatesToEslintFilterAndCompacts()
    {
        var env = new Dictionary<string, string> { ["VTK_FAKE_NPM_CODE"] = "1" };
        var (outp, err, code) = _h.RunFaked(_h.Repo, env, "npm", "run", "lint");
        Assert.Equal(1, code); // parity
        var id = SmokeHarness.MustOkId(outp);
        // The eslint per-rule rollup, not the banner or raw stylish lines.
        Assert.Contains("semi", outp);
        Assert.Contains("12 problems", outp);
        Assert.DoesNotContain("demo@1.0.0", outp);
        Assert.DoesNotContain("> eslint", outp);
        // The spool recovers the full raw, banner included.
        var (shown, _, showCode) = _h.Run(_h.Repo, "show", id);
        Assert.Equal(0, showCode);
        Assert.Contains("> eslint . --cache", shown);
        Assert.Contains("'foo' is not defined", shown);
        // The invocation is attributed to eslint, not npm.
        Assert.Contains("\"cmd\":\"eslint", _h.InvocationLog());
        Assert.True(err == "", $"unexpected stderr: {err}");
    }

    [Fact]
    public void NpmRunWithNoInnerFilterStripsBannerAndGapsToInnerTool()
    {
        var (outp, _, code) = _h.RunFaked(_h.Repo, null, "npm", "run", "nofil");
        Assert.Equal(0, code);
        // Banner stripped; body preserved. The tiny body saving is below the
        // #52 bar, so it prints inline with no OK spool signal.
        Assert.DoesNotContain("demo@1.0.0", outp);
        Assert.DoesNotContain("> node", outp);
        Assert.Contains("config is in sync", outp);
        SmokeAssert.NoOk(outp);
        // Gap attributed to node (the inner tool), not npm.
        var (gaps, _, gapsCode) = _h.Run(_h.Repo, "gaps");
        Assert.Equal(0, gapsCode);
        Assert.Contains("node", gaps);
        Assert.DoesNotContain("npm", gaps);
    }
}

/// <summary>Port of test/smoke/dbmate_smoke_test.go: the dbmate filter family, direct and via the npm-run dispatch.</summary>
[Collection("Smoke")]
public class DbmateSmokeTests : IDisposable
{
    private readonly SmokeHarness _h = new();
    public void Dispose() => _h.Dispose();

    [Fact]
    public void StatusCollapsesAppliedKeepsPendingAndSummary()
    {
        var (raw, rawCode) = _h.RunRawTool(null, "dbmate", "status");
        Assert.Equal(0, rawCode);

        var (outp, err, code) = _h.RunFaked(_h.Repo, null, "dbmate", "status");
        Assert.Equal(rawCode, code); // parity
        var id = SmokeHarness.MustOkId(outp);
        Assert.True(outp.Length < raw.Length, $"compact ({outp.Length}) not smaller than raw ({raw.Length}); stderr: {err}");
        // Applied list collapsed to the count line; no individual [X] entries.
        Assert.Contains("[X] Applied: 18", outp);
        Assert.DoesNotContain("create_users", outp);
        // Pending entries kept verbatim; summary preserved.
        foreach (var want in new[] { "add_read_flag.sql", "create_audit_log.sql", "Applied: 18", "Pending: 2" })
            Assert.Contains(want, outp);
        // Spool recovers the full raw applied list.
        var (shown, _, showCode) = _h.Run(_h.Repo, "show", id);
        Assert.Equal(0, showCode);
        Assert.Contains("create_users", shown);
        Assert.Contains("# cmd: dbmate status", shown);
    }

    [Fact]
    public void UpDropsProgressKeepsResultLines()
    {
        var (outp, _, code) = _h.RunFaked(_h.Repo, null, "dbmate", "up");
        Assert.Equal(0, code);
        SmokeHarness.MustOkId(outp);
        Assert.DoesNotContain("Applying:", outp);
        Assert.Contains("Applied: 20260101000011_migration_step_11.sql", outp);
        Assert.Contains("Applied: 20260101000020_migration_step_20.sql", outp);
    }

    [Fact]
    public void RollbackDropsProgressKeepsResult()
    {
        var (outp, _, code) = _h.RunFaked(_h.Repo, null, "dbmate", "rollback");
        Assert.Equal(0, code);
        SmokeHarness.MustOkId(outp);
        Assert.DoesNotContain("Rolling back:", outp);
        Assert.Contains("Rolled back: 20260101000020_migration_step_20.sql", outp);
        Assert.Contains("Rolled back: 20260101000015_migration_step_15.sql", outp);
    }

    [Fact]
    public void FailedMigrationExitsNonzeroAndPassesThroughRaw()
    {
        var (raw, rawCode) = _h.RunRawTool(null, "dbmate", "fail");
        Assert.Equal(1, rawCode);

        var (outp, err, code) = _h.RunFaked(_h.Repo, null, "dbmate", "fail");
        Assert.Equal(rawCode, code); // parity
        // Clean-run-only registration: nonzero exit is never filtered.
        SmokeAssert.NoOk(outp + err);
        // Error preserved verbatim (raw is on stderr).
        Assert.Contains("Error: table users already exists", err);
        Assert.Equal(raw, outp + err);
    }

    [Fact]
    public void UnknownSubcommandKeepsExitCodeParity()
    {
        // Fake dbmate exits 1 on an unknown subcommand; the unrecognized
        // shape falls through raw with parity preserved.
        var (_, _, code) = _h.RunFaked(_h.Repo, null, "dbmate", "bogus-subcmd");
        Assert.Equal(1, code);
    }

    [Fact]
    public void NpmRunDbStatusDispatchesToDbmateFilterWithAttribution()
    {
        var (outp, err, code) = _h.RunFaked(_h.Repo, null, "npm", "run", "db:status");
        Assert.Equal(0, code); // parity
        var id = SmokeHarness.MustOkId(outp);
        // npm banner stripped; dbmate applied list collapsed.
        Assert.DoesNotContain("demo@1.0.0", outp);
        Assert.DoesNotContain("> dbmate status", outp);
        Assert.Contains("[X] Applied: 18", outp);
        Assert.DoesNotContain("create_users", outp);
        // Attribution to dbmate, not npm.
        Assert.Contains("\"cmd\":\"dbmate status\"", _h.InvocationLog());
        // Spool recovers banner + full raw applied list.
        var (shown, _, showCode) = _h.Run(_h.Repo, "show", id);
        Assert.Equal(0, showCode);
        Assert.Contains("> dbmate status", shown);
        Assert.Contains("create_users", shown);
        Assert.True(err == "", $"unexpected stderr: {err}");
    }
}

/// <summary>Port of test/smoke/files_smoke_test.go: listings are packed and capped with full output recoverable, `ls -l` passes through, grep caps per file, no-match exit 1 keeps parity.</summary>
[Collection("Smoke")]
public class FilesSmokeTests : IDisposable
{
    private readonly SmokeHarness _h = new();
    public void Dispose() => _h.Dispose();

    [Fact]
    public void LsPacksCapsAt40AndSpoolsFullListing()
    {
        var (raw, rawCode) = _h.RunRawTool(null, "ls");
        Assert.Equal(0, rawCode);

        var (outp, err, code) = _h.RunFaked(_h.Repo, null, "ls");
        Assert.Equal(rawCode, code); // parity
        var id = SmokeHarness.MustOkId(outp);
        Assert.Contains("(+120 more)", outp);
        Assert.Contains("entry001.txt", outp);
        Assert.DoesNotContain("entry041.txt", outp);
        SmokeAssert.PackRowsMaxWidth(outp);
        var savings = 1 - (double)outp.Length / raw.Length;
        Assert.True(savings >= 0.60, $"savings {savings:P0} below the 60% band floor ({raw.Length} -> {outp.Length} bytes); stderr: {err}");

        var (shown, _, showCode) = _h.Run(_h.Repo, "show", id);
        Assert.Equal(0, showCode);
        Assert.Contains("# cmd: ls", shown);
        Assert.Contains("entry041.txt", shown);
        Assert.Contains("entry160.txt", shown);
    }

    [Fact]
    public void LsLongFormatPassesThroughUnchangedWithNoOk()
    {
        var (raw, rawCode) = _h.RunRawTool(null, "ls", "-l");
        Assert.Equal(0, rawCode);

        var (outp, _, code) = _h.RunFaked(_h.Repo, null, "ls", "-l");
        Assert.Equal(rawCode, code); // parity
        SmokeAssert.NoOk(outp);
        Assert.Equal(raw, outp);
    }

    [Fact]
    public void GrepRnCapsMatchesPerFileWithPerFileTail()
    {
        var (raw, rawCode) = _h.RunRawTool(null, "grep", "-rn", "needle", ".");
        Assert.Equal(0, rawCode);

        var (outp, err, code) = _h.RunFaked(_h.Repo, null, "grep", "-rn", "needle", ".");
        Assert.Equal(rawCode, code); // parity
        var id = SmokeHarness.MustOkId(outp);
        Assert.Equal(5, Regex.Matches(outp, @"(?m)^\./notes_a\.txt:\d+:").Count);
        Assert.Contains("./notes_a.txt: (+25 more)", outp);
        Assert.Equal(2, Regex.Matches(outp, @"(?m)^\./notes_b\.txt:\d+:").Count);
        Assert.DoesNotContain("notes_b.txt: (+", outp);
        Assert.DoesNotContain("needle mark 30", outp);
        Assert.True(outp.Length < raw.Length, $"compact ({outp.Length}) not smaller than raw ({raw.Length}); stderr: {err}");

        var (shown, _, showCode) = _h.Run(_h.Repo, "show", id);
        Assert.Equal(0, showCode);
        Assert.Contains("needle mark 30", shown);
    }

    [Fact]
    public void GrepRlFileListIsPackedAndCapped()
    {
        var (outp, _, code) = _h.RunFaked(_h.Repo, null, "grep", "-rl", "needle", ".");
        Assert.Equal(0, code);
        var id = SmokeHarness.MustOkId(outp);
        Assert.Contains("(+160 more)", outp);
        Assert.Contains("./m001.txt", outp);
        Assert.DoesNotContain("m041.txt", outp);
        SmokeAssert.PackRowsMaxWidth(outp);

        var (shown, _, showCode) = _h.Run(_h.Repo, "show", id);
        Assert.Equal(0, showCode);
        Assert.Contains("./m200.txt", shown);
    }

    [Fact]
    public void GrepNoMatchExit1KeepsParityAndStaysRaw()
    {
        var (raw, rawCode) = _h.RunRawTool(null, "grep", "-rn", "zzz-absent", ".");
        Assert.Equal(1, rawCode);
        Assert.Equal("", raw);

        var (outp, err, code) = _h.RunFaked(_h.Repo, null, "grep", "-rn", "zzz-absent", ".");
        Assert.Equal(rawCode, code); // parity: exit 1 is outside grep's allowlist
        SmokeAssert.NoOk(outp + err);
        Assert.Equal(raw, outp);
    }

    [Fact]
    public void FindPacksCapsAndSpoolsFullWalk()
    {
        var (raw, rawCode) = _h.RunRawTool(null, "find", ".");
        Assert.Equal(0, rawCode);

        var (outp, err, code) = _h.RunFaked(_h.Repo, null, "find", ".");
        Assert.Equal(rawCode, code); // parity
        var id = SmokeHarness.MustOkId(outp);
        Assert.Contains("(+121 more)", outp);
        SmokeAssert.PackRowsMaxWidth(outp);
        Assert.True(outp.Length < raw.Length, $"compact ({outp.Length}) not smaller than raw ({raw.Length}); stderr: {err}");

        var (shown, _, showCode) = _h.Run(_h.Repo, "show", id);
        Assert.Equal(0, showCode);
        Assert.Contains("entry160.txt", shown);
    }
}
