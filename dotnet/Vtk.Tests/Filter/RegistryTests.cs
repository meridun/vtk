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
}
