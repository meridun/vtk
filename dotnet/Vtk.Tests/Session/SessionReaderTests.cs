using Vtk.Core.Session;

namespace Vtk.Tests.Session;

/// <summary>
/// Fixture-driven tests for the Claude Code session JSONL reader (#43):
/// tool_use/tool_result pairing, both result-content shapes, and silent
/// tolerance of noise (summaries, other tools, malformed lines, dangling
/// tool uses).
/// </summary>
public class SessionReaderTests
{
    private static readonly string FixtureDir = Path.Combine(
        AppContext.BaseDirectory, "Session", "testdata");

    [Fact]
    public void ReadCommands_BasicFixture_PairsBashUseWithResult()
    {
        var events = SessionReader.ReadCommands(
            File.ReadLines(Path.Combine(FixtureDir, "session_basic.jsonl")));

        Assert.Equal(2, events.Count);

        Assert.Equal("git satus", events[0].Command);
        Assert.True(events[0].IsError);
        Assert.Contains("'satus' is not a git command", events[0].Output);

        Assert.Equal("git status", events[1].Command);
        Assert.False(events[1].IsError);
        // Array-of-text content flattens to newline-joined text.
        Assert.Equal("On branch dev\nnothing to commit, working tree clean", events[1].Output);
    }

    [Fact]
    public void ReadCommands_FlakyFixture_ReadsStringAndErrorShapes()
    {
        var events = SessionReader.ReadCommands(
            File.ReadLines(Path.Combine(FixtureDir, "session_flaky.jsonl")));

        Assert.Equal(4, events.Count);
        Assert.Equal("npm run lint --ext .ts", events[0].Command);
        Assert.True(events[0].IsError);
        Assert.Equal("lint clean", events[1].Output); // plain-string content
        Assert.True(events[2].IsError);
        Assert.False(events[3].IsError);
    }

    public static IEnumerable<object[]> NoiseCases()
    {
        // lines, expectedCount — every shape the reader must skip silently
        yield return new object[] { new[] { "" }, 0 };
        yield return new object[] { new[] { "42" }, 0 };
        yield return new object[] { new[] { "{\"type\":\"summary\",\"summary\":\"s\"}" }, 0 };
        // result with no matching tool_use
        yield return new object[] { new[] { "{\"message\":{\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"toolu_99\",\"content\":\"x\"}]}}" }, 0 };
        // tool_use never resolved
        yield return new object[] { new[] { "{\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"toolu_1\",\"name\":\"Bash\",\"input\":{\"command\":\"ls\"}}]}}" }, 0 };
        // non-Bash tool_use + its result
        yield return new object[] { new[] {
            "{\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"toolu_1\",\"name\":\"Glob\",\"input\":{\"command\":\"ls\"}}]}}",
            "{\"message\":{\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"toolu_1\",\"content\":\"x\"}]}}" }, 0 };
    }

    [Theory]
    [MemberData(nameof(NoiseCases))]
    public void ReadCommands_SkipsNoise(string[] lines, int expected)
    {
        Assert.Equal(expected, SessionReader.ReadCommands(lines).Count);
    }
}

/// <summary>Session locator tests: project-path munging and file listing.</summary>
public class SessionProviderTests
{
    public static IEnumerable<object[]> MungeCases()
    {
        // projectPath, expectedDirName (Claude Code scheme: non-alphanumeric -> '-')
        yield return new object[] { @"C:\Claude\vtk", "C--Claude-vtk" };
        yield return new object[] { "/home/u/proj.x", "-home-u-proj-x" };
        yield return new object[] { "/home/u/my_proj", "-home-u-my-proj" };
        yield return new object[] { "plain", "plain" };
    }

    [Theory]
    [MemberData(nameof(MungeCases))]
    public void MungeProjectPath_MatchesClaudeCodeScheme(string path, string expected)
    {
        Assert.Equal(expected, SessionProvider.MungeProjectPath(path));
    }

    [Fact]
    public void SessionFiles_MissingDir_ReturnsEmpty()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vtk-test-nonexistent-" + Guid.NewGuid().ToString("N"));
        Assert.Empty(SessionProvider.SessionFiles(dir));
    }

    [Fact]
    public void SessionFiles_ListsOnlyJsonl()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vtk-test-sessions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.jsonl"), "");
            File.WriteAllText(Path.Combine(dir, "b.jsonl"), "");
            File.WriteAllText(Path.Combine(dir, "notes.txt"), "");
            var files = SessionProvider.SessionFiles(dir);
            Assert.Equal(2, files.Count);
            Assert.All(files, f => Assert.EndsWith(".jsonl", f));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
