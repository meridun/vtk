using Xunit;

namespace Vtk.Tests.Smoke;

/// <summary>
/// Real-binary smoke for `vtk learn` (#43): exercises the published vtk.exe
/// against a scratch sessions directory of fixture transcripts — rules-file
/// write and overwrite, --dry-run, thresholds, and the exit-code convention
/// (0 success, 1 environment, 2 usage).
/// </summary>
[Collection("Smoke")]
public class LearnSmokeTests : IDisposable
{
    private static readonly string FixtureDir = Path.Combine(
        AppContext.BaseDirectory, "Session", "testdata");

    private readonly SmokeHarness _h = new();
    private readonly string _sessions;
    private readonly string _out;

    public LearnSmokeTests()
    {
        _sessions = Path.Combine(_h.Repo, "sessions");
        _out = Path.Combine(_h.Repo, "rules", "cli-corrections.md");
        Directory.CreateDirectory(_sessions);
    }

    public void Dispose() => _h.Dispose();

    private void CopyFixture(string name, string asName) =>
        File.Copy(Path.Combine(FixtureDir, name), Path.Combine(_sessions, asName));

    [Fact]
    public void LearnWorkflow()
    {
        CopyFixture("session_basic.jsonl", "a.jsonl");
        CopyFixture("session_basic.jsonl", "b.jsonl");
        CopyFixture("session_flaky.jsonl", "c.jsonl");

        // dry-run prints rules, writes nothing
        var (dryOut, _, dryCode) = _h.Run(_h.Repo, "learn", "--dry-run", "--sessions", _sessions, "--out", _out);
        Assert.Equal(0, dryCode);
        // Assertions stay ASCII-only: the harness decodes child stdout with the
        // default console encoding, which mangles the renderer's arrow/dash glyphs.
        Assert.Contains("`git satus`", dryOut);
        Assert.Contains("`git status`", dryOut);
        Assert.Contains("command-not-found, seen 2x, confidence 0.75", dryOut);
        Assert.Contains("dry-run", dryOut);
        Assert.False(File.Exists(_out));

        // real write emits the rules file
        var (wrOut, _, wrCode) = _h.Run(_h.Repo, "learn", "--sessions", _sessions, "--out", _out);
        Assert.Equal(0, wrCode);
        Assert.Contains("wrote 2 rules", wrOut);
        var content = File.ReadAllText(_out);
        Assert.Contains("`git satus` → `git status`", content); // file is read as UTF-8, glyphs intact
        // the flaky identical retry must not become a rule
        Assert.DoesNotContain("dotnet test", content);

        // re-run with a threshold overwrites the previous file
        var (ovOut, _, ovCode) = _h.Run(_h.Repo, "learn", "--sessions", _sessions, "--out", _out, "--min-occurrences", "2");
        Assert.Equal(0, ovCode);
        Assert.Contains("wrote 1 rules", ovOut);
        var overwritten = File.ReadAllText(_out);
        Assert.Contains("`git satus` → `git status`", overwritten);
        Assert.DoesNotContain("npm run lint", overwritten);

        // missing sessions dir is an environment failure: exit 1
        var (_, envErr, envCode) = _h.Run(_h.Repo, "learn", "--sessions", Path.Combine(_h.Repo, "nope"), "--out", _out);
        Assert.Equal(1, envCode);
        Assert.Contains("no session transcripts", envErr);

        // unknown flag is a usage failure: exit 2
        var (_, useErr, useCode) = _h.Run(_h.Repo, "learn", "--bogus");
        Assert.Equal(2, useCode);
        Assert.Contains("usage: vtk learn", useErr);
    }
}
