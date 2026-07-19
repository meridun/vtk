using Vtk.Core.Filter;
using Xunit;

namespace Vtk.Tests.Filter;

public class PrefixTests
{
    public static TheoryData<string[], string[]> UnwrapCases() => new()
    {
        // cross-env with assignments (the telemetry-evidenced shape, #97)
        {
            new[] { "cross-env", "INTEGRATION=1", "mocha", "test/integration/**/*.test.js" },
            new[] { "mocha", "test/integration/**/*.test.js" }
        },
        // cross-env with multiple assignments
        {
            new[] { "cross-env", "A=1", "B=2", "eslint", ".", "--cache" },
            new[] { "eslint", ".", "--cache" }
        },
        // cross-env with zero assignments is still transparent
        { new[] { "cross-env", "mocha" }, new[] { "mocha" } },
        // npx, bare and with transparent flags
        { new[] { "npx", "mocha", "--grep", "x" }, new[] { "mocha", "--grep", "x" } },
        { new[] { "npx", "--yes", "eslint", "." }, new[] { "eslint", "." } },
        { new[] { "npx", "-y", "eslint", "." }, new[] { "eslint", "." } },
        { new[] { "npx", "--no-install", "mocha" }, new[] { "mocha" } },
        // bare env assignments
        { new[] { "FOO=bar", "grep", "x" }, new[] { "grep", "x" } },
        { new[] { "A=1", "B=2", "ls", "-la" }, new[] { "ls", "-la" } },
        // stacked prefixes unwrap to a fixpoint
        {
            new[] { "A=1", "cross-env", "B=2", "npx", "-y", "mocha", "--grep", "x" },
            new[] { "mocha", "--grep", "x" }
        },
        { new[] { "npx", "cross-env", "A=1", "mocha" }, new[] { "mocha" } },
    };

    [Theory]
    [MemberData(nameof(UnwrapCases))]
    public void Unwrap_StripsTransparentPrefixes(string[] argv, string[] want)
    {
        Assert.Equal(want, Prefix.Unwrap(argv));
    }

    public static TheoryData<string[]> NoUnwrapCases() => new()
    {
        // no prefix present
        new[] { "git", "status" },
        new[] { "mocha", "test/**/*.js" },
        // degenerate: nothing after the prefix
        new[] { "cross-env" },
        new[] { "cross-env", "A=1" },
        new[] { "npx" },
        new[] { "A=1", "B=2" },
        // npx with a non-transparent flag: what runs is not the next token
        new[] { "npx", "-p", "typescript", "tsc" },
        new[] { "npx", "--package", "cowsay", "cowsay", "hi" },
        // empty argv
        System.Array.Empty<string>(),
    };

    [Theory]
    [MemberData(nameof(NoUnwrapCases))]
    public void Unwrap_LeavesNonTransparentShapesUnchanged(string[] argv)
    {
        Assert.Equal(argv, Prefix.Unwrap(argv));
    }

    /// <summary>
    /// The point of the unwrap (#97): a wrapped invocation resolves to the
    /// same registry entry as the bare tool, exit-code allowlist included.
    /// </summary>
    [Fact]
    public void Unwrap_LetsWrappedTrafficReachExistingFilters()
    {
        var r = Registry.Default();

        var argv = new[] { "cross-env", "INTEGRATION=1", "mocha", "test/integration/**/*.test.js", "--timeout", "20000" };
        Assert.False(r.TryLookup(argv, out _)); // wrapped form alone never matched
        Assert.True(r.TryLookup(Prefix.Unwrap(argv), out var entry));
        Assert.True(entry.Filters(0) && entry.Filters(1), "mocha filters exits 0 and 1");

        var npx = new[] { "npx", "--yes", "eslint", ".", "--cache" };
        Assert.True(r.TryLookup(Prefix.Unwrap(npx), out var eslint));
        Assert.True(eslint.Filters(1), "eslint problems-found exit 1 stays filterable through npx unwrap");
    }
}
