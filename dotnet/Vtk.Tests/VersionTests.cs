using Xunit;

namespace Vtk.Tests;

/// <summary>
/// Table-driven tests for `vtk version` (#98): the pure Describe core over its
/// two SHA sources (publish-time informational-version stamp, then the
/// vtk-releases release-dir name), plus subcommand-reservation routing —
/// `version`/`--version` must resolve in-process (exit 0/2), never fall
/// through to exec passthrough.
/// </summary>
public class VersionTests
{
    public static IEnumerable<object?[]> DescribeCases()
    {
        // informationalVersion, deployDir, expected, description
        yield return new object?[] { "1.0.0+abc1234", null, "vtk abc1234", "publish stamp" };
        yield return new object?[] { "1.0.0+abc1234", @"C:\Users\x\tools\vtk-releases\def5678", "vtk abc1234", "stamp wins over dir name" };
        yield return new object?[] { "1.0.0", @"C:\Users\x\tools\vtk-releases\def5678", "vtk def5678", "unstamped: release-dir fallback" };
        yield return new object?[] { null, @"C:\Users\x\tools\vtk-releases\def5678\", "vtk def5678", "trailing separator trimmed" };
        yield return new object?[] { "1.0.0", @"C:\Users\x\tools\vtk-releases\legacy", "vtk legacy", "pre-versioning migrated dir reports as legacy" };
        yield return new object?[] { "1.0.0", @"C:\Claude\vtk\dotnet\Vtk.Cli\bin\Release\net9.0", "vtk unknown (unstamped build; not a versioned deploy)", "dev build tree" };
        yield return new object?[] { "1.0.0+", @"C:\dev\build", "vtk unknown (unstamped build; not a versioned deploy)", "empty suffix after + is not a sha" };
        yield return new object?[] { null, null, "vtk unknown (unstamped build; not a versioned deploy)", "no source at all" };
    }

    [Theory]
    [MemberData(nameof(DescribeCases))]
    public void Describe_ResolvesShaByPrecedence(string? informationalVersion, string? deployDir, string expected, string because)
    {
        Assert.Equal(expected, Vtk.Cli.VersionCmd.Describe(informationalVersion, deployDir));
        _ = because;
    }

    [Theory]
    [InlineData("version")]
    [InlineData("--version")]
    public void Run_ReservedSpellings_ExitZero(string spelling)
    {
        Assert.Equal(0, Vtk.Cli.Program.Run(new[] { spelling }));
    }

    [Fact]
    public void Run_UnexpectedArgument_ExitTwo()
    {
        Assert.Equal(2, Vtk.Cli.Program.Run(new[] { "version", "extra" }));
    }
}
