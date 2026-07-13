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
    public void Sweep_RemovesEntriesOlderThanTtl()
    {
        var id = _store.Write(new[] { "git", "status" }, "raw", DateTime.UtcNow);
        var spoolPath = Path.Combine(_dir, "spool", id + ".txt");
        File.SetLastWriteTimeUtc(spoolPath, DateTime.UtcNow.AddHours(-2));

        _store.Sweep(Store.DefaultTTL, DateTime.UtcNow);

        Assert.False(File.Exists(spoolPath));
    }
}
