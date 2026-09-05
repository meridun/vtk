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

    [Fact]
    public void InteractiveConsoleStdinUnderCapture_ChildSeesEofAndDoesNotHang()
    {
        if (!OperatingSystem.IsWindows())
            return; // ConPTY is Windows-only

        // The one cell that keeps the pre-#144 redirect-and-close: vtk's
        // stdin is a real (pseudo) console but its stdout is redirected, so
        // the child runs captured/counted and the user could never see a
        // prompt. The ConPTY input pipe is held open and never written, so
        // a child that inherited console stdin and read it (`cat`) would
        // block until the harness timeout; with the close it sees EOF at
        // once. `cat` covers RunPassthroughCounted(tty:false), `git status`
        // covers RunCaptured.
        File.WriteAllText(Path.Combine(_h.Repo, "file1.txt"), "line 1 content\ndirty\n");
        var catOut = Path.Combine(_h.Home, "cat-out.txt");
        var statusOut = Path.Combine(_h.Home, "status-out.txt");
        var script = Path.Combine(_h.Home, "stdin-console.cmd");
        File.WriteAllText(script,
            "@echo off\r\n" +
            $"\"{_h.Bin}\" cat > \"{catOut}\"\r\n" +
            "echo CAT_EXIT=%ERRORLEVEL%\r\n" +
            $"\"{_h.Bin}\" git status > \"{statusOut}\"\r\n" +
            "echo STATUS_EXIT=%ERRORLEVEL%\r\n");

        var env = new Dictionary<string, string>
        {
            ["LOCALAPPDATA"] = _h.Home,
            ["XDG_CACHE_HOME"] = _h.Home,
            ["HOME"] = _h.Home,
            ["TERM"] = "dumb",
            ["PATH"] = _h.FakeBinDir + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
        };
        string output;
        try
        {
            (output, _) = ConPty.Run($"cmd.exe /d /c \"{script}\"", _h.Repo, env, TimeSpan.FromSeconds(60));
        }
        catch (TimeoutException ex)
        {
            Assert.Fail($"vtk (or its child) hung on console stdin under capture: {ex.Message}");
            return;
        }

        Assert.Contains("CAT_EXIT=0", output);
        Assert.Contains("STATUS_EXIT=0", output);
        // cat saw EOF immediately: nothing forwarded from the console.
        Assert.Equal("", File.ReadAllText(catOut));
        // git status took the captured/filtered path (not tty bypass): the
        // compact form, not the raw "On branch ..." prose.
        var status = File.ReadAllText(statusOut);
        Assert.Contains("file1.txt", status);
        Assert.DoesNotContain("On branch", status);
        var log = _h.InvocationLog();
        Assert.Contains("\"filtered\":true", log);
        Assert.DoesNotContain("\"tty\":true", log);
    }
}
