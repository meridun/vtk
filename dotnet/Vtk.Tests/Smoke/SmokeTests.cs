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

        // concurrent identical invocations both succeed
        var t1 = Task.Run(() => _h.Run(_h.Repo, "git", "log"));
        var t2 = Task.Run(() => _h.Run(_h.Repo, "git", "log"));
        Task.WaitAll(t1, t2);
        Assert.Equal(0, t1.Result.code);
        Assert.Equal(0, t2.Result.code);
        SmokeHarness.MustOkId(t1.Result.stdout);
        SmokeHarness.MustOkId(t2.Result.stdout);
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
