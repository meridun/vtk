using System.Diagnostics;
using System.Text.Json.Nodes;
using Xunit;

namespace Vtk.Tests.Smoke;

/// <summary>
/// Real-run smoke for `vtk hooks` (#45): exercises init/verify/rewrite
/// through the published binary against a scratch settings file — never the
/// real ~/.claude/settings.json. Covers the init lifecycle (install →
/// unchanged fixed point → verified → uninstall → clean), desync detection,
/// and the rewrite contract (updatedInput for a plain git/gh/npm command;
/// empty-output exit-0 passthrough for everything else, including malformed
/// input; no permissionDecision ever).
/// </summary>
[Collection("Smoke")]
public class HooksSmokeTests : IDisposable
{
    private readonly SmokeHarness _h = new();
    private readonly string _dir;

    public HooksSmokeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vtk-hooks-smoke-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
        _h.Dispose();
    }

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    /// <summary>Runs the published vtk binary with the given stdin. Returns stdout, stderr, exit code.</summary>
    private (string stdout, string stderr, int code) RunStdin(string stdin, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _h.Bin,
            WorkingDirectory = _dir,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        proc.StandardInput.Write(stdin);
        proc.StandardInput.Close();
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return (stdout, stderr, proc.ExitCode);
    }

    [Fact]
    public void InitLifecycle()
    {
        // Pre-existing unrelated settings must survive the whole lifecycle.
        File.WriteAllText(SettingsPath, "{\"model\":\"opus\",\"env\":{\"FOO\":\"bar\"}}");

        // install
        var (out1, err1, code1) = _h.Run(_dir, "hooks", "init", "--settings", SettingsPath);
        Assert.Equal(0, code1);
        Assert.Contains("installed", out1);
        var afterInit = File.ReadAllText(SettingsPath);
        Assert.Contains("PreToolUse", afterInit);
        Assert.Contains("hooks rewrite", afterInit);
        Assert.Contains("opus", afterInit);

        // re-run is an unchanged fixed point
        var (out2, _, code2) = _h.Run(_dir, "hooks", "init", "--settings", SettingsPath);
        Assert.Equal(0, code2);
        Assert.Contains("unchanged", out2);
        Assert.Equal(afterInit, File.ReadAllText(SettingsPath));

        // verify green against the same binary
        var (vOut, vErr, vCode) = _h.Run(_dir, "hooks", "verify", "--settings", SettingsPath);
        Assert.Equal(0, vCode);
        Assert.Contains("verified", vOut);

        // uninstall removes exactly the managed entry, preserving the rest
        var (uOut, _, uCode) = _h.Run(_dir, "hooks", "init", "--uninstall", "--settings", SettingsPath);
        Assert.Equal(0, uCode);
        Assert.Contains("removed", uOut);
        var afterUninstall = File.ReadAllText(SettingsPath);
        Assert.DoesNotContain("hooks rewrite", afterUninstall);
        Assert.DoesNotContain("PreToolUse", afterUninstall);
        Assert.Contains("opus", afterUninstall);
        Assert.Contains("bar", afterUninstall);

        // uninstall again is an honest no-op
        var (u2Out, _, u2Code) = _h.Run(_dir, "hooks", "init", "--uninstall", "--settings", SettingsPath);
        Assert.Equal(0, u2Code);
        Assert.Contains("no hook present", u2Out);

        // verify after uninstall fails
        var (_, v2Err, v2Code) = _h.Run(_dir, "hooks", "verify", "--settings", SettingsPath);
        Assert.Equal(1, v2Code);
        Assert.Contains("no vtk PreToolUse hook", v2Err);
    }

    [Fact]
    public void VerifyDetectsDesyncAndMissingFile()
    {
        // hook wired to a different (stale) binary path → desync, exit 1
        var stale = OperatingSystem.IsWindows() ? "C:\\stale\\vtk.exe" : "/stale/vtk";
        var settings = new JsonObject
        {
            ["hooks"] = new JsonObject
            {
                ["PreToolUse"] = new JsonArray(new JsonObject
                {
                    ["matcher"] = "Bash",
                    ["hooks"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "command",
                        ["command"] = $"\"{stale}\" hooks rewrite",
                    }),
                }),
            },
        };
        File.WriteAllText(SettingsPath, settings.ToJsonString());
        var (_, dErr, dCode) = _h.Run(_dir, "hooks", "verify", "--settings", SettingsPath);
        Assert.Equal(1, dCode);
        Assert.Contains("desync", dErr);

        // init from the canonical binary heals the desync
        var (hOut, _, hCode) = _h.Run(_dir, "hooks", "init", "--settings", SettingsPath);
        Assert.Equal(0, hCode);
        Assert.Contains("updated", hOut);
        var (_, _, v3Code) = _h.Run(_dir, "hooks", "verify", "--settings", SettingsPath);
        Assert.Equal(0, v3Code);

        // missing settings file, exit 1
        var (_, mErr, mCode) = _h.Run(_dir, "hooks", "verify", "--settings", Path.Combine(_dir, "nope.json"));
        Assert.Equal(1, mCode);
        Assert.Contains("not found", mErr);
    }

    [Fact]
    public void RewriteWrapsPlainFamilyCommands()
    {
        foreach (var cmd in new[] { "git status", "gh pr list", "npm test", "winget list", "choco list --local-only", "reg query HKCU\\Environment" })
        {
            var input = new JsonObject
            {
                ["tool_name"] = "Bash",
                ["tool_input"] = new JsonObject { ["command"] = cmd, ["description"] = "d" },
            }.ToJsonString();
            var (stdout, stderr, code) = RunStdin(input, "hooks", "rewrite");
            Assert.Equal(0, code);
            Assert.Equal("", stderr);
            var root = JsonNode.Parse(stdout)!.AsObject();
            var hso = root["hookSpecificOutput"]!.AsObject();
            Assert.Equal("PreToolUse", hso["hookEventName"]!.GetValue<string>());
            var updated = hso["updatedInput"]!.AsObject();
            var rewritten = updated["command"]!.GetValue<string>();
            Assert.EndsWith(" " + cmd, rewritten);
            Assert.StartsWith("'", rewritten); // bash-quoted self-located binary
            Assert.Equal("d", updated["description"]!.GetValue<string>()); // other fields preserved
            Assert.DoesNotContain("permissionDecision", stdout);
        }
    }

    [Theory]
    [InlineData("{\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\"git log | head\"}}")] // pipe
    [InlineData("{\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\"git diff > f.txt\"}}")] // redirect
    [InlineData("{\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\"cd x && git status\"}}")] // chaining
    [InlineData("{\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\"ls -la\"}}")] // non-family
    [InlineData("{\"tool_name\":\"Read\",\"tool_input\":{\"file_path\":\"x\"}}")] // non-Bash tool
    [InlineData("{not json")] // malformed
    [InlineData("")] // empty stdin
    public void RewritePassesThroughSilently(string input)
    {
        var (stdout, stderr, code) = RunStdin(input, "hooks", "rewrite");
        Assert.Equal(0, code); // passthrough never breaks the tool call
        Assert.Equal("", stdout);
        Assert.Equal("", stderr);
    }

    [Fact]
    public void ConcurrentRewritesBothSucceed()
    {
        var input = "{\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\"git status\"}}";
        var t1 = Task.Run(() => RunStdin(input, "hooks", "rewrite"));
        var t2 = Task.Run(() => RunStdin(input, "hooks", "rewrite"));
        Task.WaitAll(t1, t2);
        Assert.Equal(0, t1.Result.code);
        Assert.Equal(0, t2.Result.code);
        Assert.Equal(t1.Result.stdout, t2.Result.stdout);
        Assert.NotEqual("", t1.Result.stdout.Trim());
    }
}
