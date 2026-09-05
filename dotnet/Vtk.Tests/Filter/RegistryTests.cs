using System.Text.RegularExpressions;
using Vtk.Core.Filter;
using Xunit;

namespace Vtk.Tests.Filter;

public class RegistryTests
{
    [Fact]
    public void Lookup_MatchesTwoTokenKeyFirst()
    {
        var r = new Registry();
        r.Register("git status", s => "compact-status");
        r.Register("git", s => "compact-bare");

        Assert.True(r.TryLookup(new[] { "git", "status" }, out var entry));
        Assert.Equal("compact-status", entry.Fn(""));
    }

    [Fact]
    public void Lookup_FallsBackToBareCommand()
    {
        var r = new Registry();
        r.Register("git", s => "compact-bare");

        Assert.True(r.TryLookup(new[] { "git", "-C", "dir", "status" }, out var entry));
        Assert.Equal("compact-bare", entry.Fn(""));
    }

    [Fact]
    public void Lookup_NoMatch_ReturnsFalse()
    {
        var r = new Registry();
        Assert.False(r.TryLookup(new[] { "unknown" }, out _));
    }

    [Fact]
    public void Entry_FiltersOnlyAllowlistedExitCodes()
    {
        var r = new Registry();
        r.RegisterCodes("eslint", s => s, 0, 1);
        r.TryLookup(new[] { "eslint" }, out var entry);

        Assert.True(entry.Filters(0));
        Assert.True(entry.Filters(1));
        Assert.False(entry.Filters(2));
    }

    /// <summary>
    /// RegisterRegex filters match against the full command string and are
    /// consulted only after the exact-key map misses.
    /// </summary>
    [Fact]
    public void RegisterRegex_MatchesFullCommandString()
    {
        var r = new Registry();
        const string marker = "REGEX";
        r.RegisterRegex(new Regex(@"^cargo (build|test)\b"), _ => marker, 0, 1);

        Assert.False(r.TryLookup(new[] { "cargo", "clippy" }, out _));
        Assert.True(r.TryLookup(new[] { "cargo", "build", "--release" }, out var entry));
        Assert.Equal(marker, entry.Fn("x"));
        Assert.True(entry.Filters(0));
        Assert.True(entry.Filters(1));
    }

    /// <summary>Exact keys win over regex fallbacks: a hand-written family key is never shadowed.</summary>
    [Fact]
    public void ExactKey_BeatsRegex()
    {
        var r = new Registry();
        r.Register("git status", _ => "KEY");
        r.RegisterRegex(new Regex("^git"), _ => "REGEX");

        Assert.True(r.TryLookup(new[] { "git", "status" }, out var entry));
        Assert.Equal("KEY", entry.Fn("x"));
    }

    /// <summary>
    /// The default registry wires the embedded TOML filters onto the regex
    /// path: cargo (a TOML demonstrator) matches, and its exit-code allowlist
    /// is {0}.
    /// </summary>
    [Fact]
    public void Default_LoadsTomlFilters()
    {
        var r = Registry.Default();
        Assert.True(r.TryLookup(new[] { "cargo", "build" }, out var entry),
            "embedded cargo TOML filter not registered");
        Assert.NotNull(entry.Fn);
        Assert.True(entry.Filters(0) && !entry.Filters(101),
            "cargo should filter exit 0 only (compile errors exit 101 stay raw)");
        Assert.False(string.IsNullOrEmpty(entry.Name), "TOML entries carry their def name for telemetry");
    }

    /// <summary>
    /// Entries carry their registry identity so telemetry can name the filter
    /// that engaged (#96): exact-key entries use the key, regex entries
    /// default to the pattern.
    /// </summary>
    [Fact]
    public void Entry_NameCarriesRegistryIdentity()
    {
        var r = new Registry();
        r.Register("git status", _ => "");
        r.RegisterRegex(new Regex(@"^cargo\b"), _ => "");

        Assert.True(r.TryLookup(new[] { "git", "status" }, out var byKey));
        Assert.Equal("git status", byKey.Name);
        Assert.True(r.TryLookup(new[] { "cargo", "build" }, out var byRegex));
        Assert.Equal(@"^cargo\b", byRegex.Name);
    }

    /// <summary>mocha exits with its failure count, min(failures, 255): every code 0..255 is a report and filters (#138); both registry keys agree.</summary>
    [Theory]
    [InlineData("mocha")]
    [InlineData("npx mocha")]
    public void Default_Mocha_AllowsFailureCountExits(string key)
    {
        var r = Registry.Default();
        Assert.True(r.TryLookup(key.Split(' '), out var entry));
        Assert.Equal(key, entry.Name);
        Assert.True(entry.Filters(0) && entry.Filters(1) && entry.Filters(2) && entry.Filters(255));
        Assert.False(entry.Filters(256));
        Assert.False(entry.Filters(-1));
    }

    /// <summary>`gh run` dispatches both run-list tables and CI job logs (#96), on exit 0 and 1 (`--exit-status` forms).</summary>
    [Fact]
    public void Default_GhRun_AllowsExitZeroAndOne()
    {
        var r = Registry.Default();
        Assert.True(r.TryLookup(new[] { "gh", "run" }, out var entry));
        Assert.Equal("gh run", entry.Name);
        Assert.True(entry.Filters(0) && entry.Filters(1) && !entry.Filters(2));
    }
}
