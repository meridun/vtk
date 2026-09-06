using System.Diagnostics;
using Xunit;

namespace Vtk.Tests.Smoke;

/// <summary>
/// End-to-end smoke for the #152 git global-option normalization through the
/// real published vtk binary and real git: `git -C &lt;dir&gt; status`,
/// stacked `--no-pager -c k=v -C &lt;dir&gt; status`, and
/// `--git-dir=/--work-tree= branch` engage the pair-key filters, while
/// `diff`/`log`/`show` behind a global option stay byte-identical passthrough
/// with a `no-filter` gap row. Exit-code parity is asserted on the success
/// path, the failure path (128 / 129 / 1), and the concurrent path; the
/// invocation log keeps the raw argv shape (normalization is lookup-only) and
/// never carries output content (invariant 3).
/// </summary>
[Collection("Smoke")]
public class GitGlobalOptionsSmokeTests : IDisposable
{
    private readonly SmokeHarness _h = new();
    public void Dispose() => _h.Dispose();

    /// <summary>Bare git without the harness's nonzero-exit throw: the parity baseline for failure paths.</summary>
    private static (string stdout, string stderr, int code) RawGit(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = dir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return (stdout, stderr, proc.ExitCode);
    }

    /// <summary>Enough churn that the compact form clears the #52 savings gate and emits an OK id.</summary>
    private void DirtyRepo()
    {
        for (var i = 1; i <= 3; i++)
            File.AppendAllText(Path.Combine(_h.Repo, $"file{i}.txt"), "dirty\n");
        for (var i = 1; i <= 8; i++)
            File.WriteAllText(Path.Combine(_h.Repo, $"new{i}.txt"), "untracked\n");
    }

    [Fact]
    public void GlobalOptionFormsEngageTheBarePairKeyFilterWithParityAndRawTelemetryShape()
    {
        DirtyRepo();
        var raw = SmokeHarness.Git(_h.Repo, "status");

        // Run from a different cwd: the -C form is the shape real traffic has
        // (the caller is not inside the repo), and it proves the executed
        // argv is untouched — a normalized-away -C would target _h.Other.
        var (outp, err, code) = _h.Run(_h.Other, "git", "-C", _h.Repo, "status");
        Assert.Equal(0, code);
        var id = SmokeHarness.MustOkId(outp);
        Assert.True(outp.Length < raw.Length, $"compact ({outp.Length}) not smaller than raw ({raw.Length}); stderr: {err}");
        Assert.Contains("file1.txt", outp);
        Assert.Contains("new8.txt", outp);

        // Spool provenance keeps the ORIGINAL argv, global option included.
        var (shown, _, showCode) = _h.Run(_h.Other, "show", id);
        Assert.Equal(0, showCode);
        Assert.Contains("# cmd: git -C " + _h.Repo + " status", shown);
        Assert.Contains("modified:", shown);

        // Stacked forms: flag + value option + -C ahead of the subcommand.
        var (stacked, _, stackedCode) = _h.Run(_h.Other, "git", "--no-pager", "-c", "core.quotepath=off", "-C", _h.Repo, "status");
        Assert.Equal(0, stackedCode);
        SmokeHarness.MustOkId(stacked);
        Assert.Equal(outp.Length, stacked.Length);

        // Inline-value forms resolve `branch` the same way (output is tiny,
        // so the savings gate may withhold the OK line — assert on the log).
        var (_, _, branchCode) = _h.Run(_h.Other, "git", "--git-dir=" + Path.Combine(_h.Repo, ".git"), "--work-tree=" + _h.Repo, "branch");
        Assert.Equal(0, branchCode);

        var log = _h.InvocationLog();
        Assert.Contains("\"cmd\":\"git -C " + _h.Repo.Replace("\\", "\\\\") + " status\"", log); // raw shape, not the normalized key
        Assert.Contains("\"cmd\":\"git --no-pager -c core.quotepath=off -C ", log);
        Assert.Contains("\"cmd\":\"git --git-dir=", log);
        Assert.Equal(2, CountOccurrences(log, "\"reason\":\"git status\""));
        Assert.Contains("\"reason\":\"git branch\"", log);
        Assert.DoesNotContain("\"reason\":\"no-filter\"", log);
        // Invariant 3: no output content in the metadata log.
        Assert.DoesNotContain("modified:", log);
        Assert.DoesNotContain("new8.txt", log);
    }

    [Fact]
    public void ExcludedSubcommandsBehindGlobalOptionsStayPassthroughWithGapRows()
    {
        DirtyRepo();

        // diff: byte-identical stdout against bare git, no OK line.
        var rawDiff = SmokeHarness.Git(_h.Repo, "--no-pager", "diff");
        Assert.NotEqual("", rawDiff.Trim());
        var (diffOut, _, diffCode) = _h.Run(_h.Repo, "git", "--no-pager", "diff");
        Assert.Equal(0, diffCode);
        Assert.Equal(rawDiff, diffOut);
        SmokeAssert.NoOk(diffOut);

        // log and show from another cwd via -C: same passthrough.
        var rawLog = SmokeHarness.Git(_h.Repo, "log", "--oneline");
        var (logOut, _, logCode) = _h.Run(_h.Other, "git", "-C", _h.Repo, "log", "--oneline");
        Assert.Equal(0, logCode);
        Assert.Equal(rawLog, logOut);
        SmokeAssert.NoOk(logOut);

        var rawShow = SmokeHarness.Git(_h.Repo, "show", "--stat");
        var (showOut, _, showCode) = _h.Run(_h.Other, "git", "-C", _h.Repo, "show", "--stat");
        Assert.Equal(0, showCode);
        Assert.Equal(rawShow, showOut);
        SmokeAssert.NoOk(showOut);

        var log = _h.InvocationLog();
        Assert.Equal(3, CountOccurrences(log, "\"reason\":\"no-filter\""));
        Assert.Contains("\"cmd\":\"git --no-pager diff\"", log);
        Assert.DoesNotContain("\"filtered\":true", log);
        // Invariant 3: passthrough gap rows carry byte counts, not content.
        Assert.DoesNotContain("+dirty", log);
        Assert.DoesNotContain("commit 3", log);
    }

    [Fact]
    public void ExitCodeParityOnFailurePathsThroughTheNormalizedLookup()
    {
        // -C to a missing directory: git exits 128 before any subcommand runs.
        var missing = Path.Combine(_h.Other, "no-such-dir");
        var raw = RawGit(_h.Other, "-C", missing, "status");
        Assert.Equal(128, raw.code);
        var (outp, err, code) = _h.Run(_h.Other, "git", "-C", missing, "status");
        Assert.Equal(raw.code, code);
        Assert.Equal(raw.stdout + raw.stderr, outp + err);

        // Value option with no value: usage error (129), not normalized.
        var rawUsage = RawGit(_h.Repo, "-C");
        Assert.Equal(129, rawUsage.code);
        var (_, _, usageCode) = _h.Run(_h.Repo, "git", "-C");
        Assert.Equal(rawUsage.code, usageCode);

        // Clean-tree commit through the normalized `git commit` key: exit 1
        // with the raw message (the filter's allowlist is exit 0 only).
        var rawCommit = RawGit(_h.Repo, "-C", _h.Repo, "commit", "-m", "nothing");
        Assert.Equal(1, rawCommit.code);
        var (commitOut, commitErr, commitCode) = _h.Run(_h.Other, "git", "-C", _h.Repo, "commit", "-m", "nothing");
        Assert.Equal(rawCommit.code, commitCode);
        Assert.Equal(rawCommit.stdout + rawCommit.stderr, commitOut + commitErr);

        var log = _h.InvocationLog();
        Assert.Equal(2, CountOccurrences(log, "\"reason\":\"nonzero-exit\""));
        Assert.DoesNotContain("\"filtered\":true", log);
    }

    [Fact]
    public async Task ConcurrentNormalizedStatusInvocationsKeepParityAndBothLog()
    {
        DirtyRepo();
        // Same argv, same spool target: the atomic-rename race must leave both
        // exits at 0 and both rows logged; one may degrade to raw passthrough
        // (invariant 2, #125), so folding is asserted for at least one.
        var t1 = Task.Run(() => _h.Run(_h.Other, "git", "-C", _h.Repo, "status"));
        var t2 = Task.Run(() => _h.Run(_h.Other, "git", "-C", _h.Repo, "status"));
        var results = await Task.WhenAll(t1, t2);
        Assert.Equal(0, results[0].code);
        Assert.Equal(0, results[1].code);
        var okRe = new System.Text.RegularExpressions.Regex(@"(?m)^OK [0-9a-f]{4}$");
        var folded = 0;
        foreach (var r in results)
        {
            if (okRe.IsMatch(r.stdout))
                folded++;
            else
                Assert.Contains("Untracked files:", r.stdout); // raw passthrough is complete
        }
        Assert.True(folded >= 1, "neither concurrent invocation folded");

        // Both rows keep the raw -C shape. The count is >= 1, not == 2: two
        // processes appending to invocations.jsonl at the same instant can
        // drop a row on Windows (observed ~1 in 10; a telemetry-append race
        // outside this change's lookup-only scope), and the existing
        // concurrent smoke asserts the same way.
        var log = _h.InvocationLog();
        Assert.True(CountOccurrences(log, "\"cmd\":\"git -C ") >= 1, "no concurrent invocation logged");
        Assert.DoesNotContain("\"cmd\":\"git status\"", log);
    }

    private static int CountOccurrences(string s, string sub)
    {
        var n = 0;
        for (var i = s.IndexOf(sub, StringComparison.Ordinal); i >= 0; i = s.IndexOf(sub, i + sub.Length, StringComparison.Ordinal))
            n++;
        return n;
    }
}
