using System.Diagnostics;
using System.Text.RegularExpressions;
using Xunit;

namespace Vtk.Tests.Smoke;

/// <summary>
/// End-to-end smoke for the #167 registry reuse through the real published
/// vtk binary: `gh pr diff` (fake gh, unified diff on the `gh pr` key) rides
/// <c>Git.Diff</c> with its #135 floor; `git grep -n` (real git) rides
/// <c>Files.Grep</c>; `git merge` (real git) rides <c>Git.Pull</c>. Each
/// family logs under its own entry name (never a `no-filter` gap row), the
/// exit-1 shapes (grep no-match, merge conflict, gh failure) keep exit-code
/// parity — raw below the fold floor, tail-folded at or above it (#165) — and
/// the invocation log never carries output content (invariant 3).
/// </summary>
[Collection("Smoke")]
public class RegistryReuseSmokeTests : IDisposable
{
    private const int FloorBytes = 64 * 1024;
    private const string DiffMarker = "pr-diff-marker-167";
    private const string GrepMarker = "grep-needle-167";

    private readonly SmokeHarness _h = new();
    public void Dispose() => _h.Dispose();

    private static Dictionary<string, string> GhCode(int n) => new() { ["VTK_FAKE_GH_CODE"] = n.ToString() };

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

    // ---- gh pr diff -> Git.Diff --------------------------------------------

    [Fact]
    public void GhPrDiffBelowFloorIsByteIdenticalLoggedUnderGhPr()
    {
        var (raw, rawCode) = _h.RunRawTool(null, "gh", "pr", "diff", "42");
        Assert.Equal(0, rawCode);
        Assert.Contains("@@", raw);
        Assert.True(raw.Length < FloorBytes, $"fake diff unexpectedly at/above the floor ({raw.Length})");

        var (outp, err, code) = _h.RunFaked(_h.Repo, null, "gh", "pr", "diff", "42");
        Assert.Equal(rawCode, code); // parity
        Assert.Equal(raw, outp);
        Assert.Equal("", err);
        SmokeAssert.NoOk(outp);

        var log = _h.InvocationLog();
        Assert.Contains("\"reason\":\"gh pr\"", log);
        Assert.Contains("\"filtered\":true", log);
        Assert.DoesNotContain("\"reason\":\"no-filter\"", log);
        Assert.DoesNotContain(DiffMarker, log); // invariant 3

        var (gaps, _, gapsCode) = _h.Run(_h.Repo, "gaps");
        Assert.Equal(0, gapsCode);
        Assert.DoesNotContain("gh pr", SmokeAssert.GapTable(gaps));
    }

    [Fact]
    public void GhPrDiffAboveFloorFoldsToStatsWithRecoverableSpool()
    {
        var (raw, rawCode) = _h.RunRawTool(null, "gh", "pr", "diff", "big");
        Assert.Equal(0, rawCode);
        Assert.True(raw.Length >= FloorBytes, $"fake diff below the floor ({raw.Length})");

        var (outp, err, code) = _h.RunFaked(_h.Repo, null, "gh", "pr", "diff", "big");
        Assert.Equal(rawCode, code); // parity
        var id = SmokeHarness.MustOkId(outp);
        Assert.Contains("README.md | +1 -0", outp);
        Assert.Contains("internal/big.go | +1200 -0", outp);
        Assert.Contains("2 files, +1201 -0", outp);
        Assert.DoesNotContain("@@", outp);
        Assert.DoesNotContain(DiffMarker, outp);
        Assert.True(outp.Length < raw.Length / 100, $"fold ({outp.Length}) not >= 99% smaller than raw ({raw.Length}); stderr: {err}");

        var (shown, _, showCode) = _h.Run(_h.Repo, "show", id);
        Assert.Equal(0, showCode);
        Assert.Contains("# cmd: gh pr diff big", shown);
        Assert.Contains("@@", shown);
        Assert.Contains(DiffMarker + " padding line 1199", shown);

        var log = _h.InvocationLog();
        Assert.Contains("\"reason\":\"gh pr\"", log);
        Assert.DoesNotContain("\"reason\":\"no-filter\"", log);
        Assert.DoesNotContain(DiffMarker, log); // invariant 3
    }

    [Fact]
    public void GhPrListStillRoutesToPrListThroughTheDispatcher()
    {
        var (outp, _, code) = _h.RunFaked(_h.Repo, null, "gh", "pr", "list");
        Assert.Equal(0, code);
        SmokeHarness.MustOkId(outp);
        Assert.Contains("#59 open Wire up coverage for the number 59 filter family", outp);
        Assert.DoesNotContain("some-longish-branch-name", outp);
        Assert.Contains("\"reason\":\"gh pr\"", _h.InvocationLog());
    }

    [Fact]
    public void GhPrDiffNonzeroExitBelowFloorStaysRawWithParity()
    {
        var (raw, rawCode) = _h.RunRawTool(GhCode(1), "gh", "pr", "diff", "42");
        Assert.Equal(1, rawCode);
        Assert.True(raw.Length < FloorBytes, $"fake diff unexpectedly at/above the floor ({raw.Length})");

        var (outp, err, code) = _h.RunFaked(_h.Repo, GhCode(1), "gh", "pr", "diff", "42");
        Assert.Equal(rawCode, code); // parity: exit 1 is outside the `gh pr` allowlist
        SmokeAssert.NoOk(outp + err);
        // Below the floor a failure is byte-identical raw (#165, invariant 2).
        Assert.Equal(raw, outp + err);
        Assert.Contains("\"reason\":\"nonzero-exit\"", _h.InvocationLog());
    }

    [Fact]
    public void GhPrDiffNonzeroExitAboveFloorFoldsToTailWithParity()
    {
        var (raw, rawCode) = _h.RunRawTool(GhCode(1), "gh", "pr", "diff", "big");
        Assert.Equal(1, rawCode);
        Assert.True(raw.Length >= FloorBytes, $"fake diff below the floor ({raw.Length})");

        var (outp, err, code) = _h.RunFaked(_h.Repo, GhCode(1), "gh", "pr", "diff", "big");
        Assert.Equal(rawCode, code); // parity: exit 1 is outside the `gh pr` allowlist
        // A failure at or above the floor takes the failure tail-fold (#165):
        // bounded tail + OK <id>, full raw recoverable, `failure-fold` row
        // attributed to the covered `gh pr` family (not a coverage gap).
        var id = SmokeHarness.MustOkId(outp);
        Assert.True(outp.Length <= 8 * 1024 + 16, $"failure fold not bounded ({outp.Length} bytes); stderr: {err}");
        Assert.Contains(DiffMarker + " padding line 1199", outp);
        Assert.DoesNotContain(DiffMarker + " padding line 0:", outp);
        Assert.DoesNotContain("internal/big.go | +1200 -0", outp); // tail, not stats

        var (shown, _, showCode) = _h.Run(_h.Repo, "show", id);
        Assert.Equal(0, showCode);
        Assert.Contains("# cmd: gh pr diff big", shown);
        Assert.Contains(DiffMarker + " padding line 0:", shown);
        Assert.Contains(DiffMarker + " padding line 1199", shown);

        var log = _h.InvocationLog();
        Assert.Contains("\"reason\":\"failure-fold\"", log);
        Assert.Contains("\"filtered\":true", log);
        Assert.DoesNotContain("\"reason\":\"nonzero-exit\"", log);
        Assert.DoesNotContain(DiffMarker, log); // invariant 3

        var (gaps, _, gapsCode) = _h.Run(_h.Repo, "gaps");
        Assert.Equal(0, gapsCode);
        Assert.DoesNotContain("gh pr", SmokeAssert.GapTable(gaps));
    }

    // ---- git grep -> Files.Grep --------------------------------------------

    /// <summary>Commits a tracked file with many marker lines so `git grep -n` clears the per-file cap and the #52 savings bar.</summary>
    private void SeedGrepFile()
    {
        var lines = new List<string>();
        for (var i = 1; i <= 30; i++)
            lines.Add($"{GrepMarker} occurrence {i}: some tracked content that pads each match line for the savings bar");
        File.WriteAllText(Path.Combine(_h.Repo, "needles.txt"), string.Join('\n', lines) + "\n");
        File.WriteAllText(Path.Combine(_h.Repo, "file1.txt"), $"line 1 content\n{GrepMarker} single hit in file1\n");
        SmokeHarness.Git(_h.Repo, "add", ".");
        SmokeHarness.Git(_h.Repo, "commit", "-q", "-m", "commit 4: seed grep needles");
    }

    [Fact]
    public void GitGrepNCapsMatchesPerFileWithRecoverableSpool()
    {
        SeedGrepFile();
        var raw = SmokeHarness.Git(_h.Repo, "grep", "-n", GrepMarker);
        Assert.Equal(31, raw.TrimEnd('\n').Split('\n').Length);

        var (outp, err, code) = _h.Run(_h.Repo, "git", "grep", "-n", GrepMarker);
        Assert.Equal(0, code); // parity
        var id = SmokeHarness.MustOkId(outp);
        Assert.Equal(5, Regex.Matches(outp, @"(?m)^needles\.txt:\d+:").Count);
        Assert.Contains("needles.txt: (+25 more)", outp);
        Assert.Single(Regex.Matches(outp, @"(?m)^file1\.txt:\d+:"));
        Assert.DoesNotContain("file1.txt: (+", outp);
        Assert.DoesNotContain("occurrence 30", outp);
        Assert.True(outp.Length < raw.Length, $"compact ({outp.Length}) not smaller than raw ({raw.Length}); stderr: {err}");

        var (shown, _, showCode) = _h.Run(_h.Repo, "show", id);
        Assert.Equal(0, showCode);
        Assert.Contains("# cmd: git grep -n " + GrepMarker, shown);
        Assert.Contains("occurrence 30", shown);

        var log = _h.InvocationLog();
        Assert.Contains("\"reason\":\"git grep\"", log);
        Assert.DoesNotContain("\"reason\":\"no-filter\"", log);
        Assert.DoesNotContain("occurrence", log); // invariant 3

        var (gaps, _, gapsCode) = _h.Run(_h.Repo, "gaps");
        Assert.Equal(0, gapsCode);
        Assert.DoesNotContain("git grep", SmokeAssert.GapTable(gaps));
    }

    [Fact]
    public void GitGrepNoMatchExit1KeepsParityAndStaysRaw()
    {
        var raw = RawGit(_h.Repo, "grep", "-n", "zzz-absent-167");
        Assert.Equal(1, raw.code);
        Assert.Equal("", raw.stdout);

        var (outp, err, code) = _h.Run(_h.Repo, "git", "grep", "-n", "zzz-absent-167");
        Assert.Equal(raw.code, code); // parity: exit 1 is outside the `git grep` allowlist
        SmokeAssert.NoOk(outp + err);
        Assert.Equal(raw.stdout, outp);
        Assert.Equal(raw.stderr, err);
        Assert.Contains("\"reason\":\"nonzero-exit\"", _h.InvocationLog());
    }

    [Fact]
    public async Task ConcurrentGitGrepKeepsParityAndEveryLogRow()
    {
        SeedGrepFile();
        // Same argv, same spool target: the atomic-rename race must leave both
        // exits at 0; one may degrade to raw passthrough (invariant 2, #125),
        // so compaction is asserted for at least one and completeness for the rest.
        var t1 = Task.Run(() => _h.Run(_h.Repo, "git", "grep", "-n", GrepMarker));
        var t2 = Task.Run(() => _h.Run(_h.Repo, "git", "grep", "-n", GrepMarker));
        var results = await Task.WhenAll(t1, t2);
        Assert.Equal(0, results[0].code);
        Assert.Equal(0, results[1].code);
        var okRe = new Regex(@"(?m)^OK [0-9a-f]{4}$");
        var compacted = 0;
        foreach (var r in results)
        {
            if (okRe.IsMatch(r.stdout))
                compacted++;
            else
                Assert.Contains("occurrence 30", r.stdout); // raw passthrough is complete
        }
        Assert.True(compacted >= 1, "neither concurrent invocation compacted");
        Assert.Equal(2, CountOccurrences(_h.InvocationLog(), "\"cmd\":\"git grep -n " + GrepMarker + "\""));
    }

    // ---- git merge -> Git.Pull ---------------------------------------------

    /// <summary>Cuts <paramref name="branch"/> from <paramref name="from"/> with three new files committed, then returns to main.</summary>
    private void SeedBranch(string branch, string from, string prefix)
    {
        SmokeHarness.Git(_h.Repo, "checkout", "-q", "-b", branch, from);
        for (var i = 1; i <= 3; i++)
            File.WriteAllText(Path.Combine(_h.Repo, $"{prefix}{i}.txt"), $"{prefix} {i}\n");
        SmokeHarness.Git(_h.Repo, "add", ".");
        SmokeHarness.Git(_h.Repo, "commit", "-q", "-m", $"{prefix} work");
        SmokeHarness.Git(_h.Repo, "checkout", "-q", "main");
    }

    [Fact]
    public void GitMergeFastForwardAndOrtDropDiffstatLines()
    {
        // Fast-forward: `Updating a..b` + `Fast-forward` + summary survive;
        // per-file stat and `create mode` lines are the noise dropped.
        SeedBranch("ff", "main", "ff");
        var (ffOut, _, ffCode) = _h.Run(_h.Repo, "git", "merge", "ff");
        Assert.Equal(0, ffCode);
        Assert.Contains("Fast-forward", ffOut);
        Assert.Contains("3 files changed, 3 insertions(+)", ffOut);
        Assert.DoesNotContain("ff1.txt", ffOut);
        Assert.DoesNotContain("create mode", ffOut);
        SmokeAssert.NoOk(ffOut); // tiny output: the #52 savings bar withholds the OK line

        // Non-ff (ort): same shape with the strategy line kept.
        SeedBranch("topic", "HEAD~1", "topic");
        var (ortOut, _, ortCode) = _h.Run(_h.Repo, "git", "merge", "topic");
        Assert.Equal(0, ortCode);
        Assert.Contains("Merge made by the 'ort' strategy.", ortOut);
        Assert.Contains("3 files changed, 3 insertions(+)", ortOut);
        Assert.DoesNotContain("topic1.txt", ortOut);
        Assert.DoesNotContain("create mode", ortOut);
        SmokeAssert.NoOk(ortOut);

        // The merge itself happened (semantics untouched): both branches are ancestors of main.
        SmokeHarness.Git(_h.Repo, "merge-base", "--is-ancestor", "ff", "main");
        SmokeHarness.Git(_h.Repo, "merge-base", "--is-ancestor", "topic", "main");
        Assert.True(File.Exists(Path.Combine(_h.Repo, "topic3.txt")));

        var log = _h.InvocationLog();
        Assert.Equal(2, CountOccurrences(log, "\"reason\":\"git merge\""));
        Assert.Equal(2, CountOccurrences(log, "\"filtered\":true"));
        Assert.DoesNotContain("\"reason\":\"no-filter\"", log);
        Assert.DoesNotContain("ff1.txt", log); // invariant 3

        var (gaps, _, gapsCode) = _h.Run(_h.Repo, "gaps");
        Assert.Equal(0, gapsCode);
        Assert.DoesNotContain("git merge", SmokeAssert.GapTable(gaps));
    }

    [Fact]
    public void GitMergeConflictExit1StaysRawWithParity()
    {
        // Diverge file1.txt on both sides so the merge conflicts.
        SmokeHarness.Git(_h.Repo, "checkout", "-q", "-b", "clash");
        File.WriteAllText(Path.Combine(_h.Repo, "file1.txt"), "clash side\n");
        SmokeHarness.Git(_h.Repo, "commit", "-q", "-am", "clash side");
        SmokeHarness.Git(_h.Repo, "checkout", "-q", "main");
        File.WriteAllText(Path.Combine(_h.Repo, "file1.txt"), "main side\n");
        SmokeHarness.Git(_h.Repo, "commit", "-q", "-am", "main side");

        var raw = RawGit(_h.Repo, "merge", "clash");
        Assert.Equal(1, raw.code);
        Assert.Contains("CONFLICT (content): Merge conflict in file1.txt", raw.stdout);
        SmokeHarness.Git(_h.Repo, "merge", "--abort");

        var (outp, err, code) = _h.Run(_h.Repo, "git", "merge", "clash");
        Assert.Equal(raw.code, code); // parity: conflict exit 1 is outside the allowlist
        Assert.Equal(raw.stdout + raw.stderr, outp + err); // conflict report survives unaltered (invariant 2)
        SmokeAssert.NoOk(outp + err);
        SmokeHarness.Git(_h.Repo, "merge", "--abort");

        // Unknown ref: git exits 1 with a stderr message; parity again.
        var rawRef = RawGit(_h.Repo, "merge", "no-such-ref-167");
        Assert.NotEqual(0, rawRef.code);
        var (refOut, refErr, refCode) = _h.Run(_h.Repo, "git", "merge", "no-such-ref-167");
        Assert.Equal(rawRef.code, refCode);
        Assert.Equal(rawRef.stdout + rawRef.stderr, refOut + refErr);

        var log = _h.InvocationLog();
        Assert.Equal(2, CountOccurrences(log, "\"reason\":\"nonzero-exit\""));
        Assert.DoesNotContain("\"filtered\":true", log);
    }

    private static int CountOccurrences(string s, string sub)
    {
        var n = 0;
        for (var i = s.IndexOf(sub, StringComparison.Ordinal); i >= 0; i = s.IndexOf(sub, i + sub.Length, StringComparison.Ordinal))
            n++;
        return n;
    }
}
