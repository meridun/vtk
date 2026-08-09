using Vtk.Core.Runner;
using Xunit;

namespace Vtk.Tests.Runner;

/// <summary>
/// Pins the cmd.exe command-line construction for .cmd/.bat scripts. The
/// regression these guard: a spaced script path plus a multi-word arg used to
/// be handed to cmd /c as >2 quoted tokens, and cmd stripped the outer pair,
/// splitting "C:\Program Files\...\npm.cmd" into an unrunnable `C:\Program`.
/// </summary>
public class ProcessRunnerTests
{
    [Fact]
    public void BuildCmdArguments_SpacedPathAndMultiWordArg_WrapsInSacrificialOuterQuotes()
    {
        var argv = new[]
        {
            @"C:\Program Files\nodejs\npm.cmd", "run", "sdlc", "--", "dup-check", "some multi word title",
        };

        var got = ProcessRunner.BuildCmdArguments(argv[0], argv);

        // /s + a single outer quote pair; each spaced token individually quoted.
        Assert.Equal(
            "/s /c \"\"C:\\Program Files\\nodejs\\npm.cmd\" run sdlc -- dup-check \"some multi word title\"\"",
            got);

        // Sacrificial pair: stripping the first and last char (what cmd /s does)
        // must leave a line whose own quoting keeps the spaced path intact.
        var stripped = got.Substring("/s /c ".Length);
        Assert.Equal('"', stripped[0]);
        Assert.Equal('"', stripped[^1]);
        var inner = stripped[1..^1];
        Assert.StartsWith("\"C:\\Program Files\\nodejs\\npm.cmd\"", inner);
    }

    [Fact]
    public void RunCaptured_LargeStderrBeforeStdoutCloses_DoesNotDeadlock()
    {
        // Regression for #94: sequential ReadToEnd(stdout) -> ReadToEnd(stderr)
        // deadlocked once the child filled the ~4KB stderr pipe buffer while
        // stdout was still open (rolldown-vite's ~10KB of stderr warnings wedged
        // every captured `npm run e2e:local`). The .cmd child mirrors the real
        // npm.cmd launch path (cmd.exe /s /c routing).
        var script = Path.Combine(Path.GetTempPath(), $"vtk-noisy-{Guid.NewGuid():N}.cmd");
        File.WriteAllText(script,
            "@echo off\r\n" +
            "for /L %%i in (1,1,4000) do echo stderr-filler-line-%%i-xxxxxxxxxxxxxxxxxxxxxxxx 1>&2\r\n" +
            "echo STDOUT DONE\r\n");
        try
        {
            var run = Task.Run(() => ProcessRunner.RunCaptured(new[] { script }));
            Assert.True(run.Wait(TimeSpan.FromSeconds(60)), "RunCaptured deadlocked on large stderr");

            var result = run.Result;
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("STDOUT DONE", result.Stdout);
            Assert.True(result.Stderr.Length > 64 * 1024, $"expected >64KB stderr, got {result.Stderr.Length}");
        }
        finally
        {
            File.Delete(script);
        }
    }

    [Fact]
    public void RunCaptured_NonexistentCommand_Synthesizes127AndFlagsSpawnFail()
    {
        var result = ProcessRunner.RunCaptured(new[] { "vtk-definitely-not-a-command-xyz" });

        Assert.Equal(127, result.ExitCode);
        Assert.True(result.SpawnFailed, "spawn failure must be flagged so callers log it as spawn-fail, not a coverage gap (#118)");
        Assert.Equal("", result.Combined);
    }

    [Fact]
    public void RunPassthroughCounted_NonexistentCommand_Synthesizes127AndFlagsSpawnFail()
    {
        var code = ProcessRunner.RunPassthroughCounted(
            new[] { "vtk-definitely-not-a-command-xyz" }, tty: false, out var bytes, out var spawnFailed);

        Assert.Equal(127, code);
        Assert.True(spawnFailed, "spawn failure must be flagged so callers log it as spawn-fail, not a coverage gap (#118)");
        Assert.Equal(0, bytes);
    }

    [Theory]
    [InlineData("run", "run")]                        // bare token: unchanged
    [InlineData("dup-check", "dup-check")]             // hyphen is not special
    [InlineData("multi word", "\"multi word\"")]       // space: quoted
    [InlineData("a&b", "\"a&b\"")]                     // cmd metachar: quoted
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]     // embedded quote: escaped
    [InlineData("", "\"\"")]                            // empty: explicit empty arg
    public void QuoteForCmd_QuotesOnlyWhenNeeded(string arg, string want)
    {
        Assert.Equal(want, ProcessRunner.QuoteForCmd(arg));
    }
}
