using Xunit;

namespace Vtk.Tests.Smoke;

/// <summary>
/// Port of test/smoke/smoke_test.go's TestSmoke: exercises vtk end-to-end
/// through the real published binary. Requires `dotnet publish Vtk.Cli -c
/// Release -o Vtk.Cli/bin/smoke` to have been run (or VTK_SMOKE_BIN set).
/// </summary>
[Collection("Smoke")]
public class SmokeTests : IDisposable
{
    private readonly SmokeHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public void FullWorkflow()
    {
        // Dirty the tree: one modification, one untracked file.
        File.WriteAllText(Path.Combine(_h.Repo, "file1.txt"), "line 1 content\ndirty\n");
        File.WriteAllText(Path.Combine(_h.Repo, "newfile.txt"), "untracked\n");

        var raw = SmokeHarness.Git(_h.Repo, "status");
        var (out1, err1, code1) = _h.Run(_h.Repo, "git", "status");
        Assert.Equal(0, code1);
        var statusId = SmokeHarness.MustOkId(out1);
        Assert.True(out1.Length < raw.Length, $"compact ({out1.Length}) not smaller than raw ({raw.Length})");
        Assert.Contains("file1.txt", out1);
        Assert.Contains("newfile.txt", out1);

        // show recovers raw with provenance header
        var (showOut, _, showCode) = _h.Run(_h.Repo, "show", statusId);
        Assert.Equal(0, showCode);
        Assert.Contains("# cmd: git status", showOut);
        Assert.Contains("modified:", showOut);

        // show --grep filters lines
        var (grepOut, _, grepCode) = _h.Run(_h.Repo, "show", statusId, "--grep", "file1");
        Assert.Equal(0, grepCode);
        foreach (var line in grepOut.TrimEnd('\n').Split('\n'))
            Assert.Contains("file1", line);

        // show missing id exits 1
        var (_, _, missingCode) = _h.Run(_h.Repo, "show", "dead");
        Assert.Equal(1, missingCode);

        // exit-code parity on failure with identical output
        var rawOtherOut = "";
        var rawOtherCode = 0;
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = _h.Other,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("status");
            using var proc = System.Diagnostics.Process.Start(psi)!;
            rawOtherOut = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            rawOtherCode = proc.ExitCode;
        }
        Assert.NotEqual(0, rawOtherCode);
        var (otherOut, otherErr, otherCode) = _h.Run(_h.Other, "git", "status");
        Assert.Equal(rawOtherCode, otherCode);
        Assert.Equal(rawOtherOut, otherOut + otherErr);

        // exit-code 127 when command cannot run
        var (_, _, notFoundCode) = _h.Run(_h.Repo, "vtk-no-such-cmd-xyz");
        Assert.Equal(127, notFoundCode);

        // nothing elided emits no OK
        SmokeHarness.Git(_h.Repo, "checkout", "-q", "--", "file1.txt");
        File.Delete(Path.Combine(_h.Repo, "newfile.txt"));
        var (diffOut, _, diffCode) = _h.Run(_h.Repo, "git", "diff"); // clean tree: empty diff
        Assert.Equal(0, diffCode);
        Assert.Equal("", diffOut.Trim());

        // #52: a small lossless reformat (git branch --merged) saves ~nothing, so it
        // clears no savings bar — it prints inline with NO OK spool signal, and holds
        // exit-code parity with bare git. This is the exact regression #52 reported.
        var rawBranch = SmokeHarness.Git(_h.Repo, "branch", "--merged");
        var (branchOut, _, branchCode) = _h.Run(_h.Repo, "git", "branch", "--merged");
        Assert.Equal(0, branchCode);
        Assert.DoesNotMatch(@"(?m)^OK [0-9a-f]{4}$", branchOut);
        Assert.Contains("main", branchOut);
        Assert.Contains("main", rawBranch);

        // passthrough is gap-logged without output content
        var (revOut, _, revCode) = _h.Run(_h.Repo, "git", "rev-parse", "HEAD");
        Assert.Equal(0, revCode);
        var head = revOut.Trim();
        var (gapsOut, _, gapsCode) = _h.Run(_h.Repo, "gaps");
        Assert.Equal(0, gapsCode);
        Assert.Contains("git", gapsOut);
        var meta = _h.InvocationLog();
        Assert.DoesNotContain(head, meta);

        // command line secrets redacted in gap log
        _h.Run(_h.Repo, "git", "vtk-smoke-noop", "AWS_SECRET_ACCESS_KEY=supersecret123");
        meta = _h.InvocationLog();
        Assert.DoesNotContain("supersecret123", meta);
        Assert.Contains("AWS_SECRET_ACCESS_KEY=[REDACTED]", meta);

        // spooled content is redacted. The mutation is deliberately large so the
        // raw diff clears the #52 savings bar (>=256 bytes AND >=20%): a summarized
        // diff still spools + emits OK, keeping the redaction path exercised.
        var file2Lines = new List<string> { "line 2 content", "Authorization: Bearer sk-live-abc123" };
        for (var i = 0; i < 20; i++)
            file2Lines.Add($"padding line {i}: extra tracked content to grow the raw diff well past the savings floor");
        File.WriteAllText(Path.Combine(_h.Repo, "file2.txt"), string.Join('\n', file2Lines) + "\n");
        var (diff2Out, _, diff2Code) = _h.Run(_h.Repo, "git", "diff");
        Assert.Equal(0, diff2Code);
        var id2 = SmokeHarness.MustOkId(diff2Out);
        var (shown, _, shownCode) = _h.Run(_h.Repo, "show", id2);
        Assert.Equal(0, shownCode);
        Assert.DoesNotContain("sk-live-abc123", shown);
        Assert.Contains("Authorization: [REDACTED]", shown);
        SmokeHarness.Git(_h.Repo, "checkout", "-q", "--", "file2.txt");

        // TTL sweep removes expired entries
        var spoolPath = Path.Combine(_h.SpoolDir, statusId + ".txt");
        var old = DateTime.UtcNow.AddHours(-2);
        File.SetLastWriteTimeUtc(spoolPath, old);
        var (_, _, sweepTriggerCode) = _h.Run(_h.Repo, "git", "rev-parse", "HEAD");
        Assert.Equal(0, sweepTriggerCode);
        Assert.False(File.Exists(spoolPath));
        var (_, _, sweptShowCode) = _h.Run(_h.Repo, "show", statusId);
        Assert.Equal(1, sweptShowCode);

        // Concurrent identical invocations: neither may lose output and at
        // least one must fold. Both race a replace into the same spool file;
        // on Windows one can transiently lose and degrade to raw passthrough
        // — the designed spool-fail fallback (invariant 2, #125) — so
        // requiring both to fold over-asserts against decided behavior.
        var t1 = Task.Run(() => _h.Run(_h.Repo, "git", "log"));
        var t2 = Task.Run(() => _h.Run(_h.Repo, "git", "log"));
        Task.WaitAll(t1, t2);
        Assert.Equal(0, t1.Result.code);
        Assert.Equal(0, t2.Result.code);
        var okRe = new System.Text.RegularExpressions.Regex(@"(?m)^OK [0-9a-f]{4}$");
        var results = new[] { t1.Result, t2.Result };
        var folded = 0;
        foreach (var r in results)
        {
            if (okRe.IsMatch(r.stdout))
                folded++; // folded: raw is recoverable via the spool id
            else
                // Raw passthrough: the full git log must be present verbatim.
                Assert.Contains("commit 3: add file3.txt", r.stdout);
        }
        Assert.True(folded >= 1, "neither concurrent invocation folded");
        if (folded == 2)
            Assert.Equal(t1.Result.stdout, t2.Result.stdout);
    }

    [Fact]
    public void NullDeviceRedirect()
    {
        File.WriteAllText(Path.Combine(_h.Repo, "file1.txt"), "line 1 content\ndirty\n");

        // covered command redirected to null device is filtered
        var (_, code1) = _h.RunNull(_h.Repo, "git", "status");
        Assert.Equal(0, code1);
        var log = _h.InvocationLog();
        Assert.Contains("\"cmd\":\"git status\"", log);
        Assert.Contains("\"filtered\":true", log);
        Assert.DoesNotContain("\"tty\":true", log);
        var (gapsOut, _, gapsCode) = _h.Run(_h.Repo, "gaps");
        Assert.Equal(0, gapsCode);
        Assert.DoesNotContain("git", gapsOut);

        // uncovered command redirected to null device is a no-filter gap
        var (_, code2) = _h.RunNull(_h.Repo, "git", "rev-parse", "HEAD");
        Assert.Equal(0, code2);
        log = _h.InvocationLog();
        Assert.Contains("\"reason\":\"no-filter\"", log);
        var (gapsOut2, _, gapsCode2) = _h.Run(_h.Repo, "gaps");
        Assert.Equal(0, gapsCode2);
        Assert.Contains("git", gapsOut2);

        // nonzero exit keeps parity and is excluded from gaps
        var (stderr3, code3) = _h.RunNull(_h.Repo, "git", "diff", "vtk-no-such-ref");
        Assert.NotEqual(0, code3);
        Assert.Contains("vtk-no-such-ref", stderr3);
        log = _h.InvocationLog();
        Assert.Contains("\"reason\":\"nonzero-exit\"", log);
        var (gapsOut3, _, gapsCode3) = _h.Run(_h.Repo, "gaps");
        Assert.Equal(0, gapsCode3);
        Assert.Equal(1, CountOccurrences(gapsOut3, "git"));
    }

    [Fact]
    public void GapsSinceWindow()
    {
        // #140 through the real binary: a just-logged no-filter gap is inside
        // a trailing window and outside a future-dated one; the header prints
        // only when the flag is given; a bad value is a usage failure.
        var (_, code) = _h.RunNull(_h.Repo, "git", "rev-parse", "HEAD");
        Assert.Equal(0, code);

        var (bareOut, _, bareCode) = _h.Run(_h.Repo, "gaps");
        Assert.Equal(0, bareCode);
        Assert.Contains("git", bareOut);
        Assert.DoesNotContain("window:", bareOut);

        var (recentOut, _, recentCode) = _h.Run(_h.Repo, "gaps", "--since", "1h");
        Assert.Equal(0, recentCode);
        Assert.StartsWith("window: since ", recentOut);
        Assert.Contains("(1h)", recentOut);
        Assert.Contains("git", recentOut);

        var (futureOut, _, futureCode) = _h.Run(_h.Repo, "gaps", "--since", "2099-01-01");
        Assert.Equal(0, futureCode);
        Assert.Contains("window: since 2099-01-01T00:00:00Z (2099-01-01)", futureOut);
        Assert.Contains("no gap entries", futureOut);
        Assert.DoesNotContain("git", futureOut);

        var (_, badErr, badCode) = _h.Run(_h.Repo, "gaps", "--since", "nope");
        Assert.Equal(2, badCode);
        Assert.Contains("invalid --since", badErr);

        // out-of-range duration (audit B1, #140): usage failure, never an unhandled exception
        var (_, hugeErr, hugeCode) = _h.Run(_h.Repo, "gaps", "--since", "2147483647d");
        Assert.Equal(2, hugeCode);
        Assert.Contains("invalid --since", hugeErr);
        Assert.DoesNotContain("Unhandled exception", hugeErr);
    }

    [Fact]
    public void GapsKeysPairFamiliesAfterGitGlobalOptions()
    {
        // #139 through the real binary: unfiltered git subcommands aggregate
        // under `git <sub>` (never one undifferentiated `git` row), known git
        // global options are normalized before the subcommand is chosen, and
        // a filtered subcommand never ranks. `vtk gain` keeps the argv[0] grain.
        Assert.Equal(0, _h.RunNull(_h.Repo, "git", "rev-parse", "HEAD").code);
        Assert.Equal(0, _h.RunNull(_h.Repo, "git", "-C", ".", "rev-parse", "HEAD").code);
        Assert.Equal(0, _h.RunNull(_h.Repo, "git", "--no-pager", "log", "-1").code);
        Assert.Equal(0, _h.RunNull(_h.Repo, "git", "status").code);

        var (gapsOut, _, gapsCode) = _h.Run(_h.Repo, "gaps");
        Assert.Equal(0, gapsCode);
        Assert.Matches(@"(?m)^git rev-parse\s+2\s", gapsOut);
        Assert.Matches(@"(?m)^git log\s+1\s", gapsOut);
        Assert.DoesNotMatch(@"(?m)^git\s+\d", gapsOut);
        Assert.DoesNotContain("git status", gapsOut);
        Assert.DoesNotContain("--no-pager", gapsOut);

        var (gainOut, _, gainCode) = _h.Run(_h.Repo, "gain");
        Assert.Equal(0, gainCode);
        Assert.Matches(@"(?m)^git\s+4\s", gainOut);
        Assert.DoesNotContain("git rev-parse", gainOut);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) != -1)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }
}

/// <summary>Port of test/smoke/reserved_meta_smoke_test.go.</summary>
[Collection("Smoke")]
public class ReservedMetaSmokeTests : IDisposable
{
    private readonly SmokeHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public void ProxyFailsClearlyWithExit2()
    {
        var (stdout, stderr, code) = _h.Run(_h.Repo, "proxy");
        Assert.Equal(2, code);
        Assert.Contains("not implemented yet", stderr);
        Assert.DoesNotContain("executable file not found", stderr);
        Assert.Equal("", stdout);
    }

    [Fact]
    public void ReservedWordsLeaveNoTrace()
    {
        _h.Run(_h.Repo, "proxy");
        Assert.False(File.Exists(Path.Combine(_h.Home, "vtk", "invocations.jsonl")));
        if (Directory.Exists(_h.SpoolDir))
            Assert.Empty(Directory.GetFileSystemEntries(_h.SpoolDir));
    }

    [Fact]
    public void UnknownNonReservedWordKeepsExec127Path()
    {
        var (_, _, code) = _h.Run(_h.Repo, "vtk-not-reserved-xyz");
        Assert.Equal(127, code);
    }

    [Theory]
    [InlineData("help")]
    [InlineData("--help")]
    [InlineData("-h")]
    public void HelpSpellingsPrintUsageInProcess(string spelling)
    {
        // #118: these used to spawn-fail as external commands (`--help`, `-h`)
        // or reach Windows cmd's HELP (`help`).
        var (stdout, stderr, code) = _h.Run(_h.Repo, spelling);
        Assert.Equal(0, code);
        Assert.Contains("usage: vtk", stdout);
        Assert.Equal("", stderr);
        // Resolved before the store opens: no telemetry trace, like `proxy`.
        Assert.False(File.Exists(Path.Combine(_h.Home, "vtk", "invocations.jsonl")));
    }

    [Fact]
    public void SpawnFailureIsLoggedButExcludedFromGaps()
    {
        // #118: a child that never started is logged (fallback always logs)
        // under spawn-fail, and its family never ranks in `vtk gaps`.
        var (_, _, code) = _h.Run(_h.Repo, "definitely-not-a-command-xyz");
        Assert.Equal(127, code);
        var log = _h.InvocationLog();
        Assert.Contains("\"cmd\":\"definitely-not-a-command-xyz\"", log);
        Assert.Contains("\"reason\":\"spawn-fail\"", log);

        var (gapsOut, _, gapsCode) = _h.Run(_h.Repo, "gaps");
        Assert.Equal(0, gapsCode);
        Assert.DoesNotContain("definitely-not-a-command-xyz", gapsOut);
    }

    [Fact]
    public void ShippedSubcommandsUnaffected()
    {
        var (_, stderr, code) = _h.Run(_h.Repo, "git", "status");
        Assert.Equal(0, code);
        var (_, _, gapsCode) = _h.Run(_h.Repo, "gaps");
        Assert.Equal(0, gapsCode);
    }

    [Fact]
    public void GainReportsCumulativeSavings()
    {
        _h.Run(_h.Repo, "git", "status"); // ensure at least one logged invocation
        var (outp, _, code) = _h.Run(_h.Repo, "gain");
        Assert.Equal(0, code);
        Assert.Contains("cumulative savings:", outp);
    }
}

/// <summary>
/// Real-run smoke for the #98 version subcommand: both reserved spellings
/// resolve in-process through the real binary (never exec passthrough),
/// print a single "vtk &lt;sha&gt;" line, and leave no spool/gap-log trace.
/// </summary>
[Collection("Smoke")]
public class VersionSmokeTests : IDisposable
{
    private readonly SmokeHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Theory]
    [InlineData("version")]
    [InlineData("--version")]
    public void ReservedSpellingsPrintShaAndLeaveNoTrace(string spelling)
    {
        var (stdout, stderr, code) = _h.Run(_h.Repo, spelling);
        Assert.Equal(0, code);
        Assert.Equal("", stderr);
        // Published binaries carry a "+<sha>" informational-version stamp
        // (SourceLink full sha by default; sdlc-maint shortens it), so the
        // real binary always resolves a sha rather than the unknown branch.
        Assert.Matches(@"^vtk [0-9a-f]{7,40}\n$", stdout);

        // No exec, no spool open, no gap log — the wrap path is untouched.
        Assert.False(File.Exists(Path.Combine(_h.Home, "vtk", "invocations.jsonl")));
        if (Directory.Exists(_h.SpoolDir))
            Assert.Empty(Directory.GetFileSystemEntries(_h.SpoolDir));
    }

    [Fact]
    public void UnexpectedArgumentExits2WithUsage()
    {
        var (stdout, stderr, code) = _h.Run(_h.Repo, "version", "extra");
        Assert.Equal(2, code);
        Assert.Contains("usage: vtk version", stderr);
        Assert.Equal("", stdout);
    }
}

/// <summary>
/// Real-run smoke for the #58 gain rollups: dollarized summary plus the
/// --daily / --graph / --history flavors through the real binary.
/// </summary>
[Collection("Smoke")]
public class GainRollupsSmokeTests : IDisposable
{
    private readonly SmokeHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public void RollupFlagsReportDollarizedSavings()
    {
        // Dirty the tree and wrap a covered command so the log has one
        // countable invocation feeding every rollup.
        File.WriteAllText(Path.Combine(_h.Repo, "file1.txt"), "line 1 content\ndirty\n");
        var (_, _, wrapCode) = _h.Run(_h.Repo, "git", "status");
        Assert.Equal(0, wrapCode);

        var (outp, _, code) = _h.Run(_h.Repo, "gain", "--daily", "--graph", "--history");
        Assert.Equal(0, code);
        Assert.Contains("cumulative savings:", outp);
        Assert.Contains("bytes/4 heuristic", outp);   // dollarized summary line
        Assert.Contains("~USD", outp);                // --daily table header
        Assert.Contains("daily saved bytes", outp);   // --graph section
        Assert.Contains("recent invocations", outp);  // --history section
        Assert.Contains("git status", outp);          // history row names the command
    }

    [Fact]
    public void UnknownGainFlagExits2WithUsage()
    {
        var (stdout, stderr, code) = _h.Run(_h.Repo, "gain", "--bogus");
        Assert.Equal(2, code);
        Assert.Contains("usage: vtk gain", stderr);
        Assert.Equal("", stdout);
    }

    [Fact]
    public void SessionFlagAttributesInvocationsToTranscriptWindow()
    {
        // One countable invocation logged now.
        File.WriteAllText(Path.Combine(_h.Repo, "file1.txt"), "line 1 content\ndirty\n");
        var (_, _, wrapCode) = _h.Run(_h.Repo, "git", "status");
        Assert.Equal(0, wrapCode);

        // A transcript whose timestamp window contains "now": the invocation
        // must be attributed to it and rendered dollarized (#46).
        var sessions = Path.Combine(Path.GetTempPath(), "vtk-smoke-sess-" + Path.GetRandomFileName());
        Directory.CreateDirectory(sessions);
        try
        {
            var start = DateTime.UtcNow.AddHours(-1).ToString("yyyy-MM-ddTHH:mm:ssZ");
            var end = DateTime.UtcNow.AddHours(1).ToString("yyyy-MM-ddTHH:mm:ssZ");
            File.WriteAllLines(Path.Combine(sessions, "aaaa1111-2222-3333-4444-555566667777.jsonl"), new[]
            {
                $"{{\"timestamp\":\"{start}\",\"type\":\"user\"}}",
                "not json {{{",
                $"{{\"timestamp\":\"{end}\",\"type\":\"assistant\"}}",
            });

            var (outp, _, code) = _h.Run(_h.Repo, "gain", "--session", "--sessions", sessions);
            Assert.Equal(0, code);
            Assert.Contains("savings per session", outp);
            Assert.Contains("SESSION", outp);   // table header rendered
            Assert.Contains("aaaa1111", outp);  // short id = file-stem prefix
            Assert.Contains("~USD", outp);      // dollarized column

            // A window that excludes the invocation: view renders the
            // no-attribution note instead of a table, still exit 0.
            File.WriteAllLines(Path.Combine(sessions, "aaaa1111-2222-3333-4444-555566667777.jsonl"), new[]
            {
                "{\"timestamp\":\"2001-01-01T00:00:00Z\",\"type\":\"user\"}",
                "{\"timestamp\":\"2001-01-01T01:00:00Z\",\"type\":\"assistant\"}",
            });
            var (missOut, _, missCode) = _h.Run(_h.Repo, "gain", "--session", "--sessions", sessions);
            Assert.Equal(0, missCode);
            Assert.Contains("no logged invocations fall inside a session window", missOut);
        }
        finally
        {
            try { Directory.Delete(sessions, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void SessionFlagMissingDirNotesAndExitsZero()
    {
        File.WriteAllText(Path.Combine(_h.Repo, "file1.txt"), "line 1 content\ndirty\n");
        var (_, _, wrapCode) = _h.Run(_h.Repo, "git", "status");
        Assert.Equal(0, wrapCode);

        var missing = Path.Combine(Path.GetTempPath(), "vtk-smoke-nosess-" + Path.GetRandomFileName());
        var (outp, _, code) = _h.Run(_h.Repo, "gain", "--session", "--sessions", missing);
        Assert.Equal(0, code); // a savings report, not an environment failure
        Assert.Contains("no session transcripts in", outp);

        // Plain `gain` stays free of session output.
        var (plain, _, plainCode) = _h.Run(_h.Repo, "gain");
        Assert.Equal(0, plainCode);
        Assert.DoesNotContain("savings per session", plain);
    }
}

/// <summary>
/// Real-run smoke for the #61 ports: the declarative TOML filter engine (#40)
/// exercised end-to-end via the embedded cargo def and a stub cargo.cmd on
/// PATH, and the `vtk gaps --file-issues` surface (#34) on its hermetic
/// paths (empty candidate set / arg errors — nothing here shells to gh).
/// </summary>
[Collection("Smoke")]
public class TomlGapsSmokeTests : IDisposable
{
    private readonly SmokeHarness _h = new();
    private readonly string _stubDir;

    public TomlGapsSmokeTests()
    {
        // A stub cargo.cmd that replays canned cargo output: progress chatter
        // plus a Finished line on success (exit 0), compile errors on --fail
        // (exit 101). The chatter is large enough that stripping it clears
        // the #52 savings bar, so the filtered run spools and emits OK <id>.
        _stubDir = Path.Combine(Path.GetTempPath(), "vtk-smoke-stub-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_stubDir);

        var okLines = new List<string> { "   Compiling libc v0.2.169" };
        for (var i = 0; i < 30; i++) okLines.Add($"   Compiling smoke-crate-{i} v0.1.{i}");
        okLines.Add("    Checking myapp v0.1.0 (/home/u/myapp)");
        okLines.Add("    Finished `dev` profile [unoptimized + debuginfo] target(s) in 4.21s");
        File.WriteAllText(Path.Combine(_stubDir, "cargo-ok.txt"), string.Join("\r\n", okLines) + "\r\n");

        File.WriteAllText(Path.Combine(_stubDir, "cargo-fail.txt"), string.Join("\r\n", new[]
        {
            "   Compiling myapp v0.1.0 (/home/u/myapp)",
            "error[E0425]: cannot find value `x` in this scope",
            "error: could not compile `myapp` (bin \"myapp\") due to 1 previous error",
        }) + "\r\n");

        File.WriteAllText(Path.Combine(_stubDir, "cargo.cmd"),
            "@echo off\r\n" +
            "if \"%2\"==\"--fail\" (\r\n" +
            "  type \"%~dp0cargo-fail.txt\"\r\n" +
            "  exit /b 101\r\n" +
            ")\r\n" +
            "type \"%~dp0cargo-ok.txt\"\r\n" +
            "exit /b 0\r\n");
    }

    public void Dispose()
    {
        _h.Dispose();
        try { Directory.Delete(_stubDir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private (string stdout, string stderr, int code) RunWithStub(params string[] args) =>
        _h.RunEnv(_h.Repo, new Dictionary<string, string>
        {
            ["PATH"] = _stubDir + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? ""),
        }, args);

    [Fact]
    public void CargoTomlFilterCompactsCleanBuild()
    {
        // Clean build (exit 0): the embedded cargo TOML def strips progress
        // chatter via the registry's regex fallback path, keeps the Finished
        // summary, spools the raw, and emits OK <id>.
        var (stdout, _, code) = RunWithStub("cargo", "build");
        Assert.Equal(0, code);
        Assert.Contains("Finished `dev` profile", stdout);
        Assert.DoesNotContain("Compiling", stdout);
        var id = SmokeHarness.MustOkId(stdout);

        // show recovers the raw output with provenance.
        var (shown, _, showCode) = _h.Run(_h.Repo, "show", id);
        Assert.Equal(0, showCode);
        Assert.Contains("# cmd: cargo build", shown);
        Assert.Contains("Compiling libc v0.2.169", shown);
        Assert.Contains("Compiling smoke-crate-29", shown);

        // A filtered family is coverage, not a gap.
        var (gapsOut, _, gapsCode) = _h.Run(_h.Repo, "gaps");
        Assert.Equal(0, gapsCode);
        Assert.DoesNotContain("cargo", gapsOut);
    }

    [Fact]
    public void CargoFailurePassesThroughRawWithExitParity()
    {
        // Compile error (exit 101): 101 is outside the def's exit_codes {0},
        // so the output passes through raw — progress chatter intact, no OK —
        // and vtk returns the child's own exit code (invariant 1).
        var (stdout, stderr, code) = RunWithStub("cargo", "build", "--fail");
        Assert.Equal(101, code);
        var combined = stdout + stderr;
        Assert.Contains("Compiling myapp v0.1.0", combined);
        Assert.Contains("error: could not compile `myapp`", combined);
        Assert.DoesNotMatch(@"(?m)^OK [0-9a-f]{4}$", stdout);
    }

    [Fact]
    public void GapsFileIssuesDryRunEmptyStoreFilesNothing()
    {
        // No gap families at all: --file-issues reports the empty candidate
        // set and exits 0 before ever consulting gh (dry-run is the default).
        var (stdout, _, code) = _h.Run(_h.Repo, "gaps", "--file-issues");
        Assert.Equal(0, code);
        Assert.Contains("no gap families over threshold", stdout);
    }

    [Fact]
    public void GapsFileIssuesFloorsClampWithStderrNotes()
    {
        // Below-floor thresholds clamp up (anti-spam) with a note per flag,
        // and the clamped values are what the report line echoes back.
        var (stdout, stderr, code) = _h.Run(_h.Repo, "gaps", "--file-issues", "--min-bytes", "1", "--min-calls", "1");
        Assert.Equal(0, code);
        Assert.Contains("--min-bytes 1 below floor; using 4096", stderr);
        Assert.Contains("--min-calls 1 below floor; using 2", stderr);
        Assert.Contains("no gap families over threshold (min-bytes=4096, min-calls=2)", stdout);
    }

    [Fact]
    public void GapsArgErrorsExit2()
    {
        var (_, err1, code1) = _h.Run(_h.Repo, "gaps", "--bogus");
        Assert.Equal(2, code1);
        Assert.Contains("unexpected argument", err1);

        var (_, err2, code2) = _h.Run(_h.Repo, "gaps", "--yes");
        Assert.Equal(2, code2);
        Assert.Contains("require --file-issues", err2);

        var (_, err3, code3) = _h.Run(_h.Repo, "gaps", "--file-issues", "--min-bytes");
        Assert.Equal(2, code3);
        Assert.Contains("--min-bytes requires a value", err3);
    }
}

public class WingetSmokeTests : IDisposable
{
    private readonly SmokeHarness _h = new();
    private readonly string _stubDir;

    public WingetSmokeTests()
    {
        // A stub winget.cmd replaying the real `winget install --id jqlang.jq`
        // capture (v1.29.280, CRLF; same capture as the Filter/testdata/winget
        // golden pair). The sentinel id vtk.smoke.fail replays a real
        // no-match failure with winget's exit code 20. Verified against real
        // winget on 2026-07-20 (#105): install strips license/Downloading/
        // hash/Starting and fires OK; a failing install passes through raw
        // with exit 20 parity.
        _stubDir = Path.Combine(Path.GetTempPath(), "vtk-smoke-stub-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_stubDir);

        File.WriteAllText(Path.Combine(_stubDir, "winget-ok.txt"), string.Join("\r\n", new[]
        {
            "Found jq [jqlang.jq] Version 1.8.2",
            "This application is licensed to you by its owner.",
            "Microsoft is not responsible for, nor does it grant any licenses to, third-party packages.",
            "Downloading https://github.com/jqlang/jq/releases/download/jq-1.8.2/jq-windows-amd64.exe",
            "Successfully verified installer hash",
            "Starting package install...",
            "Path environment variable modified; restart your shell to use the new value.",
            "Command line alias added: \"jq\"",
            "Successfully installed",
        }) + "\r\n");

        File.WriteAllText(Path.Combine(_stubDir, "winget.cmd"),
            "@echo off\r\n" +
            "if \"%3\"==\"vtk.smoke.fail\" (\r\n" +
            "  echo No package found matching input criteria.\r\n" +
            "  exit /b 20\r\n" +
            ")\r\n" +
            "type \"%~dp0winget-ok.txt\"\r\n" +
            "exit /b 0\r\n");
    }

    public void Dispose()
    {
        _h.Dispose();
        try { Directory.Delete(_stubDir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private (string stdout, string stderr, int code) RunWithStub(params string[] args) =>
        _h.RunEnv(_h.Repo, new Dictionary<string, string>
        {
            ["PATH"] = _stubDir + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? ""),
        }, args);

    [Fact]
    public void WingetInstallStripsSpamAndSpools()
    {
        // Successful install (exit 0): license boilerplate, Downloading, hash
        // verification, and Starting-package chatter stripped; Found/result
        // lines kept; savings (300 of 470 bytes) clear the #52 bar, so the
        // raw spools and OK <id> fires.
        var (stdout, _, code) = RunWithStub("winget", "install", "--id", "jqlang.jq");
        Assert.Equal(0, code);
        Assert.Contains("Found jq [jqlang.jq] Version 1.8.2", stdout);
        Assert.Contains("Successfully installed", stdout);
        Assert.Contains("Command line alias added", stdout);
        Assert.DoesNotContain("licensed to you by its owner", stdout);
        Assert.DoesNotContain("Downloading", stdout);
        Assert.DoesNotContain("Successfully verified installer hash", stdout);
        Assert.DoesNotContain("Starting package install", stdout);
        var id = SmokeHarness.MustOkId(stdout);

        // show recovers the full raw with provenance (nothing lost).
        var (shown, _, showCode) = _h.Run(_h.Repo, "show", id);
        Assert.Equal(0, showCode);
        Assert.Contains("# cmd: winget install --id jqlang.jq", shown);
        Assert.Contains("This application is licensed to you by its owner.", shown);
        Assert.Contains("Starting package install...", shown);
    }

    [Fact]
    public void WingetFailurePassesThroughRawWithExitParity()
    {
        // No-match failure (exit 20): outside the def's exit_codes {0}, so the
        // output passes through raw with no OK and vtk returns winget's own
        // exit code (invariant 1).
        var (stdout, stderr, code) = RunWithStub("winget", "install", "--id", "vtk.smoke.fail");
        Assert.Equal(20, code);
        Assert.Contains("No package found matching input criteria.", stdout + stderr);
        Assert.DoesNotMatch(@"(?m)^OK [0-9a-f]{4}$", stdout);
    }

    [Fact]
    public void WingetListDoesNotMatchAndPassesThrough()
    {
        // `winget list` is outside the def's verb set (install|upgrade|
        // uninstall|download): no filter runs, so even strippable lines pass
        // through untouched.
        var (stdout, _, code) = RunWithStub("winget", "list");
        Assert.Equal(0, code);
        Assert.Contains("This application is licensed to you by its owner.", stdout);
        Assert.Contains("Downloading", stdout);
        Assert.DoesNotMatch(@"(?m)^OK [0-9a-f]{4}$", stdout);
    }
}
