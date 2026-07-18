using Vtk.Cli;

namespace Vtk.Tests.Learn;

/// <summary>
/// End-to-end `vtk learn` against a temp sessions directory of fixture
/// transcripts (#43): rules-file emission, thresholds, --dry-run, and the
/// exit-code convention (0 success, 1 environment, 2 usage).
/// </summary>
public class LearnCliTests : IDisposable
{
    private static readonly string FixtureDir = Path.Combine(
        AppContext.BaseDirectory, "Session", "testdata");

    private readonly string _root;
    private readonly string _sessions;
    private readonly string _out;

    public LearnCliTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "vtk-learn-test-" + Guid.NewGuid().ToString("N"));
        _sessions = Path.Combine(_root, "sessions");
        _out = Path.Combine(_root, "rules", "cli-corrections.md");
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
        var code = Vtk.Cli.Learn.Run(args, so, se);
        stdout = so.ToString();
        stderr = se.ToString();
        return code;
    }

    [Fact]
    public void Run_MinesRulesAcrossSessions_WritesFile()
    {
        // The same correction observed in two sessions -> occurrences 2.
        CopyFixture("session_basic.jsonl", "a.jsonl");
        CopyFixture("session_basic.jsonl", "b.jsonl");
        CopyFixture("session_flaky.jsonl", "c.jsonl");

        var code = Run(out var stdout, out _, "--sessions", _sessions, "--out", _out);

        Assert.Equal(0, code);
        Assert.Contains("wrote 2 rules", stdout);
        var content = File.ReadAllText(_out);
        Assert.Contains("- `git satus` → `git status` — command-not-found, seen 2x, confidence 0.75", content);
        Assert.Contains("- `npm run lint --ext .ts` → `npm run lint -- --ext .ts` — unknown-flag, seen 1x, confidence 0.50", content);
        // The flaky identical dotnet retry must not become a rule.
        Assert.DoesNotContain("dotnet test", content);
    }

    [Fact]
    public void Run_ThresholdsFilterRules()
    {
        CopyFixture("session_basic.jsonl", "a.jsonl");
        CopyFixture("session_flaky.jsonl", "b.jsonl");

        // git pair and npm pair are singletons; --min-occurrences 2 drops both.
        var code = Run(out var stdout, out _, "--sessions", _sessions, "--out", _out, "--min-occurrences", "2");

        Assert.Equal(0, code);
        Assert.Contains("no correction rules mined", stdout);
        Assert.False(File.Exists(_out));
    }

    [Fact]
    public void Run_DryRun_PrintsWithoutWriting()
    {
        CopyFixture("session_basic.jsonl", "a.jsonl");

        var code = Run(out var stdout, out _, "--sessions", _sessions, "--out", _out, "--dry-run");

        Assert.Equal(0, code);
        Assert.Contains("- `git satus` → `git status`", stdout);
        Assert.Contains("dry-run", stdout);
        Assert.False(File.Exists(_out));
    }

    [Fact]
    public void Run_MissingSessionsDir_EnvFailure()
    {
        var code = Run(out _, out var stderr,
            "--sessions", Path.Combine(_root, "nope"), "--out", _out);

        Assert.Equal(1, code);
        Assert.Contains("no session transcripts", stderr);
    }

    public static IEnumerable<object[]> UsageErrorCases()
    {
        yield return new object[] { new[] { "--bogus" } };
        yield return new object[] { new[] { "--min-confidence" } };
        yield return new object[] { new[] { "--min-confidence", "2" } };
        yield return new object[] { new[] { "--min-confidence", "abc" } };
        yield return new object[] { new[] { "--min-occurrences", "0" } };
        yield return new object[] { new[] { "--sessions" } };
        yield return new object[] { new[] { "--out" } };
    }

    [Theory]
    [MemberData(nameof(UsageErrorCases))]
    public void Run_UsageErrors_Exit2(string[] args)
    {
        Assert.Equal(2, Run(out _, out _, args));
    }
}
