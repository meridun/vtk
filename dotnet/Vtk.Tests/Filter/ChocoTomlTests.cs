using Vtk.Core.Filter;
using Vtk.Core.Filter.Toml;
using Xunit;

namespace Vtk.Tests.Filter;

/// <summary>
/// Golden-fixture tests for the embedded choco TOML strip def (#106): real
/// captured choco output in → compacted out, with a measured savings
/// assertion. Fixtures were captured from `choco outdated` and
/// `choco install jq -y --noop` (choco v2.3.0, output redirected — the same
/// pipe capture vtk uses; the noop run performs the real package download,
/// so the \r-frame progress spam is genuine) and keep their original
/// CRLF/CR bytes exact (testdata is `-text` in .gitattributes).
/// </summary>
public class ChocoTomlTests
{
    private static readonly string FixtureDir = Path.Combine(
        AppContext.BaseDirectory, "Filter", "testdata", "choco");

    private static Compiled Choco() =>
        TomlFilter.Load().Single(c => c.Name == "choco");

    [Theory]
    [InlineData("outdated", 0.15)]
    [InlineData("install-noop", 0.08)]
    public void MatchesGoldenOutput(string name, double minSavings)
    {
        var raw = File.ReadAllText(Path.Combine(FixtureDir, name + ".raw.txt"));
        var want = File.ReadAllText(Path.Combine(FixtureDir, name + ".want.txt"));
        var got = Choco().Fn(raw);
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

        Assert.True(r.TryLookup(new[] { "choco", "install", "jq", "-y" }, out var entry),
            "embedded choco TOML filter not registered");
        Assert.Equal("choco", entry.Name);
        Assert.True(entry.Filters(0) && !entry.Filters(1),
            "choco should filter exit 0 only (failures stay raw)");
        Assert.True(r.TryLookup(new[] { "choco", "upgrade", "all", "-y" }, out _));
        Assert.True(r.TryLookup(new[] { "choco", "outdated" }, out _));

        Assert.False(r.TryLookup(new[] { "choco", "search", "jq" }, out _),
            "read-only choco verbs must not dispatch the filter");
        Assert.False(r.TryLookup(new[] { "choco", "list" }, out _));
    }
}
