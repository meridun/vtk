using Vtk.Core.Spool;

namespace Vtk.Tests.Discover;

/// <summary>
/// End-to-end `vtk discover` against a temp sessions directory of fixture
/// transcripts (#44): ranked report with unwrapped/candidate classes against
/// the real shipped registry, --top, the wrapped-invocation subtraction
/// (#115), and the exit-code convention (0 success, 1 environment, 2 usage).
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

    private int Run(out string stdout, out string stderr, params string[] args) =>
        RunWith(null, out stdout, out stderr, args);

    private int RunWith(
        IReadOnlyList<Invocation>? invocations, out string stdout, out string stderr, params string[] args)
    {
        using var so = new StringWriter();
        using var se = new StringWriter();
        var code = Vtk.Cli.Discover.Run(
            args, so, se, invocations is null ? null : () => invocations);
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
        // 6 Bash events in the fixture (vtk-wrapped and error events counted
        // as commands, then skipped by classification).
        Assert.Contains("from 6 commands across 1 sessions", stdout);
        // Bare `git status` (x2) is covered by the shipped registry: unwrapped.
        Assert.Contains("unwrapped", stdout);
        Assert.Contains("git status", stdout);
        // `cd dotnet && dotnet test ...` is covered by the shipped dotnet def
        // (#121): unwrapped under the registry name, never a candidate.
        Assert.Matches(@"unwrapped\s+dotnet\s", stdout);
        Assert.Contains("dotnet test Vtk.sln", stdout);
        Assert.DoesNotContain("dotnet-test", stdout);
        // `go test ./...` matches the go-test rule with no shipped filter: candidate.
        Assert.Contains("candidate", stdout);
        Assert.Contains("go-test", stdout);
        // The vtk-wrapped `git log` and the failed `dotnet build` produce no rows.
        Assert.DoesNotContain("git log", stdout);
        Assert.DoesNotContain("dotnet-build", stdout);
        // go-test's observed output outranks git status's.
        Assert.True(stdout.IndexOf("go-test", StringComparison.Ordinal)
            < stdout.IndexOf("git status", StringComparison.Ordinal));
    }

    [Fact]
    public void Run_Top_LimitsRows()
    {
        CopyFixture("session_discover.jsonl", "a.jsonl");

        var code = Run(out var stdout, out _, "--sessions", _sessions, "--top", "1");

        Assert.Equal(0, code);
        Assert.Contains("discover: 3 opportunities", stdout);
        Assert.Contains("go-test", stdout);
        // The lower-ranked `dotnet test` and `git status` rows are cut; only
        // the legend mentions the unwrapped class.
        Assert.DoesNotContain("dotnet test Vtk.sln", stdout);
        Assert.DoesNotContain("git status", stdout);
    }

    [Fact]
    public void Run_WrappedInvocation_SubtractsMatchedCall()
    {
        // The fixture's first `git status` result line is stamped
        // 2026-07-19T10:00:01Z; a logged vtk invocation seconds earlier
        // matches it, so the row drops from 2 calls to 1 and the summary
        // reports the exclusion (#115).
        CopyFixture("session_discover.jsonl", "a.jsonl");
        var invs = new List<Invocation>
        {
            new() { Cmd = "git status", Time = new DateTime(2026, 7, 19, 10, 0, 0, DateTimeKind.Utc) },
        };

        var code = RunWith(invs, out var stdout, out _, "--sessions", _sessions);

        Assert.Equal(0, code);
        Assert.Contains("1 matched logged vtk invocations (already wrapped; excluded)", stdout);
        Assert.Matches(@"git status\s+1\s", stdout);
    }

    [Fact]
    public void Run_WrappedInvocationOutsideWindow_NoSubtraction()
    {
        // Same command a day earlier in the log: no match, both calls report.
        CopyFixture("session_discover.jsonl", "a.jsonl");
        var invs = new List<Invocation>
        {
            new() { Cmd = "git status", Time = new DateTime(2026, 7, 18, 10, 0, 0, DateTimeKind.Utc) },
        };

        var code = RunWith(invs, out var stdout, out _, "--sessions", _sessions);

        Assert.Equal(0, code);
        Assert.DoesNotContain("matched logged vtk invocations", stdout);
        Assert.Matches(@"git status\s+2\s", stdout);
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

    [Fact]
    public void Run_Since_BeforeFixtureStamps_KeepsRowsAndPrintsWindow()
    {
        // Only the first `git status` result line in the fixture carries a
        // timestamp (2026-07-19T10:00:01Z); the other 5 events are undated.
        // A window opening the day before keeps the stamped event, drops the
        // undated ones with a count, and names the window (#140).
        CopyFixture("session_discover.jsonl", "a.jsonl");

        var code = Run(out var stdout, out _, "--sessions", _sessions, "--since", "2026-07-18");

        Assert.Equal(0, code);
        Assert.Contains(
            "discover: 1 opportunities from 1 commands across 1 sessions; window since 2026-07-18T00:00:00Z (2026-07-18); 5 undated events dropped",
            stdout);
        Assert.Matches(@"unwrapped\s+git status\s+1\s", stdout);
        Assert.DoesNotContain("go-test", stdout);
    }

    [Fact]
    public void Run_Since_AfterFixtureStamps_DropsEvents()
    {
        // Same transcript, window opening after the stamp: the file passes
        // the mtime pre-filter (just copied) but the stamped event falls
        // before the bound (dropped silently — it is dated, just old) and the
        // undated ones are counted. Empty result is exit 0, not an
        // environment error.
        CopyFixture("session_discover.jsonl", "a.jsonl");

        var code = Run(out var stdout, out _, "--sessions", _sessions, "--since", "2026-07-19T10:00:02Z");

        Assert.Equal(0, code);
        Assert.Contains(
            "no opportunities found (0 commands across 1 sessions; window since 2026-07-19T10:00:02Z (2026-07-19T10:00:02Z); 5 undated events dropped)",
            stdout);
    }

    [Fact]
    public void Run_Since_StaleSessionFile_SkippedByMtimePreFilter()
    {
        // A transcript last written before the window opened is never read
        // (#140 pre-filter), so it contributes neither commands nor a session
        // to the count; the fresh copy still does.
        CopyFixture("session_discover.jsonl", "old.jsonl");
        File.SetLastWriteTimeUtc(Path.Combine(_sessions, "old.jsonl"), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        CopyFixture("session_discover.jsonl", "new.jsonl");

        var code = Run(out var stdout, out _, "--sessions", _sessions, "--since", "2026-07-01");

        Assert.Equal(0, code);
        // One session, one stamped command, 5 undated — the stale copy would
        // have doubled every figure had it been read.
        Assert.Contains("from 1 commands across 1 sessions; window since 2026-07-01T00:00:00Z (2026-07-01); 5 undated events dropped", stdout);
    }

    [Fact]
    public void Run_NoSince_OutputUnchanged()
    {
        // Opt-in flag: without it the summary line carries no window note.
        CopyFixture("session_discover.jsonl", "a.jsonl");

        var code = Run(out var stdout, out _, "--sessions", _sessions);

        Assert.Equal(0, code);
        Assert.Contains("discover: 3 opportunities from 6 commands across 1 sessions\n", stdout.Replace("\r\n", "\n"));
        Assert.DoesNotContain("window", stdout);
    }

    public static IEnumerable<object[]> UsageErrorCases()
    {
        yield return new object[] { new[] { "--bogus" } };
        yield return new object[] { new[] { "--sessions" } };
        yield return new object[] { new[] { "--top" } };
        yield return new object[] { new[] { "--top", "0" } };
        yield return new object[] { new[] { "--top", "abc" } };
        yield return new object[] { new[] { "--since" } };
        yield return new object[] { new[] { "--since", "0d" } };
        yield return new object[] { new[] { "--since", "bogus" } };
        yield return new object[] { new[] { "--since", "14" } };
        yield return new object[] { new[] { "--since", "999999d" } };
        yield return new object[] { new[] { "--since", "99999999h" } };
    }

    [Theory]
    [MemberData(nameof(UsageErrorCases))]
    public void Run_UsageErrors_Exit2(string[] args)
    {
        Assert.Equal(2, Run(out _, out _, args));
    }
}
