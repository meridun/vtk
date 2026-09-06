using Vtk.Core.Spool;
using Xunit;

namespace Vtk.Tests.Spool;

public class StoreTests : IDisposable
{
    private readonly string _dir;
    private readonly Store _store;

    public StoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vtk-test-" + Path.GetRandomFileName());
        _store = Store.NewAt(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>Directory the entries in these tests are "run from" (#136: cwd is part of the key).</summary>
    private const string Cwd = "/proj";

    [Fact]
    public void WriteThenRead_RoundTrips()
    {
        var argv = new[] { "git", "status" };
        var id = _store.Write(Cwd, argv, "some raw output\n", DateTime.UtcNow);
        var content = _store.Read(id);
        Assert.Contains("# vtk spool", content);
        Assert.Contains("git status", content);
        Assert.Contains("# cwd: " + Cwd + "\n", content);
        Assert.Contains("some raw output", content);
        Assert.Equal(Cwd, Store.HeaderCwd(content));
    }

    [Theory]
    [InlineData("/proj", "/proj", true)]
    [InlineData("/proj", "/proj-wt-1", false)]
    [InlineData("/proj/a", "/proj/b", false)]
    public void Id_KeysOnCwdAndArgv(string cwdA, string cwdB, bool same)
    {
        // #136: the same command from two worktrees must not share an entry;
        // the same command from the same directory still overwrites its own.
        var argv = new[] { "npm", "run", "test:unit" };
        Assert.Equal(same, Store.Id(cwdA, argv) == Store.Id(cwdB, argv));
        Assert.Matches("^[0-9a-f]{4}$", Store.Id(cwdA, argv));
    }

    [Fact]
    public void Write_SameArgvFromTwoCwds_KeepsBothEntries()
    {
        var argv = new[] { "npm", "test" };
        var idA = _store.Write("/wt/1", argv, "output from wt 1\n", DateTime.UtcNow);
        var idB = _store.Write("/wt/2", argv, "output from wt 2\n", DateTime.UtcNow);
        Assert.NotEqual(idA, idB);
        Assert.Contains("output from wt 1", _store.Read(idA));
        Assert.Contains("output from wt 2", _store.Read(idB));
    }

    [Theory]
    [InlineData("# vtk spool\n# cmd: git status\n# cwd: /wt/1\n# time: 2026-09-06T00:00:00Z\n\nbody\n", "/wt/1")]
    [InlineData("# vtk spool\n# cmd: git status\n# cwd: C:\\Claude\\vtk-wt\\136\n# time: 2026-09-06T00:00:00Z\n\nbody\n", "C:\\Claude\\vtk-wt\\136")]
    [InlineData("# vtk spool\n# cmd: git status\n# time: 2026-09-06T00:00:00Z\n\n# cwd: /not/the/header\n", null)]
    [InlineData("# vtk spool\r\n# cmd: git status\r\n# cwd: /wt/1\r\n# time: 2026-09-06T00:00:00Z\r\n\r\nbody\r\n", "/wt/1")]
    public void HeaderCwd_ReadsHeaderOnly(string content, string? expected)
    {
        // Legacy (pre-#136) entries carry no cwd line → null, so `vtk show`
        // stays silent on them; a `# cwd:` in the body is never mistaken for
        // the header.
        Assert.Equal(expected, Store.HeaderCwd(content));
    }

    [Fact]
    public void Write_RedactsSecrets()
    {
        var id = _store.Write(Cwd, new[] { "curl" }, "Authorization: Bearer sekret123\n", DateTime.UtcNow);
        var content = _store.Read(id);
        Assert.DoesNotContain("sekret123", content);
        Assert.Contains("[REDACTED]", content);
    }

    [Fact]
    public async Task Write_RetriesPastTransientDestinationLock()
    {
        // #125: concurrent identical invocations replace the same spool file;
        // on Windows the replace can transiently fail with a sharing
        // violation. Hold the destination open with no sharing, release it on
        // another thread inside the retry window, and require Write to
        // succeed via retry. Sharing violations are Windows semantics — on
        // other platforms the open never blocks the replace, so skip.
        if (!OperatingSystem.IsWindows()) return;

        var argv = new[] { "git", "log" };
        var id = _store.Write(Cwd, argv, "first\n", DateTime.UtcNow);
        var dest = Path.Combine(_dir, "spool", id + ".txt");

        var lockFs = new FileStream(dest, FileMode.Open, FileAccess.Read, FileShare.None);
        var release = Task.Run(async () =>
        {
            await Task.Delay(60); // shorter than the 25+50+100 ms retry budget
            lockFs.Dispose();
        });
        try
        {
            var id2 = _store.Write(Cwd, argv, "second\n", DateTime.UtcNow);
            Assert.Equal(id, id2);
        }
        finally
        {
            await release;
        }
        Assert.Contains("second", _store.Read(id));
    }

    [Fact]
    public void Read_UnknownId_Throws()
    {
        Assert.ThrowsAny<Exception>(() => _store.Read("dead"));
    }

    [Fact]
    public void Read_InvalidIdFormat_Throws()
    {
        Assert.Throws<ArgumentException>(() => _store.Read("not-hex!"));
    }

    [Fact]
    public void Gaps_AggregatesNoFilterEntriesByFamily()
    {
        _store.LogInvocation(new Invocation { Cmd = "ls -la", RawBytes = 100, Reason = Store.ReasonNoFilter });
        _store.LogInvocation(new Invocation { Cmd = "ls -R", RawBytes = 50, Reason = Store.ReasonNoFilter });
        _store.LogInvocation(new Invocation { Cmd = "git status", RawBytes = 10, Filtered = true });

        var gaps = _store.Gaps();
        var ls = Assert.Single(gaps);
        Assert.Equal("ls", ls.Family);
        Assert.Equal(2, ls.Calls);
        Assert.Equal(150, ls.RawBytes);
    }

    /// <summary>
    /// #139: the family key is the caller's function over the redacted
    /// command line — `vtk gaps` passes a pair-aware key so git's unfiltered
    /// subcommands aggregate apart, while the default stays the first token.
    /// The window and the non-gap exclusions are unchanged by the key.
    /// </summary>
    [Fact]
    public void Gaps_UsesCallerFamilyKey_DefaultStaysFirstToken()
    {
        static string PairKey(string cmd)
        {
            var argv = cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return argv[0] == "git" && argv.Length >= 2 ? argv[0] + " " + argv[1] : argv[0];
        }
        var now = DateTime.UtcNow;
        _store.LogInvocation(new Invocation { Time = now, Cmd = "git rev-parse HEAD", RawBytes = 100, Reason = Store.ReasonNoFilter });
        _store.LogInvocation(new Invocation { Time = now, Cmd = "git rev-parse --show-toplevel", RawBytes = 100, Reason = Store.ReasonNoFilter });
        _store.LogInvocation(new Invocation { Time = now, Cmd = "git fetch origin", RawBytes = 50, Reason = Store.ReasonNoFilter });
        _store.LogInvocation(new Invocation { Time = now, Cmd = "git status", RawBytes = 999, Filtered = true });
        _store.LogInvocation(new Invocation { Time = now, Cmd = "git worktree list", RawBytes = 999, Reason = Store.ReasonNonzeroExit });
        _store.LogInvocation(new Invocation { Time = now, Cmd = "ls -la", RawBytes = 10, Reason = Store.ReasonNoFilter });

        var byPair = _store.Gaps(null, PairKey);
        Assert.Equal(
            new[] { ("git rev-parse", 2, 200L), ("git fetch", 1, 50L), ("ls", 1, 10L) },
            byPair.Select(g => (g.Family, g.Calls, g.RawBytes)));

        var byFirst = _store.Gaps();
        Assert.Equal(
            new[] { ("git", 3, 250L), ("ls", 1, 10L) },
            byFirst.Select(g => (g.Family, g.Calls, g.RawBytes)));

        // Filing thresholds apply to the pair-grained totals.
        Assert.Equal(new[] { "git rev-parse" }, _store.FileIssueGaps(150, 2, null, PairKey).Select(g => g.Family));
        Assert.Equal(new[] { "git" }, _store.FileIssueGaps(150, 2).Select(g => g.Family));
    }

    [Fact]
    public void Gaps_ExcludesSpawnFailEntries()
    {
        // Junk argv that never started a child (#118): logged — fallback
        // always logs — but never ranked as a coverage-gap family.
        _store.LogInvocation(new Invocation { Cmd = "--passthrough", Reason = Store.ReasonSpawnFail });
        _store.LogInvocation(new Invocation { Cmd = "definitely-not-a-command-xyz", Reason = Store.ReasonSpawnFail });
        _store.LogInvocation(new Invocation { Cmd = "ls -la", RawBytes = 100, Reason = Store.ReasonNoFilter });

        var gaps = _store.Gaps();
        var ls = Assert.Single(gaps);
        Assert.Equal("ls", ls.Family);
    }

    [Fact]
    public void FileIssueGaps_ExcludesSpawnFailEntries()
    {
        for (var i = 0; i < 5; i++)
            _store.LogInvocation(new Invocation { Cmd = "definitely-not-a-command-xyz run", RawBytes = 99999, OutBytes = 99999, Reason = Store.ReasonSpawnFail });

        Assert.Empty(_store.FileIssueGaps(1, 1));
    }

    [Fact]
    public void Gain_ComputesSavedBytesPerFamily()
    {
        _store.LogInvocation(new Invocation { Cmd = "git status", RawBytes = 1000, OutBytes = 100, Filtered = true });
        _store.LogInvocation(new Invocation { Cmd = "git log", RawBytes = 200, OutBytes = 200, Reason = Store.ReasonNoFilter });

        var report = _store.Gain();
        Assert.Equal(2, report.Calls);
        Assert.Equal(1200, report.RawBytes);
        Assert.Equal(300, report.OutBytes);
        Assert.Equal(900, report.Saved);
        Assert.Single(report.Families, f => f.Family == "git" && f.Saved == 900);
    }

    [Fact]
    public void Daily_GroupsByUtcDayAscending_AndSkipsUncountable()
    {
        var day1 = new DateTime(2026, 7, 10, 23, 50, 0, DateTimeKind.Utc);
        var day2 = new DateTime(2026, 7, 11, 0, 10, 0, DateTimeKind.Utc); // 20 min later, next UTC day
        _store.LogInvocation(new Invocation { Time = day2, Cmd = "git log", RawBytes = 500, OutBytes = 100, Filtered = true });
        _store.LogInvocation(new Invocation { Time = day1, Cmd = "git status", RawBytes = 1000, OutBytes = 200, Filtered = true });
        _store.LogInvocation(new Invocation { Time = day1, Cmd = "ls", RawBytes = 300, OutBytes = 300, Reason = Store.ReasonNoFilter });
        _store.LogInvocation(new Invocation { Time = day1, Cmd = "vim", RawBytes = 9999, OutBytes = 9999, TTY = true, Reason = Store.ReasonTTYBypass });

        var days = _store.Daily();

        Assert.Equal(2, days.Count);
        Assert.Equal(new DateOnly(2026, 7, 10), days[0].Date);
        Assert.Equal(2, days[0].Calls);
        Assert.Equal(1300, days[0].RawBytes);
        Assert.Equal(800, days[0].Saved);
        Assert.Equal(new DateOnly(2026, 7, 11), days[1].Date);
        Assert.Equal(1, days[1].Calls);
        Assert.Equal(400, days[1].Saved);
    }

    [Fact]
    public void BySession_AttributesByWindow_SkipsUncountableAndUnmatched()
    {
        var s1 = new DateTime(2026, 7, 10, 10, 0, 0, DateTimeKind.Utc);
        var s1End = s1.AddHours(1);
        var s2 = new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc);
        var s2End = s2.AddHours(1);
        var windows = new[] { ("aaaa1111", s1, s1End), ("bbbb2222", s2, s2End) };

        // inside s1
        _store.LogInvocation(new Invocation { Time = s1.AddMinutes(5), Cmd = "git status", RawBytes = 1000, OutBytes = 100, Filtered = true });
        _store.LogInvocation(new Invocation { Time = s1.AddMinutes(10), Cmd = "git log", RawBytes = 500, OutBytes = 200, Filtered = true });
        // inside s2
        _store.LogInvocation(new Invocation { Time = s2.AddMinutes(5), Cmd = "ls", RawBytes = 300, OutBytes = 300, Reason = Store.ReasonNoFilter });
        // outside every window — excluded from the view
        _store.LogInvocation(new Invocation { Time = s1End.AddMinutes(30), Cmd = "git diff", RawBytes = 700, OutBytes = 70, Filtered = true });
        // inside s1 but uncountable (tty bypass) — excluded
        _store.LogInvocation(new Invocation { Time = s1.AddMinutes(6), Cmd = "vim", RawBytes = 9999, OutBytes = 9999, TTY = true, Reason = Store.ReasonTTYBypass });

        var sessions = _store.BySession(windows);

        Assert.Equal(2, sessions.Count);
        Assert.Equal("aaaa1111", sessions[0].Id); // oldest start first
        Assert.Equal(2, sessions[0].Calls);
        Assert.Equal(1500, sessions[0].RawBytes);
        Assert.Equal(1200, sessions[0].Saved);
        Assert.Equal("bbbb2222", sessions[1].Id);
        Assert.Equal(1, sessions[1].Calls);
        Assert.Equal(0, sessions[1].Saved);
    }

    [Fact]
    public void BySession_OverlappingWindows_LatestStartWins()
    {
        var s1 = new DateTime(2026, 7, 10, 10, 0, 0, DateTimeKind.Utc);
        var s2 = new DateTime(2026, 7, 10, 10, 30, 0, DateTimeKind.Utc); // starts inside s1
        var windows = new[] { ("aaaa1111", s1, s1.AddHours(2)), ("bbbb2222", s2, s2.AddHours(2)) };

        _store.LogInvocation(new Invocation { Time = s2.AddMinutes(1), Cmd = "git status", RawBytes = 100, OutBytes = 10, Filtered = true });

        var sessions = _store.BySession(windows);

        // Attributed once, to the most-recently-started session; the
        // zero-call session is omitted entirely.
        Assert.Single(sessions);
        Assert.Equal("bbbb2222", sessions[0].Id);
        Assert.Equal(90, sessions[0].Saved);
    }

    [Fact]
    public void BySession_NoWindows_ReturnsEmpty()
    {
        _store.LogInvocation(new Invocation { Cmd = "git status", RawBytes = 100, OutBytes = 10, Filtered = true });
        Assert.Empty(_store.BySession(Array.Empty<(string, DateTime, DateTime)>()));
    }

    [Fact]
    public void Recent_ReturnsLastNCountableInLogOrder()
    {
        for (var i = 0; i < 5; i++)
            _store.LogInvocation(new Invocation { Cmd = $"git cmd{i}", RawBytes = 100 + i, OutBytes = 10, Filtered = true });
        _store.LogInvocation(new Invocation { Cmd = "vim", RawBytes = 9999, TTY = true, Reason = Store.ReasonTTYBypass });

        var recent = _store.Recent(3);

        Assert.Equal(3, recent.Count);
        Assert.Equal("git cmd2", recent[0].Cmd);
        Assert.Equal("git cmd4", recent[2].Cmd); // TTY entry excluded, not counted in the window
    }

    [Fact]
    public void Recent_FewerThanN_ReturnsAll()
    {
        _store.LogInvocation(new Invocation { Cmd = "git status", RawBytes = 100, OutBytes = 10, Filtered = true });
        Assert.Single(_store.Recent(10));
    }

    [Theory]
    [InlineData("gh pr list --json number,title", true)]
    [InlineData("gh issue view 5 --json=body", true)]
    [InlineData("gh run list --format json", true)]
    [InlineData("gh api repos --format=json", true)]
    [InlineData("kubectl get pods -o json", true)]
    [InlineData("tool --output json", true)]
    [InlineData("tool --output=json", true)]
    [InlineData("gh pr list", false)]
    [InlineData("git log --stat", false)]
    [InlineData("npm test", false)]
    [InlineData("cargo build --release", false)]
    [InlineData("echo --jsonish", false)] // narrow: not a real json flag
    [InlineData("grep json file.txt", false)]
    public void IsMachineReadable_MatchesNarrowJsonForms(string cmd, bool want)
    {
        Assert.Equal(want, Store.IsMachineReadable(cmd));
    }

    [Fact]
    public void FileIssueGaps_SelectsTrueGapsOverBothThresholds()
    {
        var now = DateTime.UtcNow;
        var entries = new[]
        {
            // cargo: two real gaps, 30000 raw over 2 calls — over threshold.
            new Invocation { Time = now, Cmd = "cargo build", RawBytes = 20000, OutBytes = 20000, Reason = Store.ReasonNoFilter },
            new Invocation { Time = now, Cmd = "cargo test", RawBytes = 10000, OutBytes = 10000, Reason = Store.ReasonNoFilter },
            // gh: mostly machine-readable — the json calls must be excluded,
            // leaving only 5000 raw over 1 call, under the call threshold.
            new Invocation { Time = now, Cmd = "gh pr list --json number", RawBytes = 40000, OutBytes = 40000, Reason = Store.ReasonNoFilter },
            new Invocation { Time = now, Cmd = "gh run view --json jobs", RawBytes = 40000, OutBytes = 40000, Reason = Store.ReasonNoFilter },
            new Invocation { Time = now, Cmd = "gh pr diff 5", RawBytes = 5000, OutBytes = 5000, Reason = Store.ReasonNoFilter },
            // docker: real gaps but total under the byte threshold.
            new Invocation { Time = now, Cmd = "docker ps", RawBytes = 100, OutBytes = 100, Reason = Store.ReasonNoFilter },
            new Invocation { Time = now, Cmd = "docker images", RawBytes = 100, OutBytes = 100, Reason = Store.ReasonNoFilter },
            new Invocation { Time = now, Cmd = "docker build .", RawBytes = 100, OutBytes = 100, Reason = Store.ReasonNoFilter },
            // Non-gap reasons: never eligible even at high volume.
            new Invocation { Time = now, Cmd = "make all", RawBytes = 99000, OutBytes = 99000, Reason = Store.ReasonNonzeroExit },
            new Invocation { Time = now, Cmd = "make lint", RawBytes = 99000, OutBytes = 99000, Reason = Store.ReasonFilterPanic },
            new Invocation { Time = now, Cmd = "make build", RawBytes = 99000, OutBytes = 60, Filtered = true },
        };
        foreach (var e in entries) _store.LogInvocation(e);

        var got = _store.FileIssueGaps(15000, 2);

        var cargo = Assert.Single(got);
        Assert.Equal("cargo", cargo.Family);
        Assert.Equal(2, cargo.Calls);
        Assert.Equal(30000, cargo.RawBytes);
    }

    [Fact]
    public void Gaps_Since_KeepsRowsAtOrAfterWindow_DropsUndated()
    {
        // #140: the window is a row predicate on Time; rows before it and
        // undated (default Time) rows leave the rollup, and the no-window
        // call still reads all history.
        var since = new DateTime(2026, 8, 20, 0, 0, 0, DateTimeKind.Utc);
        _store.LogInvocation(new Invocation { Time = since.AddDays(-30), Cmd = "grep -r foo", RawBytes = 1000, Reason = Store.ReasonNoFilter });
        _store.LogInvocation(new Invocation { Time = since.AddSeconds(-1), Cmd = "grep -n bar", RawBytes = 500, Reason = Store.ReasonNoFilter });
        _store.LogInvocation(new Invocation { Time = since, Cmd = "grep baz", RawBytes = 100, Reason = Store.ReasonNoFilter });
        _store.LogInvocation(new Invocation { Time = since.AddDays(1), Cmd = "ls -la", RawBytes = 40, Reason = Store.ReasonNoFilter });
        _store.LogInvocation(new Invocation { Cmd = "ls -R", RawBytes = 60, Reason = Store.ReasonNoFilter }); // undated
        _store.LogInvocation(new Invocation { Time = since.AddDays(-1), Cmd = "make all", RawBytes = 70, Reason = Store.ReasonFilterPanic });
        _store.LogInvocation(new Invocation { Time = since.AddDays(2), Cmd = "make lint", RawBytes = 30, Reason = Store.ReasonFilterPanic });

        var all = _store.Gaps();
        Assert.Equal(new[] { ("grep", 3, 1600L), ("ls", 2, 100L) }, all.Select(g => (g.Family, g.Calls, g.RawBytes)));

        var windowed = _store.Gaps(since);
        Assert.Equal(new[] { ("grep", 1, 100L), ("ls", 1, 40L) }, windowed.Select(g => (g.Family, g.Calls, g.RawBytes)));

        var degraded = _store.Degraded(since);
        var make = Assert.Single(degraded);
        Assert.Equal(1, make.Calls);
        Assert.Equal(30, make.RawBytes);

        // Filing thresholds apply to the windowed totals: grep clears 3 calls
        // over all history but only 1 inside the window.
        Assert.Single(_store.FileIssueGaps(1000, 3));
        Assert.Empty(_store.FileIssueGaps(1000, 3, since));
        Assert.Single(_store.FileIssueGaps(100, 1, since), g => g.Family == "grep");
    }

    [Fact]
    public void LogInvocation_SpoolIdAndGrep_OmittedWhenNull_RoundTripWhenSet()
    {
        // #137: the two new fields never change the shape of a row that
        // does not carry them, and round-trip when they do.
        _store.LogInvocation(new Invocation { Cmd = "git status", RawBytes = 100, OutBytes = 10, Filtered = true });
        _store.LogInvocation(new Invocation { Cmd = "git diff", RawBytes = 5000, OutBytes = 200, Filtered = true, Reason = "git diff", SpoolId = "2e3f" });
        _store.LogInvocation(new Invocation { Cmd = "show 2e3f", RawBytes = 5000, OutBytes = 5000, Reason = Store.ReasonShow, SpoolId = "2e3f", Grep = false });

        var lines = File.ReadAllLines(Path.Combine(_dir, "invocations.jsonl"));
        Assert.DoesNotContain("spool_id", lines[0]);
        Assert.DoesNotContain("grep", lines[0]);
        Assert.Contains("\"spool_id\":\"2e3f\"", lines[1]);
        Assert.DoesNotContain("grep", lines[1]);
        Assert.Contains("\"reason\":\"show\"", lines[2]);
        Assert.Contains("\"grep\":false", lines[2]);

        var rows = _store.Invocations();
        Assert.Null(rows[0].SpoolId);
        Assert.Null(rows[0].Grep);
        Assert.Equal("2e3f", rows[1].SpoolId);
        Assert.Equal("2e3f", rows[2].SpoolId);
        Assert.False(rows[2].Grep);
    }

    public static IEnumerable<object[]> RecoveryCases()
    {
        // minutesAfterFold, expectRecovered
        yield return new object[] { 0.0, true };
        yield return new object[] { 9.9, true };
        yield return new object[] { 10.0, true };
        yield return new object[] { 10.1, false };
        yield return new object[] { -1.0, false }; // show logged before its fold: not a recovery
    }

    [Theory]
    [MemberData(nameof(RecoveryCases))]
    public void Recovered_JoinsShowToFoldOnlyInsideWindow(double minutesAfterFold, bool expectRecovered)
    {
        var t0 = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        _store.LogInvocation(new Invocation { Time = t0, Cmd = "git diff", RawBytes = 9000, OutBytes = 300, Filtered = true, Reason = "git diff", SpoolId = "ab12" });
        _store.LogInvocation(new Invocation { Time = t0.AddMinutes(minutesAfterFold), Cmd = "show ab12", RawBytes = 9000, OutBytes = 9050, Reason = Store.ReasonShow, SpoolId = "ab12", Grep = false });

        var r = Assert.Single(_store.Recovered());
        Assert.Equal("git diff", r.Filter);
        Assert.Equal(1, r.Folds);
        Assert.Equal(expectRecovered ? 1 : 0, r.Recovered);
        Assert.Equal(expectRecovered ? 9050 : 0, r.ShowBytes);
    }

    [Fact]
    public void Recovered_RatesPerFoldIdentity_MostRecentFoldWins_UnjoinedShowsIgnored()
    {
        var t0 = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        // Two git diff folds share an id (same command rerun); the show joins
        // the most recent one only.
        _store.LogInvocation(new Invocation { Time = t0, Cmd = "git diff", RawBytes = 9000, OutBytes = 300, Filtered = true, Reason = "git diff", SpoolId = "ab12" });
        _store.LogInvocation(new Invocation { Time = t0.AddMinutes(1), Cmd = "git diff", RawBytes = 9500, OutBytes = 310, Filtered = true, Reason = "git diff", SpoolId = "ab12" });
        _store.LogInvocation(new Invocation { Time = t0.AddMinutes(2), Cmd = "show ab12", RawBytes = 9500, OutBytes = 9550, Reason = Store.ReasonShow, SpoolId = "ab12", Grep = false });
        // A grep'd second look at the same fold: counted once as recovered, bytes summed.
        _store.LogInvocation(new Invocation { Time = t0.AddMinutes(3), Cmd = "show ab12", RawBytes = 9500, OutBytes = 120, Reason = Store.ReasonShow, SpoolId = "ab12", Grep = true });
        // mocha fold never recovered.
        _store.LogInvocation(new Invocation { Time = t0.AddMinutes(4), Cmd = "mocha --reporter spec", RawBytes = 40000, OutBytes = 900, Filtered = true, Reason = "mocha", SpoolId = "cd34" });
        // npm fold recovered.
        _store.LogInvocation(new Invocation { Time = t0.AddMinutes(5), Cmd = "npm run build", RawBytes = 70000, OutBytes = 500, Filtered = true, Reason = "npm-run-fold", SpoolId = "ef56" });
        _store.LogInvocation(new Invocation { Time = t0.AddMinutes(6), Cmd = "show ef56", RawBytes = 70000, OutBytes = 70000, Reason = Store.ReasonShow, SpoolId = "ef56", Grep = false });
        // Unfiltered banner-strip spool (unrecognized inner tool): identity is the family.
        _store.LogInvocation(new Invocation { Time = t0.AddMinutes(7), Cmd = "tsc --noEmit", RawBytes = 3000, OutBytes = 2600, Reason = Store.ReasonNoFilter, SpoolId = "0a1b" });
        // Show of an id with no fold row (expired / pre-#137 fold): ignored.
        _store.LogInvocation(new Invocation { Time = t0.AddMinutes(8), Cmd = "show 9999", RawBytes = 100, OutBytes = 100, Reason = Store.ReasonShow, SpoolId = "9999", Grep = false });
        // Filtered rows without a spool id are not folds.
        _store.LogInvocation(new Invocation { Time = t0.AddMinutes(9), Cmd = "git status", RawBytes = 100, OutBytes = 90, Filtered = true, Reason = "git status" });

        var got = _store.Recovered();

        Assert.Equal(
            new[]
            {
                ("git diff", 2, 1, 9670L),
                ("npm-run-fold", 1, 1, 70000L),
                ("mocha", 1, 0, 0L),
                ("tsc", 1, 0, 0L),
            },
            got.Select(r => (r.Filter, r.Folds, r.Recovered, r.ShowBytes)));
    }

    [Fact]
    public void Recovered_Since_WindowsOnTheFoldRow()
    {
        var since = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        _store.LogInvocation(new Invocation { Time = since.AddMinutes(-5), Cmd = "git diff", RawBytes = 9000, OutBytes = 300, Filtered = true, Reason = "git diff", SpoolId = "ab12" });
        _store.LogInvocation(new Invocation { Time = since.AddMinutes(1), Cmd = "show ab12", RawBytes = 9000, OutBytes = 9000, Reason = Store.ReasonShow, SpoolId = "ab12", Grep = false });
        _store.LogInvocation(new Invocation { Time = since.AddMinutes(2), Cmd = "mocha", RawBytes = 40000, OutBytes = 900, Filtered = true, Reason = "mocha", SpoolId = "cd34" });

        Assert.Equal(2, _store.Recovered().Count);
        var windowed = Assert.Single(_store.Recovered(since));
        Assert.Equal("mocha", windowed.Filter);
    }

    [Fact]
    public void Gain_NetSubtractsRecoveredShowBytes_AndShowRowsNeverCountInGross()
    {
        var t0 = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        _store.LogInvocation(new Invocation { Time = t0, Cmd = "git diff", RawBytes = 9000, OutBytes = 300, Filtered = true, Reason = "git diff", SpoolId = "ab12" });
        _store.LogInvocation(new Invocation { Time = t0.AddMinutes(1), Cmd = "show ab12", RawBytes = 9000, OutBytes = 9050, Reason = Store.ReasonShow, SpoolId = "ab12", Grep = false });
        _store.LogInvocation(new Invocation { Time = t0.AddMinutes(2), Cmd = "mocha", RawBytes = 40000, OutBytes = 900, Filtered = true, Reason = "mocha", SpoolId = "cd34" });
        // Late show: outside the window, so not a recovery — and still not gross.
        _store.LogInvocation(new Invocation { Time = t0.AddMinutes(30), Cmd = "show cd34", RawBytes = 40000, OutBytes = 40000, Reason = Store.ReasonShow, SpoolId = "cd34", Grep = false });

        var report = _store.Gain();

        // Gross: exactly the two wrap rows, as before #137.
        Assert.Equal(2, report.Calls);
        Assert.Equal(49000, report.RawBytes);
        Assert.Equal(1200, report.OutBytes);
        Assert.Equal(47800, report.Saved);
        // Net: gross minus the joined show's emitted bytes.
        Assert.Equal(9050, report.Recovered);
        Assert.Equal(1, report.Recoveries);
        Assert.Equal(47800 - 9050, report.Net);
        var git = Assert.Single(report.Families, f => f.Family == "git");
        Assert.Equal(8700, git.Saved);
        Assert.Equal(9050, git.Recovered);
        Assert.Equal(-350, git.Net); // a recovered fold is a net loss: the round trip cost more than it saved
        var mocha = Assert.Single(report.Families, f => f.Family == "mocha");
        Assert.Equal(39100, mocha.Net);
        Assert.DoesNotContain(report.Families, f => f.Family == "show");

        // Daily / history share the countable gate: no show rows.
        Assert.Equal(2, Assert.Single(_store.Daily()).Calls);
        Assert.DoesNotContain(_store.Recent(10), inv => inv.Reason == Store.ReasonShow);
    }

    [Fact]
    public void Gaps_ExcludeShowRows()
    {
        _store.LogInvocation(new Invocation { Cmd = "show ab12", RawBytes = 9000, OutBytes = 9000, Reason = Store.ReasonShow, SpoolId = "ab12", Grep = false });
        _store.LogInvocation(new Invocation { Cmd = "ls -la", RawBytes = 100, Reason = Store.ReasonNoFilter });

        var ls = Assert.Single(_store.Gaps());
        Assert.Equal("ls", ls.Family);
        Assert.Empty(_store.Degraded());
        Assert.Single(_store.FileIssueGaps(1, 1));
    }

    [Fact]
    public void Sweep_RemovesEntriesOlderThanTtl()
    {
        var id = _store.Write(Cwd, new[] { "git", "status" }, "raw", DateTime.UtcNow);
        var spoolPath = Path.Combine(_dir, "spool", id + ".txt");
        File.SetLastWriteTimeUtc(spoolPath, DateTime.UtcNow.AddHours(-2));

        _store.Sweep(Store.DefaultTTL, DateTime.UtcNow);

        Assert.False(File.Exists(spoolPath));
    }
}
