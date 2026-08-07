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
    [InlineData("winget list")]
    [InlineData("choco list --local-only")]
    [InlineData("reg query HKCU\\Environment")]
    [InlineData("grep -rn needle src")] // POSIX utilities: bash-eligible (#114)
    [InlineData("ls -la")]
    [InlineData("find . -name '*.cs'")]
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
    [InlineData("grep err log.txt | wc -l")] // pipe: folded output would corrupt the count
    [InlineData("ls -la > listing.txt")] // redirect on a POSIX-utility family
    [InlineData("cat notes.txt")] // uncovered family
    [InlineData("findstr needle *.cs")] // prefix, not the find family
    [InlineData("vtk git status")] // already wrapped
    [InlineData("'/c/tools/vtk/vtk.exe' git status")] // already wrapped, full path
    [InlineData("gitk")] // prefix, not the git family
    [InlineData("regedit")] // prefix, not the reg family
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

    // ------------------------------------------------------ copilot config

    [Fact]
    public void CopilotConfigJson_RendersDocumentedShape()
    {
        var got = Hooks.CopilotConfigJson(Exe);

        var root = JsonNode.Parse(got)!.AsObject();
        Assert.Equal(1, root["version"]!.GetValue<int>());
        var pre = root["hooks"]!["preToolUse"]!.AsArray();
        Assert.Single(pre);
        var entry = pre[0]!.AsObject();
        Assert.Equal("command", entry["type"]!.GetValue<string>());
        Assert.Equal("bash|powershell", entry["matcher"]!.GetValue<string>());
        Assert.Equal($"\"{Exe}\" hooks rewrite --copilot", entry["bash"]!.GetValue<string>());
        Assert.Equal($"& \"{Exe}\" hooks rewrite --copilot", entry["powershell"]!.GetValue<string>());
    }

    [Fact]
    public void CopilotConfigJson_IsDeterministic()
    {
        // File-level idempotency rides on byte-identical re-renders.
        Assert.Equal(Hooks.CopilotConfigJson(Exe), Hooks.CopilotConfigJson(Exe));
    }

    // ------------------------------------------------------ copilot verify

    [Fact]
    public void VerifyCopilotJson_HappyPath()
    {
        Assert.Empty(Hooks.VerifyCopilotJson(Hooks.CopilotConfigJson(Exe), Exe, AllExist));
    }

    [Theory]
    [InlineData("{oops", "not valid JSON")]
    [InlineData("{}", "version is missing")]
    [InlineData("""{ "version": 2, "hooks": { "preToolUse": [] } }""", "version is 2")]
    [InlineData("""{ "version": 1 }""", "no vtk preToolUse hook entry")]
    [InlineData("""{ "version": 1, "hooks": { "preToolUse": [] } }""", "no vtk preToolUse hook entry")]
    [InlineData("""{ "version": 1, "hooks": { "preToolUse": [ { "type": "command", "bash": "other-tool check" } ] } }""", "no vtk preToolUse hook entry")]
    public void VerifyCopilotJson_MissingOrBroken(string json, string wantSubstring)
    {
        var problems = Hooks.VerifyCopilotJson(json, Exe, AllExist);
        Assert.Contains(problems, p => p.Contains(wantSubstring));
    }

    [Fact]
    public void VerifyCopilotJson_DetectsDesync()
    {
        var json = Hooks.CopilotConfigJson(@"C:\old\vtk.exe");
        var problems = Hooks.VerifyCopilotJson(json, Exe, AllExist);
        Assert.Contains(problems, p => p.Contains("desync"));
    }

    [Fact]
    public void VerifyCopilotJson_DetectsMissingBinary()
    {
        var json = Hooks.CopilotConfigJson(Exe);
        var problems = Hooks.VerifyCopilotJson(json, Exe, _ => false);
        Assert.Contains(problems, p => p.Contains("hook binary missing"));
    }

    [Fact]
    public void VerifyCopilotJson_DetectsDuplicates()
    {
        var root = JsonNode.Parse(Hooks.CopilotConfigJson(Exe))!.AsObject();
        var pre = root["hooks"]!["preToolUse"]!.AsArray();
        pre.Add(pre[0]!.DeepClone());
        var problems = Hooks.VerifyCopilotJson(root.ToJsonString(), Exe, AllExist);
        Assert.Contains(problems, p => p.Contains("expected 1"));
    }

    [Fact]
    public void VerifyCopilotJson_DetectsWrongMatcher()
    {
        var root = JsonNode.Parse(Hooks.CopilotConfigJson(Exe))!.AsObject();
        root["hooks"]!["preToolUse"]![0]!["matcher"] = "bash";
        var problems = Hooks.VerifyCopilotJson(root.ToJsonString(), Exe, AllExist);
        Assert.Contains(problems, p => p.Contains("matcher"));
    }

    [Fact]
    public void VerifyCopilotJson_DetectsMissingShellField()
    {
        var root = JsonNode.Parse(Hooks.CopilotConfigJson(Exe))!.AsObject();
        root["hooks"]!["preToolUse"]![0]!.AsObject().Remove("powershell");
        var problems = Hooks.VerifyCopilotJson(root.ToJsonString(), Exe, AllExist);
        Assert.Contains(problems, p => p.Contains("no \"powershell\" command"));
    }

    [Fact]
    public void ExtractCopilotExe_HandlesShellForms()
    {
        Assert.Equal(Exe, Hooks.ExtractCopilotExe($"\"{Exe}\" hooks rewrite --copilot"));
        Assert.Equal(Exe, Hooks.ExtractCopilotExe($"& \"{Exe}\" hooks rewrite --copilot"));
        Assert.Equal(Exe, Hooks.ExtractCopilotExe($"& '{Exe}' hooks rewrite --copilot"));
        Assert.Equal("/usr/bin/vtk", Hooks.ExtractCopilotExe("/usr/bin/vtk hooks rewrite --copilot"));
        Assert.Equal("", Hooks.ExtractCopilotExe(HookCmd)); // Claude marker, not ours
        Assert.Equal("", Hooks.ExtractCopilotExe("something else entirely"));
    }

    // ----------------------------------------------------- copilot rewrite

    private static string CopilotInput(string tool, string command) =>
        new JsonObject
        {
            ["sessionId"] = "s-1",
            ["timestamp"] = 1752760000000,
            ["cwd"] = @"C:\work",
            ["toolName"] = tool,
            ["toolArgs"] = new JsonObject { ["command"] = command },
        }.ToJsonString();

    [Theory]
    [InlineData("git status")]
    [InlineData("gh pr list --limit 5")]
    [InlineData("npm test")]
    [InlineData("winget list")]
    [InlineData("choco list --local-only")]
    [InlineData("reg query HKCU\\Environment")]
    [InlineData("grep -rn needle src")] // POSIX utilities: bash-eligible (#114)
    [InlineData("ls -la")]
    [InlineData("find . -name '*.cs'")]
    [InlineData("  git log --oneline -5  ")] // surrounding whitespace trimmed
    public void RewriteCopilotToolCall_Bash_WrapsPlainFamilyCommands(string command)
    {
        var got = Hooks.RewriteCopilotToolCall(CopilotInput("bash", command), Exe);
        Assert.NotNull(got);

        var root = JsonNode.Parse(got!)!.AsObject();
        // modifiedArgs only — the hook must never decide permissions.
        Assert.Single(root);
        Assert.Null(root["permissionDecision"]);

        var updated = root["modifiedArgs"]!["command"]!.GetValue<string>();
        var prefix = Hooks.BashQuote(OperatingSystem.IsWindows() ? Install.WinToMsys(Exe) : Exe);
        Assert.Equal(prefix + " " + command.Trim(), updated);
    }

    [Theory]
    [InlineData("git status")]
    [InlineData("gh pr list --limit 5")]
    [InlineData("npm test")]
    [InlineData("winget list")]
    [InlineData("choco list --local-only")]
    [InlineData("reg query HKCU\\Environment")]
    public void RewriteCopilotToolCall_Powershell_WrapsWithCallOperator(string command)
    {
        var got = Hooks.RewriteCopilotToolCall(CopilotInput("powershell", command), Exe);
        Assert.NotNull(got);

        var updated = JsonNode.Parse(got!)!["modifiedArgs"]!["command"]!.GetValue<string>();
        Assert.Equal($"& '{Exe}' {command}", updated);
    }

    [Theory]
    [InlineData("bash", "git log | head -3")] // pipe: downstream expects raw bytes
    [InlineData("bash", "git diff > out.txt")] // redirect: file must get raw bytes
    [InlineData("bash", "git checkout $BRANCH")] // expansion
    [InlineData("bash", "cat notes.txt")] // uncovered family
    [InlineData("bash", "grep err log.txt | wc -l")] // pipe on a POSIX-utility family
    [InlineData("bash", "vtk git status")] // already wrapped
    [InlineData("bash", "")]
    [InlineData("powershell", "git status; ls")] // chaining
    [InlineData("powershell", "ls -la")] // PS alias for Get-ChildItem — bash-only family (#114)
    [InlineData("powershell", "grep -rn needle src")] // bash-only family
    [InlineData("powershell", "find . -name '*.cs'")] // Windows find.exe homonym — bash-only family
    [InlineData("powershell", "git log @{u}")] // PS hashtable/splat trigger
    [InlineData("powershell", "git status (Get-Location)")] // PS subexpression
    [InlineData("powershell", "git log --format={h}")] // PS script-block braces
    [InlineData("powershell", "& 'C:\\tools\\vtk\\vtk.exe' git status")] // already wrapped
    [InlineData("view", "git status")] // non-shell tool
    [InlineData("Bash", "git status")] // Claude casing, not Copilot's runtime name
    public void RewriteCopilotToolCall_PassesThroughIneligible(string tool, string command)
    {
        Assert.Null(Hooks.RewriteCopilotToolCall(CopilotInput(tool, command), Exe));
    }

    [Theory]
    [InlineData("""{"toolName":"bash","toolArgs":{}}""")] // no command
    [InlineData("""{"toolName":"bash","toolArgs":{"command":42}}""")] // non-string command
    [InlineData("""{"toolName":"bash","toolArgs":"git status"}""")] // args not an object
    [InlineData("""{"toolName":"bash"}""")] // no toolArgs
    [InlineData("""{"tool_name":"Bash","tool_input":{"command":"git status"}}""")] // Claude payload on the copilot flag
    [InlineData("not json at all")]
    [InlineData("")]
    public void RewriteCopilotToolCall_PassesThroughUnsupportedInput(string inputJson)
    {
        Assert.Null(Hooks.RewriteCopilotToolCall(inputJson, Exe));
    }

    [Fact]
    public void RewriteCopilotToolCall_PreservesOtherToolArgsFields()
    {
        const string input = """
        {"toolName":"powershell","toolArgs":{"command":"git status","timeout":5000,"description":"Show status"}}
        """;
        var got = Hooks.RewriteCopilotToolCall(input, Exe);
        Assert.NotNull(got);
        var modified = JsonNode.Parse(got!)!["modifiedArgs"]!.AsObject();
        Assert.Equal(5000, modified["timeout"]!.GetValue<int>());
        Assert.Equal("Show status", modified["description"]!.GetValue<string>());
    }

    [Fact]
    public void PsQuote_DoublesSingleQuotes()
    {
        Assert.Equal(@"'C:\a b\vtk.exe'", Hooks.PsQuote(@"C:\a b\vtk.exe"));
        Assert.Equal(@"'C:\o''brien\vtk.exe'", Hooks.PsQuote(@"C:\o'brien\vtk.exe"));
    }
}
