using System.Diagnostics;
using Xunit;

namespace Vtk.Tests.Smoke;

/// <summary>
/// End-to-end smoke for the #135 hunk-fold floor through the real published
/// vtk binary and real git: hunk-shaped `git diff` / `git show` output below
/// <c>Fold.FloorBytes</c> (64 KiB) is byte-identical passthrough logged
/// <c>filtered=true</c> under the entry name (never a gap row); at or above
/// the floor the per-file stats shape plus `OK &lt;id&gt;` stands and
/// `vtk show` recovers the hunks. `--stat` and header-only `show -s` keep
/// their pre-#135 behavior. Exit-code parity is asserted on the failure path
/// (128) and the concurrent above-floor path; the invocation log never
/// carries hunk content (invariant 3).
/// </summary>
[Collection("Smoke")]
public class GitHunkFloorSmokeTests : IDisposable
{
    private const int FloorBytes = 64 * 1024;
    private const string Marker = "hunk-marker-7f3a";

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

    /// <summary>A small tracked change: a few hunk lines, far below the floor.</summary>
    private void SmallChange() =>
        File.WriteAllText(Path.Combine(_h.Repo, "file1.txt"), $"line 1 content\n{Marker} one\n{Marker} two\n");

    /// <summary>A tracked change whose unified diff clears the 64 KiB floor with margin.</summary>
    private void BigChange()
    {
        var lines = new List<string> { "line 2 content" };
        for (var i = 0; i < 900; i++)
            lines.Add($"{Marker} padding line {i}: extra tracked content to grow the raw diff past the fold floor");
        File.WriteAllText(Path.Combine(_h.Repo, "file2.txt"), string.Join('\n', lines) + "\n");
    }

    [Fact]
    public void DiffBelowFloorIsByteIdenticalPassthroughLoggedUnderTheEntry()
    {
        SmallChange();
        var raw = RawGit(_h.Repo, "diff");
        Assert.Equal(0, raw.code);
        Assert.Contains("@@", raw.stdout);
        Assert.True(raw.stdout.Length < FloorBytes, $"scratch diff unexpectedly at/above the floor ({raw.stdout.Length})");

        // Per-stream identity: stdout hunks and any git stderr (e.g. a CRLF
        // working-copy warning on Windows) both arrive unchanged.
        var (outp, err, code) = _h.Run(_h.Repo, "git", "diff");
        Assert.Equal(0, code);
        Assert.Equal(raw.stdout, outp);
        Assert.Equal(raw.stderr, err);
        SmokeAssert.NoOk(outp);

        // --stat has no hunks: passthrough as before, regardless of floor.
        var rawStat = SmokeHarness.Git(_h.Repo, "diff", "--stat");
        var (statOut, _, statCode) = _h.Run(_h.Repo, "git", "diff", "--stat");
        Assert.Equal(0, statCode);
        Assert.Equal(rawStat, statOut);
        SmokeAssert.NoOk(statOut);

        var log = _h.InvocationLog();
        Assert.Equal(2, CountOccurrences(log, "\"reason\":\"git diff\""));
        Assert.Equal(2, CountOccurrences(log, "\"filtered\":true"));
        Assert.DoesNotContain("\"reason\":\"no-filter\"", log);
        Assert.DoesNotContain(Marker, log); // invariant 3
        Assert.False(Directory.Exists(_h.SpoolDir) && Directory.GetFiles(_h.SpoolDir).Length > 0, "below-floor passthrough must not spool");
    }

    [Fact]
    public void DiffAtOrAboveFloorFoldsToStatsWithRecoverableSpool()
    {
        BigChange();
        var raw = SmokeHarness.Git(_h.Repo, "diff");
        Assert.True(raw.Length >= FloorBytes, $"scratch diff below the floor ({raw.Length})");

        var (outp, err, code) = _h.Run(_h.Repo, "git", "diff");
        Assert.Equal(0, code);
        var id = SmokeHarness.MustOkId(outp);
        Assert.Contains("file2.txt | +900 -0", outp);
        Assert.DoesNotContain("@@", outp);
        Assert.DoesNotContain(Marker, outp);
        Assert.True(outp.Length < raw.Length / 100, $"fold ({outp.Length}) not >= 99% smaller than raw ({raw.Length}); stderr: {err}");

        var (shown, _, showCode) = _h.Run(_h.Repo, "show", id);
        Assert.Equal(0, showCode);
        Assert.Contains("# cmd: git diff", shown);
        Assert.Contains("@@", shown);
        Assert.Contains(Marker + " padding line 899", shown);

        var log = _h.InvocationLog();
        Assert.Contains("\"reason\":\"git diff\"", log);
        Assert.DoesNotContain("\"reason\":\"no-filter\"", log);
        Assert.DoesNotContain(Marker, log); // invariant 3
    }

    [Fact]
    public void ShowFloorsHunksButStillCompactsHeaderOnlyShapes()
    {
        // Below the floor: the harness's small commits pass through byte-identical.
        var rawSmall = SmokeHarness.Git(_h.Repo, "show", "HEAD");
        Assert.Contains("@@", rawSmall);
        var (smallOut, _, smallCode) = _h.Run(_h.Repo, "git", "show", "HEAD");
        Assert.Equal(0, smallCode);
        Assert.Equal(rawSmall, smallOut);
        SmokeAssert.NoOk(smallOut);

        // At or above the floor: `hash subject` + stats + OK, hunks recoverable.
        BigChange();
        SmokeHarness.Git(_h.Repo, "add", ".");
        SmokeHarness.Git(_h.Repo, "commit", "-q", "-m", "commit 4: grow file2.txt");
        var head = SmokeHarness.Git(_h.Repo, "rev-parse", "HEAD").Trim();
        var rawBig = SmokeHarness.Git(_h.Repo, "show", "HEAD");
        Assert.True(rawBig.Length >= FloorBytes, $"scratch show below the floor ({rawBig.Length})");
        var (bigOut, _, bigCode) = _h.Run(_h.Repo, "git", "show", "HEAD");
        Assert.Equal(0, bigCode);
        var id = SmokeHarness.MustOkId(bigOut);
        Assert.Contains(head[..7] + " commit 4: grow file2.txt", bigOut);
        Assert.Contains("file2.txt | +900 -0", bigOut);
        Assert.DoesNotContain("@@", bigOut);
        var (shown, _, showCode) = _h.Run(_h.Repo, "show", id);
        Assert.Equal(0, showCode);
        Assert.Contains(Marker + " padding line 899", shown);

        // Header-only (-s): no hunks, so the pre-#135 compaction stands.
        // Output is tiny, so the #52 savings bar withholds the OK line.
        var (sOut, _, sCode) = _h.Run(_h.Repo, "git", "show", "-s", "HEAD");
        Assert.Equal(0, sCode);
        Assert.Equal(head[..7] + " commit 4: grow file2.txt", sOut.Trim());
        SmokeAssert.NoOk(sOut);

        var log = _h.InvocationLog();
        Assert.Equal(3, CountOccurrences(log, "\"reason\":\"git show\""));
        Assert.DoesNotContain("\"reason\":\"no-filter\"", log);
        Assert.DoesNotContain(Marker, log); // invariant 3
    }

    [Fact]
    public void ExitCodeParityOnFailingDiffAndShow()
    {
        var rawDiff = RawGit(_h.Repo, "diff", "no-such-rev-135");
        Assert.Equal(128, rawDiff.code);
        var (dOut, dErr, dCode) = _h.Run(_h.Repo, "git", "diff", "no-such-rev-135");
        Assert.Equal(rawDiff.code, dCode);
        Assert.Equal(rawDiff.stdout + rawDiff.stderr, dOut + dErr);

        var rawShow = RawGit(_h.Repo, "show", "no-such-rev-135");
        Assert.Equal(128, rawShow.code);
        var (sOut, sErr, sCode) = _h.Run(_h.Repo, "git", "show", "no-such-rev-135");
        Assert.Equal(rawShow.code, sCode);
        Assert.Equal(rawShow.stdout + rawShow.stderr, sOut + sErr);

        var log = _h.InvocationLog();
        Assert.Equal(2, CountOccurrences(log, "\"reason\":\"nonzero-exit\""));
        Assert.DoesNotContain("\"filtered\":true", log);
    }

    [Fact]
    public async Task ConcurrentAboveFloorDiffsKeepParityAndNeverLoseOutput()
    {
        BigChange();
        // Same argv, same spool target: the atomic-rename race must leave both
        // exits at 0; one may degrade to raw passthrough (invariant 2, #125),
        // so folding is asserted for at least one and completeness for the rest.
        var t1 = Task.Run(() => _h.Run(_h.Repo, "git", "diff"));
        var t2 = Task.Run(() => _h.Run(_h.Repo, "git", "diff"));
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
                Assert.Contains(Marker + " padding line 899", r.stdout); // raw passthrough is complete
        }
        Assert.True(folded >= 1, "neither concurrent invocation folded");
    }

    private static int CountOccurrences(string s, string sub)
    {
        var n = 0;
        for (var i = s.IndexOf(sub, StringComparison.Ordinal); i >= 0; i = s.IndexOf(sub, i + sub.Length, StringComparison.Ordinal))
            n++;
        return n;
    }
}
