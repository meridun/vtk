namespace Vtk.Tests.Discover;

/// <summary>
/// End-to-end `vtk discover` against a temp sessions directory of fixture
/// transcripts (#44): ranked report with unwrapped/candidate classes against
/// the real shipped registry, --top, and the exit-code convention
/// (0 success, 1 environment, 2 usage).
/// </summary>
public class DiscoverCliTests : IDisposable
{
    private static readonly string FixtureDir = Path.Combine(
        AppContext.BaseDirectory, "Session", "testdata");

    private readonly string _root;
    private readonly string _sessions;

    public DiscoverCliTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "vtk-discover-test-" + Guid.NewGuid().ToString("N"));
        _sessions = Path.Combine(_root, "sessions");
        Directory.CreateDirectory(_sessions);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private void CopyFixture(string name, string asName) =>
        File.Copy(Path.Combine(FixtureDir, name), Path.Combine(_sessions, asName));

    private int Run(out string stdout, out string stderr, params string[] args)
    {
        using var so = new StringWriter();
        using var se = new StringWriter();
        var code = Vtk.Cli.Discover.Run(args, so, se);
        stdout = so.ToString();
        stderr = se.ToString();
        return code;
    }

    [Fact]
    public void Run_ReportsRankedOpportunities()
    {
        CopyFixture("session_discover.jsonl", "a.jsonl");

        var code = Run(out var stdout, out _, "--sessions", _sessions);

        Assert.Equal(0, code);
        // 5 Bash events in the fixture (vtk-wrapped and error events counted
        // as commands, then skipped by classification).
        Assert.Contains("from 5 commands across 1 sessions", stdout);
        // Bare `git status` (x2) is covered by the shipped registry: unwrapped.
        Assert.Contains("unwrapped", stdout);
        Assert.Contains("git status", stdout);
        // `cd dotnet && dotnet test ...` matches the dotnet-test rule: candidate.
        Assert.Contains("candidate", stdout);
        Assert.Contains("dotnet-test", stdout);
        // The vtk-wrapped `git log` and the failed `dotnet build` produce no rows.
        Assert.DoesNotContain("git log", stdout);
        Assert.DoesNotContain("dotnet-build", stdout);
        // dotnet-test's observed output outranks git status's.
        Assert.True(stdout.IndexOf("dotnet-test", StringComparison.Ordinal)
            < stdout.IndexOf("git status", StringComparison.Ordinal));
    }

    [Fact]
    public void Run_Top_LimitsRows()
    {
        CopyFixture("session_discover.jsonl", "a.jsonl");

        var code = Run(out var stdout, out _, "--sessions", _sessions, "--top", "1");

        Assert.Equal(0, code);
        Assert.Contains("discover: 2 opportunities", stdout);
        Assert.Contains("dotnet-test", stdout);
        // The lower-ranked `git status` row is cut; only the legend mentions
        // the unwrapped class.
        Assert.DoesNotContain("git status", stdout);
    }

    [Fact]
    public void Run_NoOpportunities_StillSucceeds()
    {
        // session_basic's only clean Bash command is `git status`... which is
        // covered. Use an empty transcript instead: no commands, no rows.
        File.WriteAllText(Path.Combine(_sessions, "empty.jsonl"), "{\"type\":\"summary\"}\n");

        var code = Run(out var stdout, out _, "--sessions", _sessions);

        Assert.Equal(0, code);
        Assert.Contains("no opportunities found (0 commands across 1 sessions)", stdout);
    }

    [Fact]
    public void Run_MissingSessionsDir_EnvFailure()
    {
        var code = Run(out _, out var stderr, "--sessions", Path.Combine(_root, "nope"));

        Assert.Equal(1, code);
        Assert.Contains("no session transcripts", stderr);
    }

    public static IEnumerable<object[]> UsageErrorCases()
    {
        yield return new object[] { new[] { "--bogus" } };
        yield return new object[] { new[] { "--sessions" } };
        yield return new object[] { new[] { "--top" } };
        yield return new object[] { new[] { "--top", "0" } };
        yield return new object[] { new[] { "--top", "abc" } };
    }

    [Theory]
    [MemberData(nameof(UsageErrorCases))]
    public void Run_UsageErrors_Exit2(string[] args)
    {
        Assert.Equal(2, Run(out _, out _, args));
    }
}
