using Xunit;

namespace Vtk.Tests;

/// <summary>
/// Subcommand-reservation routing for the help spellings (#118):
/// `help`/`--help`/`-h` must resolve in-process with the usage text (exit 0),
/// never fall through to exec passthrough (`--help` spawn-failed as an
/// external command; bare `help` reached Windows cmd's HELP). Companion to
/// VersionTests' reserved-spelling cases (#98).
/// </summary>
public class HelpTests
{
    [Theory]
    [InlineData("help")]
    [InlineData("--help")]
    [InlineData("-h")]
    public void Run_HelpSpellings_ResolveInProcessExitZero(string spelling)
    {
        Assert.Equal(0, Vtk.Cli.Program.Run(new[] { spelling }));
    }

    [Fact]
    public void Run_NoArgs_UsageErrorExitTwo()
    {
        Assert.Equal(2, Vtk.Cli.Program.Run(System.Array.Empty<string>()));
    }
}
