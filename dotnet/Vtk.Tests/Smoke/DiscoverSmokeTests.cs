using Xunit;

namespace Vtk.Tests.Smoke;

/// <summary>
/// Real-binary smoke for `vtk discover` (#44): exercises the published
/// vtk.exe against a scratch sessions directory of fixture transcripts —
/// ranked report with unwrapped/candidate classes, --top, and the exit-code
/// convention (0 success, 1 environment, 2 usage).
/// </summary>
[Collection("Smoke")]
public class DiscoverSmokeTests : IDisposable
{
    private static readonly string FixtureDir = Path.Combine(
        AppContext.BaseDirectory, "Session", "testdata");

    private readonly SmokeHarness _h = new();
    private readonly string _sessions;

    public DiscoverSmokeTests()
    {
        _sessions = Path.Combine(_h.Repo, "sessions");
        Directory.CreateDirectory(_sessions);
    }

    public void Dispose() => _h.Dispose();

    private void CopyFixture(string name, string asName) =>
        File.Copy(Path.Combine(FixtureDir, name), Path.Combine(_sessions, asName));

    [Fact]
    public void WrappedInvocationCrossReference()
    {
        // A seeded invocation log in the isolated store home matches the
        // fixture's timestamped `git status` event: the report subtracts the
        // wrapped call and says so (#115).
        CopyFixture("session_discover.jsonl", "a.jsonl");
        var storeDir = Path.Combine(_h.Home, "vtk");
        Directory.CreateDirectory(storeDir);
        File.WriteAllText(Path.Combine(storeDir, "invocations.jsonl"),
            "{\"time\":\"2026-07-19T10:00:00Z\",\"cmd\":\"git status\",\"raw_bytes\":100," +
            "\"out_bytes\":10,\"filtered\":true,\"tty\":false,\"reason\":\"git status\"}\n");

        var (stdout, _, code) = _h.Run(_h.Repo, "discover", "--sessions", _sessions);

        Assert.Equal(0, code);
        Assert.Contains("1 matched logged vtk invocations (already wrapped; excluded)", stdout);
        Assert.Matches(@"git status\s+1\s", stdout);
    }

    [Fact]
    public void DiscoverWorkflow()
    {
        CopyFixture("session_discover.jsonl", "a.jsonl");

        // ranked report: shipped-filter coverage dedupes to `unwrapped`,
        // rule matches without a filter surface as `candidate`.
        var (rptOut, _, rptCode) = _h.Run(_h.Repo, "discover", "--sessions", _sessions);
        Assert.Equal(0, rptCode);
        Assert.Contains("from 6 commands across 1 sessions", rptOut);
        Assert.Contains("unwrapped", rptOut);
        Assert.Contains("git status", rptOut);
        // `dotnet test` is covered by the shipped dotnet def (#121):
        // unwrapped, never a candidate.
        Assert.Matches(@"unwrapped\s+dotnet\s", rptOut);
        Assert.DoesNotContain("dotnet-test", rptOut);
        Assert.Contains("candidate", rptOut);
        Assert.Contains("go-test", rptOut);
        // vtk-wrapped `git log` and the failed `dotnet build` produce no rows.
        Assert.DoesNotContain("git log", rptOut);
        Assert.DoesNotContain("dotnet-build", rptOut);

        // --top limits the table without changing the summary counts;
        // go-test's observed output outranks git status's, so it survives.
        var (topOut, _, topCode) = _h.Run(_h.Repo, "discover", "--sessions", _sessions, "--top", "1");
        Assert.Equal(0, topCode);
        Assert.Contains("from 6 commands across 1 sessions", topOut);
        Assert.Contains("go-test", topOut);
        Assert.DoesNotContain("git status", topOut);

        // missing sessions dir is an environment failure: exit 1
        var (_, envErr, envCode) = _h.Run(_h.Repo, "discover", "--sessions", Path.Combine(_h.Repo, "nope"));
        Assert.Equal(1, envCode);
        Assert.Contains("no session transcripts", envErr);

        // unknown flag is a usage failure: exit 2
        var (_, useErr, useCode) = _h.Run(_h.Repo, "discover", "--bogus");
        Assert.Equal(2, useCode);
        Assert.Contains("usage: vtk discover", useErr);
    }

    [Fact]
    public void SinceWindow()
    {
        // #140 through the real binary: a transcript last written before the
        // window is skipped by the mtime pre-filter; inside the read file the
        // one stamped event (2026-07-19T10:00:01Z) survives a window opening
        // before it and the 5 undated events are dropped with a count.
        CopyFixture("session_discover.jsonl", "old.jsonl");
        File.SetLastWriteTimeUtc(Path.Combine(_sessions, "old.jsonl"), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        CopyFixture("session_discover.jsonl", "new.jsonl");

        var (winOut, _, winCode) = _h.Run(_h.Repo, "discover", "--sessions", _sessions, "--since", "2026-07-01");
        Assert.Equal(0, winCode);
        Assert.Contains("discover: 1 opportunities from 1 commands across 1 sessions; window since 2026-07-01T00:00:00Z (2026-07-01); 5 undated events dropped", winOut);
        Assert.Matches(@"unwrapped\s+git status\s+1\s", winOut);
        Assert.DoesNotContain("go-test", winOut);

        // window opening after the stamp: dated event dropped, empty result is
        // still exit 0 (the directory is not empty — not an environment error).
        var (emptyOut, _, emptyCode) = _h.Run(_h.Repo, "discover", "--sessions", _sessions, "--since", "2026-07-19T10:00:02Z");
        Assert.Equal(0, emptyCode);
        Assert.Contains("no opportunities found (0 commands across 1 sessions; window since 2026-07-19T10:00:02Z (2026-07-19T10:00:02Z); 5 undated events dropped)", emptyOut);

        // opt-in: no flag, no window note, both files read.
        var (bareOut, _, bareCode) = _h.Run(_h.Repo, "discover", "--sessions", _sessions);
        Assert.Equal(0, bareCode);
        Assert.Contains("from 12 commands across 2 sessions", bareOut);
        Assert.DoesNotContain("window", bareOut);

        // malformed / missing value is a usage failure: exit 2
        var (_, badErr, badCode) = _h.Run(_h.Repo, "discover", "--sessions", _sessions, "--since", "14w");
        Assert.Equal(2, badCode);
        Assert.Contains("invalid --since", badErr);
        var (_, _, missingCode) = _h.Run(_h.Repo, "discover", "--sessions", _sessions, "--since");
        Assert.Equal(2, missingCode);
    }
}
