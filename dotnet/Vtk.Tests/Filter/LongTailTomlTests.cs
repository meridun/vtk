using Vtk.Core.Filter;
using Vtk.Core.Filter.Toml;
using Xunit;

namespace Vtk.Tests.Filter;

/// <summary>
/// Tests for the curated long-tail TOML strip defs (#41): gcc/g++, make,
/// terraform plan, shellcheck, yamllint, hadolint — each ported from rtk's
/// def with its fixtures adapted, plus a measured savings assertion and
/// registry dispatch/exit-code checks.
///
/// Fixture provenance: gcc and make golden files (testdata/longtail) are
/// real captures — `gcc -Wall -Wextra -c` (MinGW-W64 gcc 16.1.0) with a
/// three-deep include chain, and a recursive `mingw32-make` run with the
/// `mingw32-` prefix and host paths scrubbed — byte-exact (testdata is
/// `-text` in .gitattributes). terraform/shellcheck/yamllint/hadolint were
/// not installed on the capture host; their fixtures are rtk's shipped
/// fixtures adapted to vtk's engine semantics (inline in each def).
/// </summary>
public class LongTailTomlTests
{
    private static readonly string FixtureDir = Path.Combine(
        AppContext.BaseDirectory, "Filter", "testdata", "longtail");

    private static Compiled ByName(string name) =>
        TomlFilter.Load().Single(c => c.Name == name);

    [Theory]
    [InlineData("gcc", 0.10)]
    [InlineData("make", 0.10)]
    public void MatchesGoldenOutput(string name, double minSavings)
    {
        var raw = File.ReadAllText(Path.Combine(FixtureDir, name + ".raw.txt"));
        var want = File.ReadAllText(Path.Combine(FixtureDir, name + ".want.txt"));
        var got = ByName(name).Fn(raw);
        Assert.Equal(want, got);
        var savings = 1 - (double)got.Length / raw.Length;
        Assert.True(savings >= minSavings, $"savings {savings:P0} below minimum {minSavings:P0}");
    }

    /// <summary>
    /// Measured savings floor on each def's noisiest inline fixture (the
    /// EmbeddedFixtures_AllReproduce test already asserts exact outputs; this
    /// asserts the compaction is real). Linter floors are small by nature —
    /// their strip set is blank spacer lines; the max_lines cap does the
    /// heavy lifting on pathological outputs.
    /// </summary>
    [Theory]
    [InlineData("gcc.toml", "warning_include_chain", 0.05)]
    [InlineData("make.toml", "submake_chatter", 0.50)]
    [InlineData("terraform-plan.toml", "plan_with_changes", 0.40)]
    [InlineData("shellcheck.toml", "findings", 0.005)]
    [InlineData("yamllint.toml", "findings", 0.005)]
    [InlineData("hadolint.toml", "findings", 0.005)]
    public void InlineFixture_MeasuredSavings(string file, string group, double minSavings)
    {
        var spec = TomlFilter.Specs()[file];
        var fn = spec.Compile().Fn;
        var fx = spec.Tests[group][0];
        var got = fn(fx.Input);
        Assert.Equal(fx.Expected, got);
        var savings = 1 - (double)got.Length / fx.Input.Length;
        Assert.True(savings >= minSavings,
            $"{file} {group}: savings {savings:P1} below minimum {minSavings:P1}");
    }

    /// <summary>
    /// The six defs ride the default registry's regex path under their own
    /// names, with the exit-code allowlists from the plan: compilers/build
    /// runs filter exit 0 only (failures stay raw, cargo precedent); the
    /// report-style linters also filter their documented findings codes
    /// (decision #7).
    /// </summary>
    [Theory]
    [InlineData(new[] { "gcc", "-Wall", "-c", "main.c" }, "gcc", 0, true)]
    [InlineData(new[] { "gcc", "-Wall", "-c", "main.c" }, "gcc", 1, false)]
    [InlineData(new[] { "g++", "-O2", "app.cpp" }, "gcc", 0, true)]
    [InlineData(new[] { "make", "-j4", "all" }, "make", 0, true)]
    [InlineData(new[] { "make", "-j4", "all" }, "make", 2, false)]
    [InlineData(new[] { "terraform", "plan", "-out=tf.plan" }, "terraform-plan", 0, true)]
    [InlineData(new[] { "terraform", "plan" }, "terraform-plan", 2, false)]
    [InlineData(new[] { "shellcheck", "script.sh" }, "shellcheck", 1, true)]
    [InlineData(new[] { "shellcheck", "script.sh" }, "shellcheck", 2, false)]
    [InlineData(new[] { "yamllint", "config.yml" }, "yamllint", 1, true)]
    [InlineData(new[] { "yamllint", "--strict", "config.yml" }, "yamllint", 2, true)]
    [InlineData(new[] { "yamllint", "config.yml" }, "yamllint", 3, false)]
    [InlineData(new[] { "hadolint", "Dockerfile" }, "hadolint", 1, true)]
    [InlineData(new[] { "hadolint", "Dockerfile" }, "hadolint", 2, false)]
    public void Default_Registry_Dispatches(string[] argv, string wantName, int exitCode, bool filters)
    {
        var r = Registry.Default();
        Assert.True(r.TryLookup(argv, out var entry),
            $"{string.Join(" ", argv)}: embedded TOML filter not registered");
        Assert.Equal(wantName, entry.Name);
        Assert.Equal(filters, entry.Filters(exitCode));
    }

    /// <summary>Near-miss commands must not dispatch — passthrough is the default.</summary>
    [Theory]
    [InlineData("makepkg -si")]
    [InlineData("gccgo build")]
    [InlineData("terraform apply")]
    [InlineData("terraform state list")]
    public void Default_Registry_IgnoresNearMisses(string command)
    {
        var r = Registry.Default();
        Assert.False(r.TryLookup(command.Split(' '), out _),
            $"{command}: must not dispatch a long-tail filter");
    }
}
