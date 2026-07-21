using Vtk.Core.Filter;
using Vtk.Core.Filter.Toml;
using Xunit;

namespace Vtk.Tests.Filter;

/// <summary>
/// Golden-fixture tests for the embedded winget TOML strip def (#105): real
/// captured `winget install` output in → compacted out, with a measured
/// savings assertion. Fixtures were captured from
/// `winget install/uninstall --id jqlang.jq` (winget v1.29.280, output
/// redirected — the same pipe capture vtk uses) and keep their original CRLF
/// line endings byte-exact (testdata is `-text` in .gitattributes).
/// </summary>
public class WingetTomlTests
{
    private static readonly string FixtureDir = Path.Combine(
        AppContext.BaseDirectory, "Filter", "testdata", "winget");

    private static Compiled Winget() =>
        TomlFilter.Load().Single(c => c.Name == "winget");

    [Theory]
    [InlineData("install", 0.55)]
    public void MatchesGoldenOutput(string name, double minSavings)
    {
        var raw = File.ReadAllText(Path.Combine(FixtureDir, name + ".raw.txt"));
        var want = File.ReadAllText(Path.Combine(FixtureDir, name + ".want.txt"));
        var got = Winget().Fn(raw);
        Assert.Equal(want, got);
        var savings = 1 - (double)got.Length / raw.Length;
        Assert.True(savings >= minSavings, $"savings {savings:P0} below minimum {minSavings:P0}");
    }

    /// <summary>
    /// The def rides the default registry's regex path under its own name:
    /// the spam-bearing verbs dispatch, read-only verbs stay unfiltered, and
    /// the exit-code allowlist is {0} (failed installs pass through raw —
    /// exit-code parity is untouched either way).
    /// </summary>
    [Fact]
    public void Default_Registry_DispatchesSpamVerbsOnly()
    {
        var r = Registry.Default();

        Assert.True(r.TryLookup(new[] { "winget", "install", "--id", "jqlang.jq" }, out var entry),
            "embedded winget TOML filter not registered");
        Assert.Equal("winget", entry.Name);
        Assert.True(entry.Filters(0) && !entry.Filters(1),
            "winget should filter exit 0 only (failures stay raw)");
        Assert.True(r.TryLookup(new[] { "winget", "upgrade", "--all" }, out _));

        Assert.False(r.TryLookup(new[] { "winget", "list" }, out _),
            "read-only winget verbs must not dispatch the filter");
        Assert.False(r.TryLookup(new[] { "winget", "search", "jq" }, out _));
    }
}
