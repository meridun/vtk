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

    [Fact]
    public void WriteThenRead_RoundTrips()
    {
        var argv = new[] { "git", "status" };
        var id = _store.Write(argv, "some raw output\n", DateTime.UtcNow);
        var content = _store.Read(id);
        Assert.Contains("# vtk spool", content);
        Assert.Contains("git status", content);
        Assert.Contains("some raw output", content);
    }

    [Fact]
    public void Write_RedactsSecrets()
    {
        var id = _store.Write(new[] { "curl" }, "Authorization: Bearer sekret123\n", DateTime.UtcNow);
        var content = _store.Read(id);
        Assert.DoesNotContain("sekret123", content);
        Assert.Contains("[REDACTED]", content);
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

    [Fact]
    public void Sweep_RemovesEntriesOlderThanTtl()
    {
        var id = _store.Write(new[] { "git", "status" }, "raw", DateTime.UtcNow);
        var spoolPath = Path.Combine(_dir, "spool", id + ".txt");
        File.SetLastWriteTimeUtc(spoolPath, DateTime.UtcNow.AddHours(-2));

        _store.Sweep(Store.DefaultTTL, DateTime.UtcNow);

        Assert.False(File.Exists(spoolPath));
    }
}
