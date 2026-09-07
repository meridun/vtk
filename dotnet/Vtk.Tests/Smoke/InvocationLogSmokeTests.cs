using System.Text.Json;
using Xunit;

namespace Vtk.Tests.Smoke;

/// <summary>
/// End-to-end smoke for #155: concurrent vtk processes appending to the same
/// <c>invocations.jsonl</c> must not lose telemetry rows to a Windows sharing
/// violation. Runs a herd of real vtk invocations against one isolated home
/// and requires exactly one parseable row per invocation, no
/// <c>vtk: gap log failed:</c> on stderr, and unchanged exit codes. Earlier
/// concurrent smokes assert <c>&gt;= 1</c> rows because the pre-#155 append
/// dropped ~1 in 10; this one asserts <c>== N</c>.
/// </summary>
[Collection("Smoke")]
public class InvocationLogSmokeTests : IDisposable
{
    private readonly SmokeHarness _h = new();
    public void Dispose() => _h.Dispose();

    private const string GapLogFailed = "vtk: gap log failed:";

    private string[] Rows()
    {
        var path = Path.Combine(_h.Home, "vtk", "invocations.jsonl");
        if (!File.Exists(path)) return Array.Empty<string>();
        return File.ReadAllLines(path).Where(l => l.Length > 0).ToArray();
    }

    private static string Cmd(string row)
    {
        using var doc = JsonDocument.Parse(row); // throws on a clobbered/partial row
        return doc.RootElement.GetProperty("cmd").GetString()!;
    }

    [Fact]
    public async Task ConcurrentGitStatusHerdLogsEveryRow()
    {
        const int n = 8;
        File.WriteAllText(Path.Combine(_h.Repo, "untracked.txt"), "x\n");

        var tasks = Enumerable.Range(0, n)
            .Select(_ => Task.Run(() => _h.Run(_h.Repo, "git", "status")))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        foreach (var r in results)
        {
            Assert.Equal(0, r.code); // parity: raw `git status` exits 0
            Assert.DoesNotContain(GapLogFailed, r.stderr);
            // Spool replace-race losers degrade to raw passthrough (#125);
            // either way the output is complete.
            Assert.True(r.stdout.Contains("OK ") || r.stdout.Contains("untracked.txt"), "incomplete output:\n" + r.stdout);
        }

        var rows = Rows();
        Assert.Equal(n, rows.Length);
        Assert.All(rows, row => Assert.Equal("git status", Cmd(row)));
    }

    [Fact]
    public async Task ConcurrentNpmTestPairLogsBothRows()
    {
        // The #134 shape that surfaced the bug: two `vtk npm test` at once.
        var env = new Dictionary<string, string> { ["VTK_FAKE_MOCHA_CODE"] = "1" };
        var t1 = Task.Run(() => _h.RunFaked(_h.Repo, env, "npm", "test"));
        var t2 = Task.Run(() => _h.RunFaked(_h.Repo, env, "npm", "test"));
        var results = await Task.WhenAll(t1, t2);

        foreach (var r in results)
        {
            Assert.Equal(1, r.code); // parity through the npm dispatch layer
            Assert.DoesNotContain(GapLogFailed, r.stderr);
        }

        var rows = Rows();
        Assert.Equal(2, rows.Length);
        // #153: the inner filter owns attribution, so the row names mocha.
        Assert.All(rows, row => Assert.Contains("mocha", Cmd(row)));
    }
}
