using System.Text.RegularExpressions;
using Xunit;

namespace Vtk.Tests.Smoke;

/// <summary>
/// Port of test/smoke/pty_smoke_test.go (TestSmokePTYBypass) using a Windows
/// pseudo console instead of a Unix PTY — the Go original was build-tagged
/// !windows; ConPTY is the equivalent "real terminal" on the platform this
/// suite runs on. Exercises the interactive TTY-bypass branch end to end
/// through the real binary: with a genuine console on stdout and a registry
/// match, vtk must pass through with ReasonTTYBypass. Asserts the #20 AC
/// triplet: (a) output is unfiltered/uncaptured (raw `git status`, no
/// "OK &lt;id&gt;" elision line), (b) the invocation is logged with reason
/// "tty-bypass", tty:true, filtered:false, and (c) exit-code parity.
/// </summary>
[Collection("Smoke")]
public class TtyBypassSmokeTests : IDisposable
{
    private readonly SmokeHarness _h = new();
    public void Dispose() => _h.Dispose();

    private static readonly Regex OkRe = new(@"OK [0-9a-f]{4}", RegexOptions.Compiled);

    [Fact]
    public void PseudoConsoleTakesTtyBypassWithParityAndBypassLog()
    {
        if (!OperatingSystem.IsWindows())
            return; // ConPTY is Windows-only; the TTY branch is covered by the Unix PTY test upstream of the C# port

        // Dirty the tree so a real `git status` has content to (not) filter.
        File.WriteAllText(Path.Combine(_h.Repo, "file1.txt"), "line 1 content\ndirty\n");
        File.WriteAllText(Path.Combine(_h.Repo, "newfile.txt"), "untracked\n");

        var env = new Dictionary<string, string>
        {
            ["LOCALAPPDATA"] = _h.Home,
            ["XDG_CACHE_HOME"] = _h.Home,
            ["HOME"] = _h.Home,
            // TERM=dumb keeps git deterministic under a terminal: no pager,
            // no color VT noise in the asserted output.
            ["TERM"] = "dumb",
        };
        var (output, code) = ConPty.Run($"\"{_h.Bin}\" git status", _h.Repo, env, TimeSpan.FromSeconds(60));

        // (c) exit-code parity: `git status` on a dirty repo exits 0.
        Assert.True(code == 0, $"exit {code}, want 0 (parity); output:\n{output}");

        // (a) unfiltered/uncaptured: the compact "OK <id>" line only appears
        // on the filtered path; the interactive bypass emits raw status.
        Assert.False(OkRe.IsMatch(output), $"tty-bypass emitted an OK <id> elision line (output was captured/filtered):\n{output}");
        Assert.Contains("file1.txt", output);
        Assert.Contains("newfile.txt", output);

        // (b) metadata: the invocation is logged as a tty bypass, not a gap.
        var log = _h.InvocationLog();
        Assert.Contains("\"reason\":\"tty-bypass\"", log);
        Assert.Contains("\"tty\":true", log);
        Assert.DoesNotContain("\"filtered\":true", log);

        // A tty bypass is covered-but-unfiltered, not a coverage gap.
        var (gaps, _, gapsCode) = _h.Run(_h.Repo, "gaps");
        Assert.Equal(0, gapsCode);
        Assert.DoesNotContain("git", gaps);
    }
}
