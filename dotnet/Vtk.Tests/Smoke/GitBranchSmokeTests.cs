using System.Diagnostics;
using Xunit;

namespace Vtk.Tests.Smoke;

/// <summary>
/// End-to-end smoke for the #166 `git branch` reshape through the real
/// published vtk binary and real git: in a clone with an `origin` remote,
/// `git branch -a` comes out one entry per line with `remotes/` stripped and
/// the local + `origin/&lt;same&gt;` twin collapsed to one `(tracked)` entry,
/// while the guarded shapes (`--show-current`, `-vv`, `--format`) stay
/// byte-identical to bare git. Small listings clear no savings bar, so no
/// `OK &lt;id&gt;` (#52). Exit-code parity is asserted on the success path, the
/// usage-error path (129), the missing-branch path (1), and a concurrent pair.
/// </summary>
[Collection("Smoke")]
public class GitBranchSmokeTests : IDisposable
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

    /// <summary>
    /// Clones the harness repo into <see cref="SmokeHarness.Other"/> so it has
    /// an `origin` with a tracked twin (`main`), a local-only branch (`topic`),
    /// and a remote-only branch (`origin/remote-only`).
    /// </summary>
    private string CloneWithOrigin()
    {
        SmokeHarness.Git(_h.Repo, "branch", "remote-only");
        var clone = Path.Combine(_h.Other, "clone");
        SmokeHarness.Git(_h.Other, "clone", "-q", _h.Repo, clone);
        SmokeHarness.Git(clone, "branch", "topic");
        return clone;
    }

    [Fact]
    public void BranchAllReshapesWithTrackedTwinsAndNoOk()
    {
        var clone = CloneWithOrigin();
        var raw = SmokeHarness.Git(clone, "branch", "-a");
        Assert.Contains("  remotes/origin/main\n", raw);

        var (outp, err, code) = _h.Run(clone, "git", "branch", "-a");
        Assert.Equal(0, code);
        Assert.Equal(
            "* main (tracked)\ntopic\norigin/HEAD -> origin/main\norigin/remote-only",
            outp.TrimEnd('\r', '\n'));
        SmokeAssert.NoOk(outp);
        Assert.DoesNotContain("remotes/", outp);
        Assert.True(outp.Length < raw.Length, $"reshape ({outp.Length}) not smaller than raw ({raw.Length}); stderr: {err}");

        // -r: git already omits remotes/, so the reshape is one-per-line only.
        var (rOut, _, rCode) = _h.Run(clone, "git", "branch", "-r");
        Assert.Equal(0, rCode);
        Assert.Equal("origin/HEAD -> origin/main\norigin/main\norigin/remote-only", rOut.TrimEnd('\r', '\n'));
        SmokeAssert.NoOk(rOut);

        // Plain listing: markers kept, no (tracked) annotation without -a.
        var (lOut, _, lCode) = _h.Run(clone, "git", "branch");
        Assert.Equal(0, lCode);
        Assert.Equal("* main\ntopic", lOut.TrimEnd('\r', '\n'));
        SmokeAssert.NoOk(lOut);

        var log = _h.InvocationLog();
        Assert.Contains("\"reason\":\"git branch\"", log);
        // Invariant 3: the log carries counts, never branch names.
        Assert.DoesNotContain("remote-only", log);
    }

    [Theory]
    [InlineData("--show-current")]
    [InlineData("-vv")]
    [InlineData("--format=%(refname:short) %(upstream:short)")]
    public void NonListingShapesAreByteIdenticalPassthrough(string flag)
    {
        var clone = CloneWithOrigin();
        var raw = SmokeHarness.Git(clone, "branch", flag);
        Assert.NotEqual("", raw.Trim());
        var (outp, _, code) = _h.Run(clone, "git", "branch", flag);
        Assert.Equal(0, code);
        Assert.Equal(raw, outp);
        SmokeAssert.NoOk(outp);
    }

    [Fact]
    public void ExitCodeParityOnBranchFailurePaths()
    {
        // Unknown option: usage error (129) with the raw usage text.
        var rawUsage = RawGit(_h.Repo, "branch", "--no-such-flag");
        Assert.Equal(129, rawUsage.code);
        var (uOut, uErr, uCode) = _h.Run(_h.Repo, "git", "branch", "--no-such-flag");
        Assert.Equal(rawUsage.code, uCode);
        Assert.Equal(rawUsage.stdout + rawUsage.stderr, uOut + uErr);

        // Deleting a missing branch: exit 1 with the raw error.
        var rawDel = RawGit(_h.Repo, "branch", "-d", "does-not-exist");
        Assert.Equal(1, rawDel.code);
        var (dOut, dErr, dCode) = _h.Run(_h.Repo, "git", "branch", "-d", "does-not-exist");
        Assert.Equal(rawDel.code, dCode);
        Assert.Equal(rawDel.stdout + rawDel.stderr, dOut + dErr);
    }

    [Fact]
    public async Task ConcurrentBranchAllInvocationsAgreeAndKeepParity()
    {
        var clone = CloneWithOrigin();
        var t1 = Task.Run(() => _h.Run(clone, "git", "branch", "-a"));
        var t2 = Task.Run(() => _h.Run(clone, "git", "branch", "-a"));
        var results = await Task.WhenAll(t1, t2);
        Assert.Equal(0, results[0].code);
        Assert.Equal(0, results[1].code);
        Assert.Equal(results[0].stdout, results[1].stdout);
        Assert.Contains("* main (tracked)", results[0].stdout);
        SmokeAssert.NoOk(results[0].stdout);
        SmokeAssert.NoOk(results[1].stdout);
    }
}
