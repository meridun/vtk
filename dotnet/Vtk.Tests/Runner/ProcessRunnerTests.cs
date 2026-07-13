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
