using Vtk.Core.Filter;
using Vtk.Core.Filter.Toml;
using Xunit;

namespace Vtk.Tests.Filter;

/// <summary>
/// Golden-fixture tests for the embedded dotnet TOML strip def (#121): real
/// captured `dotnet build` / `dotnet test` output in → compacted out, with a
/// measured savings assertion. Fixtures were captured from vtk's own solution
/// (dotnet 9 SDK, VSTest 17.14.1, output redirected — the same pipe capture
/// vtk uses, so MSBuild's classic console logger) and keep their original
/// CRLF line endings byte-exact (testdata is `-text` in .gitattributes).
/// </summary>
public class DotnetTomlTests
{
    private static readonly string FixtureDir = Path.Combine(
        AppContext.BaseDirectory, "Filter", "testdata", "dotnet");

    private static Compiled Dotnet() =>
        TomlFilter.Load().Single(c => c.Name == "dotnet");

    // build: clean `dotnet build` (restore + compile chatter → summary only).
    // build-warnings: solution build with analyzer warnings — MSBuild prints
    // each warning twice (inline + summary block) and both copies are kept,
    // so measured savings are small; the case pins warnings surviving
    // verbatim. test: green `dotnet test` (banner + chatter → Passed! line).
    [Theory]
    [InlineData("build", 0.50)]
    [InlineData("build-warnings", 0.03)]
    [InlineData("test", 0.80)]
    public void MatchesGoldenOutput(string name, double minSavings)
    {
        var raw = File.ReadAllText(Path.Combine(FixtureDir, name + ".raw.txt"));
        var want = File.ReadAllText(Path.Combine(FixtureDir, name + ".want.txt"));
        var got = Dotnet().Fn(raw);
        Assert.Equal(want, got);
        var savings = 1 - (double)got.Length / raw.Length;
        Assert.True(savings >= minSavings, $"savings {savings:P0} below minimum {minSavings:P0}");
    }

    /// <summary>Warnings survive filtering verbatim — they are signal, not chatter.</summary>
    [Fact]
    public void Warnings_KeptVerbatim()
    {
        var raw = File.ReadAllText(Path.Combine(FixtureDir, "build-warnings.raw.txt"));
        var got = Dotnet().Fn(raw);
        Assert.Contains("warning xUnit1031", got);
    }

    /// <summary>
    /// The def rides the default registry's regex path under its own name:
    /// build/test dispatch, other dotnet verbs stay unfiltered, and the
    /// exit-code allowlist is {0} (compile errors and failing tests pass
    /// through raw — exit-code parity is untouched either way).
    /// </summary>
    [Fact]
    public void Default_Registry_DispatchesBuildAndTestOnly()
    {
        var r = Registry.Default();

        Assert.True(r.TryLookup(new[] { "dotnet", "build", "dotnet/Vtk.sln" }, out var entry),
            "embedded dotnet TOML filter not registered");
        Assert.Equal("dotnet", entry.Name);
        Assert.True(entry.Filters(0) && !entry.Filters(1),
            "dotnet should filter exit 0 only (failures stay raw)");
        Assert.True(r.TryLookup(new[] { "dotnet", "test", "dotnet/Vtk.Tests" }, out _));

        Assert.False(r.TryLookup(new[] { "dotnet", "run" }, out _),
            "other dotnet verbs must not dispatch the filter");
        Assert.False(r.TryLookup(new[] { "dotnet", "--version" }, out _));
        Assert.False(r.TryLookup(new[] { "dotnet", "buildx" }, out _),
            @"\b boundary: 'buildx' must not match");
    }
}
