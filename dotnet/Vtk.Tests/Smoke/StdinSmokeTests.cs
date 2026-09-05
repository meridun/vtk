using Xunit;

namespace Vtk.Tests.Smoke;

/// <summary>
/// #144: vtk must hand the wrapped command the same stdin the shell handed
/// vtk. Before the fix ProcessRunner redirected the child's stdin to a pipe
/// and closed it at once, so `printf msg | vtk git commit -F -` aborted with
/// "empty commit message" and `echo hello | vtk cat` printed nothing. Runs
/// the real published binary on both runner paths: a registry-matched
/// command (`git commit`, RunCaptured) and an unmatched one (fake `cat`,
/// RunPassthroughCounted non-TTY).
/// </summary>
[Collection("Smoke")]
public class StdinSmokeTests : IDisposable
{
    private readonly SmokeHarness _h = new();
    public void Dispose() => _h.Dispose();

    [Fact]
    public void PassthroughPath_PipedStdinReachesChildByteForByte()
    {
        // Multi-line, non-ASCII, no trailing newline: nothing may be added,
        // dropped, or re-encoded on the way through.
        const string payload = "hello via stdin\nsecond line ✔\nno trailing newline";

        var (stdout, stderr, code) = _h.RunStdin(_h.Repo, payload, faked: true, "cat");

        Assert.True(code == 0, $"exit {code}, want 0; stderr: {stderr}");
        Assert.Equal(payload, stdout);
    }

    [Fact]
    public void CapturedPath_GitCommitReadsMessageFromStdin()
    {
        File.WriteAllText(Path.Combine(_h.Repo, "stdin.txt"), "committed with -F -\n");
        SmokeHarness.Git(_h.Repo, "add", "stdin.txt");

        var (stdout, stderr, code) = _h.RunStdin(_h.Repo, "msg via stdin\n\nbody line\n", faked: false,
            "git", "commit", "-q", "-F", "-");

        // The exact regression: exit 1 "Aborting commit due to empty commit message".
        Assert.True(code == 0, $"exit {code}, want 0 (parity with `command git`); stdout: {stdout}; stderr: {stderr}");
        Assert.DoesNotContain("empty commit message", stdout + stderr);
        Assert.Equal("msg via stdin", SmokeHarness.Git(_h.Repo, "log", "-1", "--format=%s").Trim());
        Assert.Equal("body line", SmokeHarness.Git(_h.Repo, "log", "-1", "--format=%b").Trim());
    }

    [Fact]
    public async Task ChildThatNeverReadsStdin_ExitsCleanlyWithOutputIntact()
    {
        // Early-close shape: the child exits without draining a pending
        // stdin payload. vtk must neither hang nor fail the run — it owns no
        // stdin thread; the handle is the child's.
        var payload = new string('x', 64 * 1024);
        var head = SmokeHarness.Git(_h.Repo, "rev-parse", "HEAD").Trim();

        var run = Task.Run(() => _h.RunStdin(_h.Repo, payload, faked: false, "git", "rev-parse", "HEAD"));
        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(60)));
        Assert.True(finished == run, "vtk hung with an undrained stdin pipe");

        var (stdout, stderr, code) = await run;
        Assert.True(code == 0, $"exit {code}, want 0; stderr: {stderr}");
        Assert.Equal(head, stdout.Trim());
    }
}
