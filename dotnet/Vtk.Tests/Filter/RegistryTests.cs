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

    /// <summary>Only exact "cmd sub" keys make a command pair-keyed; bare keys and regex entries do not.</summary>
    [Fact]
    public void PairKeyedCommands_ListsFirstTokensOfPairKeysOnly()
    {
        var r = new Registry();
        r.Register("git status", s => s);
        r.Register("git log", s => s);
        r.Register("ls", s => s);
        r.RegisterRegex(new Regex(@"^cargo (build|test)\b"), s => s);

        Assert.Equal(new HashSet<string> { "git" }, r.PairKeyedCommands);
    }

    [Fact]
    public void Default_PairKeyedCommands_CoverShippedMultiplexers()
    {
        var pairs = Registry.Default().PairKeyedCommands;
        Assert.Superset(new HashSet<string> { "git", "gh", "npx", "dbmate" }, new HashSet<string>(pairs));
        Assert.DoesNotContain("ls", pairs);
        Assert.DoesNotContain("mocha", pairs);
    }

    /// <summary>
    /// #139: the `vtk gaps` family key is "argv[0] argv[1]" for pair-keyed
    /// commands (after git global-option normalization) and argv[0] for
    /// everything else, so a multiplexer names its actual missing subcommand.
    /// </summary>
    [Theory]
    [InlineData("git rev-parse --show-toplevel", "git rev-parse")]
    [InlineData("git fetch origin", "git fetch")]
    [InlineData("git -C ../wt rev-parse HEAD", "git rev-parse")]
    [InlineData("git --no-pager diff --stat", "git diff")]
    [InlineData("git -c core.quotepath=off --no-pager log -1", "git log")]
    [InlineData("git -C", "git -C")]           // option without its value: not normalized
    [InlineData("git --bare status", "git --bare")] // unknown global option: don't guess
    [InlineData("git", "git")]
    [InlineData("gh api repos/o/r", "gh api")]
    [InlineData("gh -R o/r issue list", "gh -R")]
    [InlineData("npx tsc --noEmit", "npx tsc")]
    [InlineData("dbmate wait", "dbmate wait")]
    [InlineData("ls -la", "ls")]
    [InlineData("mocha test/x.js", "mocha")]
    [InlineData("cargo build --release", "cargo")]
    [InlineData("  ls  -la ", "ls")]
    [InlineData("", "")]
    public void Default_GapFamily_PairKeysMultiplexersOnly(string cmd, string want)
    {
        Assert.Equal(want, Registry.Default().GapFamily(cmd));
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

    /// <summary>
    /// Known git global options ahead of the subcommand normalize to the bare
    /// pair key for the six allowed subcommands (#152): each form resolves to
    /// the same entry as `git <sub>`.
    /// </summary>
    public static TheoryData<string[], string> GitNormalizedCases()
    {
        var data = new TheoryData<string[], string>();
        var prefixes = new[]
        {
            new[] { "-C", "dir" },
            new[] { "-c", "core.quotepath=off" },
            new[] { "--no-pager" },
            new[] { "-P" },
            new[] { "-p" },
            new[] { "--paginate" },
            new[] { "--git-dir=.git" },
            new[] { "--git-dir", ".git" },
            new[] { "--work-tree=wt" },
            new[] { "--work-tree", "wt" },
            new[] { "-C", "dir", "-c", "a=b", "--no-pager" },
        };
        foreach (var sub in new[] { "status", "branch", "add", "commit", "push", "pull" })
        {
            foreach (var prefix in prefixes)
            {
                data.Add(new[] { "git" }.Concat(prefix).Append(sub).ToArray(), "git " + sub);
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(GitNormalizedCases))]
    public void Default_GitGlobalOptions_ResolveToBarePairKey(string[] argv, string wantKey)
    {
        var r = Registry.Default();
        Assert.True(r.TryLookup(argv, out var entry), string.Join(" ", argv));
        Assert.Equal(wantKey, entry.Name);
        Assert.True(r.TryLookup(wantKey.Split(' '), out var bare));
        Assert.Same(bare, entry);
    }

    /// <summary>
    /// diff/show/log are excluded from normalization (#152): behind a global
    /// option they still miss and pass through (gap-logged), even though
    /// their bare keys are registered.
    /// </summary>
    [Theory]
    [InlineData("git --no-pager diff")]
    [InlineData("git -C x show")]
    [InlineData("git -C x log")]
    [InlineData("git -c a=b --git-dir=.git diff --stat")]
    public void Default_GitGlobalOptions_ExcludedSubcommandsStillMiss(string cmd)
    {
        var r = Registry.Default();
        Assert.False(r.TryLookup(cmd.Split(' '), out _), cmd);
    }

    /// <summary>An unknown global option ahead of the subcommand keeps the conservative miss: no guessing at which token is the subcommand.</summary>
    [Fact]
    public void Default_GitUnknownGlobalOption_StillMisses()
    {
        Assert.False(Registry.Default().TryLookup(new[] { "git", "--exec-path=x", "status" }, out _));
    }
}
