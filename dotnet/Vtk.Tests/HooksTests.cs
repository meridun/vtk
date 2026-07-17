using System.Text.Json;
using System.Text.Json.Nodes;
using Vtk.Cli;
using Xunit;

namespace Vtk.Tests;

/// <summary>
/// Tests for `vtk hooks` (#45, Claude-Code-only MVP). All assertions run
/// against the pure JSON cores — no real ~/.claude/settings.json, no process
/// spawning. Init/uninstall mirror the InstallTests idempotency contract;
/// rewrite is table-driven fixture JSON in → hook output (or passthrough) out.
/// </summary>
public class HooksTests
{
    private const string Exe = @"C:\tools\vtk\vtk.exe";
    private static readonly string HookCmd = Hooks.RenderHookCommand(Exe);

    // ---------------------------------------------------------------- init

    [Fact]
    public void InitJson_FreshFile_InstallsSingleEntry()
    {
        var got = Hooks.InitJson("", HookCmd);

        var cmds = Hooks.FindVtkCommands(got);
        Assert.Single(cmds);
        Assert.Equal(HookCmd, cmds[0]);

        var root = JsonNode.Parse(got)!.AsObject();
        var pre = root["hooks"]!["PreToolUse"]!.AsArray();
        Assert.Single(pre);
        Assert.Equal("Bash", pre[0]!["matcher"]!.GetValue<string>());
        Assert.Equal("command", pre[0]!["hooks"]![0]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void InitJson_IsIdempotent()
    {
        var once = Hooks.InitJson("", HookCmd);
        var twice = Hooks.InitJson(once, HookCmd);
        Assert.Equal(once, twice);
        Assert.Single(Hooks.FindVtkCommands(twice));
    }

    [Fact]
    public void InitJson_ReplacesStalePathEntry()
    {
        var stale = Hooks.InitJson("", Hooks.RenderHookCommand(@"C:\old\vtk.exe"));
        var updated = Hooks.InitJson(stale, HookCmd);

        var cmds = Hooks.FindVtkCommands(updated);
        Assert.Single(cmds);
        Assert.Equal(HookCmd, cmds[0]);
        Assert.DoesNotContain(@"old", updated);
    }

    [Fact]
    public void InitJson_PreservesUnrelatedSettingsAndHooks()
    {
        const string existing = """
        {
          "model": "opus",
          "hooks": {
            "PreToolUse": [
              { "matcher": "Write", "hooks": [ { "type": "command", "command": "other-tool check" } ] }
            ],
            "PostToolUse": [
              { "matcher": "Bash", "hooks": [ { "type": "command", "command": "logger" } ] }
            ]
          }
        }
        """;
        var got = Hooks.InitJson(existing, HookCmd);

        var root = JsonNode.Parse(got)!.AsObject();
        Assert.Equal("opus", root["model"]!.GetValue<string>());
        Assert.Single(root["hooks"]!["PostToolUse"]!.AsArray());

        var pre = root["hooks"]!["PreToolUse"]!.AsArray();
        Assert.Equal(2, pre.Count);
        Assert.Equal("other-tool check", pre[0]!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.Single(Hooks.FindVtkCommands(got));
    }

    [Fact]
    public void InitJson_MalformedJson_Throws()
    {
        Assert.ThrowsAny<JsonException>(() => Hooks.InitJson("{not json", HookCmd));
        Assert.ThrowsAny<JsonException>(() => Hooks.InitJson("[1,2]", HookCmd));
    }

    // ----------------------------------------------------------- uninstall

    [Fact]
    public void UninstallJson_RemovesExactlyOurs()
    {
        var installed = Hooks.InitJson("""{ "model": "opus" }""", HookCmd);
        var (res, found) = Hooks.UninstallJson(installed);

        Assert.True(found);
        Assert.Empty(Hooks.FindVtkCommands(res));
        var root = JsonNode.Parse(res)!.AsObject();
        Assert.Equal("opus", root["model"]!.GetValue<string>());
        // No empty hooks husk left behind.
        Assert.Null(root["hooks"]);
    }

    [Fact]
    public void UninstallJson_LeavesOtherHooksIntact()
    {
        const string existing = """
        {
          "hooks": {
            "PreToolUse": [
              { "matcher": "Write", "hooks": [ { "type": "command", "command": "other-tool check" } ] }
            ]
          }
        }
        """;
        var installed = Hooks.InitJson(existing, HookCmd);
        var (res, found) = Hooks.UninstallJson(installed);

        Assert.True(found);
        var pre = JsonNode.Parse(res)!["hooks"]!["PreToolUse"]!.AsArray();
        Assert.Single(pre);
        Assert.Equal("other-tool check", pre[0]!["hooks"]![0]!["command"]!.GetValue<string>());
    }

    [Fact]
    public void UninstallJson_NotPresent_IsHonest()
    {
        const string existing = """{ "model": "opus" }""";
        var (res, found) = Hooks.UninstallJson(existing);
        Assert.False(found);
        Assert.Equal(existing, res); // untouched, not even reformatted
    }

    // -------------------------------------------------------------- verify

    private static readonly Func<string, bool> AllExist = _ => true;

    [Fact]
    public void VerifyJson_HappyPath()
    {
        var json = Hooks.InitJson("", HookCmd);
        Assert.Empty(Hooks.VerifyJson(json, Exe, AllExist));
    }

    [Theory]
    [InlineData("{}", "no vtk PreToolUse hook installed")]
    [InlineData("""{ "hooks": { "PreToolUse": [] } }""", "no vtk PreToolUse hook installed")]
    [InlineData("{oops", "not valid JSON")]
    public void VerifyJson_MissingOrBroken(string json, string wantSubstring)
    {
        var problems = Hooks.VerifyJson(json, Exe, AllExist);
        Assert.Contains(problems, p => p.Contains(wantSubstring));
    }

    [Fact]
    public void VerifyJson_DetectsDesync()
    {
        var json = Hooks.InitJson("", Hooks.RenderHookCommand(@"C:\old\vtk.exe"));
        var problems = Hooks.VerifyJson(json, Exe, AllExist);
        Assert.Contains(problems, p => p.Contains("desync"));
    }

    [Fact]
    public void VerifyJson_DetectsMissingBinary()
    {
        var json = Hooks.InitJson("", HookCmd);
        var problems = Hooks.VerifyJson(json, Exe, _ => false);
        Assert.Contains(problems, p => p.Contains("hook binary missing"));
    }

    [Fact]
    public void VerifyJson_DetectsDuplicates()
    {
        // Hand-build a settings file with two vtk entries (init can never
        // produce this; a hand-edit can).
        var one = Hooks.InitJson("", HookCmd);
        var root = JsonNode.Parse(one)!.AsObject();
        var pre = root["hooks"]!["PreToolUse"]!.AsArray();
        pre.Add(pre[0]!.DeepClone());
        var json = root.ToJsonString();

        var problems = Hooks.VerifyJson(json, Exe, AllExist);
        Assert.Contains(problems, p => p.Contains("expected 1"));
    }

    [Fact]
    public void VerifyJson_DetectsWrongMatcher()
    {
        var root = JsonNode.Parse(Hooks.InitJson("", HookCmd))!.AsObject();
        root["hooks"]!["PreToolUse"]![0]!["matcher"] = "Write";
        var problems = Hooks.VerifyJson(root.ToJsonString(), Exe, AllExist);
        Assert.Contains(problems, p => p.Contains("matcher"));
    }

    [Fact]
    public void ExtractExe_HandlesQuotedAndBare()
    {
        Assert.Equal(Exe, Hooks.ExtractExe(HookCmd));
        Assert.Equal("/usr/bin/vtk", Hooks.ExtractExe("/usr/bin/vtk hooks rewrite"));
        Assert.Equal("", Hooks.ExtractExe("something else entirely"));
    }

    // ------------------------------------------------------------- rewrite

    private static string HookInput(string tool, string command) =>
        new JsonObject
        {
            ["tool_name"] = tool,
            ["tool_input"] = new JsonObject { ["command"] = command },
        }.ToJsonString();

    [Theory]
    [InlineData("git status")]
    [InlineData("gh pr list --limit 5")]
    [InlineData("npm test")]
    [InlineData("  git log --oneline -5  ")] // surrounding whitespace trimmed
    public void RewriteToolCall_WrapsPlainFamilyCommands(string command)
    {
        var got = Hooks.RewriteToolCall(HookInput("Bash", command), Exe);
        Assert.NotNull(got);

        var root = JsonNode.Parse(got!)!.AsObject();
        var hso = root["hookSpecificOutput"]!.AsObject();
        Assert.Equal("PreToolUse", hso["hookEventName"]!.GetValue<string>());
        // The hook must never decide permissions — only update the input.
        Assert.Null(hso["permissionDecision"]);

        var updated = hso["updatedInput"]!["command"]!.GetValue<string>();
        var prefix = Hooks.BashQuote(OperatingSystem.IsWindows() ? Install.WinToMsys(Exe) : Exe);
        Assert.Equal(prefix + " " + command.Trim(), updated);
    }

    [Theory]
    [InlineData("git log | head -3")] // pipe: downstream expects raw bytes
    [InlineData("git diff > out.txt")] // redirect: file must get raw bytes
    [InlineData("git status && git diff")] // chaining
    [InlineData("git checkout $BRANCH")] // expansion
    [InlineData("git tag `date +%Y`")] // substitution
    [InlineData("git status; ls")]
    [InlineData("ls -la")] // uncovered family
    [InlineData("vtk git status")] // already wrapped
    [InlineData("'/c/tools/vtk/vtk.exe' git status")] // already wrapped, full path
    [InlineData("gitk")] // prefix, not the git family
    [InlineData("")]
    public void RewriteToolCall_PassesThroughIneligibleCommands(string command)
    {
        Assert.Null(Hooks.RewriteToolCall(HookInput("Bash", command), Exe));
    }

    [Theory]
    [InlineData("""{"tool_name":"Write","tool_input":{"command":"git status"}}""")] // not Bash
    [InlineData("""{"tool_name":"Bash","tool_input":{}}""")] // no command
    [InlineData("""{"tool_name":"Bash"}""")] // no tool_input
    [InlineData("not json at all")]
    [InlineData("")]
    public void RewriteToolCall_PassesThroughUnsupportedInput(string inputJson)
    {
        Assert.Null(Hooks.RewriteToolCall(inputJson, Exe));
    }

    [Fact]
    public void RewriteToolCall_PreservesOtherToolInputFields()
    {
        const string input = """
        {"tool_name":"Bash","tool_input":{"command":"git status","timeout":5000,"description":"Show status"}}
        """;
        var got = Hooks.RewriteToolCall(input, Exe);
        Assert.NotNull(got);
        var updated = JsonNode.Parse(got!)!["hookSpecificOutput"]!["updatedInput"]!.AsObject();
        Assert.Equal(5000, updated["timeout"]!.GetValue<int>());
        Assert.Equal("Show status", updated["description"]!.GetValue<string>());
    }

    [Fact]
    public void BashQuote_EscapesSingleQuotes()
    {
        Assert.Equal("'/c/a b/vtk.exe'", Hooks.BashQuote("/c/a b/vtk.exe"));
        Assert.Equal(@"'/c/o'\''brien/vtk'", Hooks.BashQuote("/c/o'brien/vtk"));
    }
}
