using System.Diagnostics;
using System.Text.Json.Nodes;
using Xunit;

namespace Vtk.Tests.Smoke;

/// <summary>
/// Real-run smoke for `vtk hooks … --copilot` (#83): exercises the Copilot
/// CLI installer/verify/rewrite trio through the published binary against a
/// scratch hooks dir — never the real ~/.copilot/hooks. Covers the managed
/// vtk.json lifecycle (install → unchanged fixed point → verified →
/// uninstall → clean), COPILOT_HOME resolution, desync detection, the
/// camelCase rewrite contract (modifiedArgs for a plain git/gh/npm command;
/// empty-output exit-0 passthrough for everything else — load-bearing, since
/// Copilot preToolUse hooks are fail-closed on non-zero exit), and exit-code
/// parity of the rewritten PowerShell command executed for real.
/// </summary>
[Collection("Smoke")]
public class CopilotHooksSmokeTests : IDisposable
{
    private readonly SmokeHarness _h = new();
    private readonly string _dir;

    public CopilotHooksSmokeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vtk-copilot-smoke-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
        _h.Dispose();
    }

    private string HookFile => Path.Combine(_dir, "vtk.json");

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
        // --print is side-effect-free
        var (pOut, _, pCode) = _h.Run(_dir, "hooks", "init", "--copilot", "--print", "--hooks-dir", _dir);
        Assert.Equal(0, pCode);
        Assert.Contains("preToolUse", pOut);
        Assert.False(File.Exists(HookFile));

        // install writes the wholly-vtk-owned managed file
        var (out1, _, code1) = _h.Run(_dir, "hooks", "init", "--copilot", "--hooks-dir", _dir);
        Assert.Equal(0, code1);
        Assert.Contains("installed", out1);
        var afterInit = File.ReadAllText(HookFile);
        var root = JsonNode.Parse(afterInit)!.AsObject();
        Assert.Equal(1, root["version"]!.GetValue<int>());
        var entry = root["hooks"]!["preToolUse"]![0]!.AsObject();
        Assert.Equal("bash|powershell", entry["matcher"]!.GetValue<string>());
        Assert.EndsWith(" hooks rewrite --copilot", entry["bash"]!.GetValue<string>());
        Assert.StartsWith("& ", entry["powershell"]!.GetValue<string>());

        // re-run is an unchanged fixed point
        var (out2, _, code2) = _h.Run(_dir, "hooks", "init", "--copilot", "--hooks-dir", _dir);
        Assert.Equal(0, code2);
        Assert.Contains("unchanged", out2);
        Assert.Equal(afterInit, File.ReadAllText(HookFile));

        // verify green against the same binary
        var (vOut, _, vCode) = _h.Run(_dir, "hooks", "verify", "--copilot", "--hooks-dir", _dir);
        Assert.Equal(0, vCode);
        Assert.Contains("verified", vOut);

        // dry-run uninstall reports but keeps the file
        var (dOut, _, dCode) = _h.Run(_dir, "hooks", "init", "--copilot", "--uninstall", "--dry-run", "--hooks-dir", _dir);
        Assert.Equal(0, dCode);
        Assert.Contains("dry-run", dOut);
        Assert.True(File.Exists(HookFile));

        // uninstall deletes exactly the managed file
        var (uOut, _, uCode) = _h.Run(_dir, "hooks", "init", "--copilot", "--uninstall", "--hooks-dir", _dir);
        Assert.Equal(0, uCode);
        Assert.Contains("removed", uOut);
        Assert.False(File.Exists(HookFile));

        // uninstall again is an honest no-op
        var (u2Out, _, u2Code) = _h.Run(_dir, "hooks", "init", "--copilot", "--uninstall", "--hooks-dir", _dir);
        Assert.Equal(0, u2Code);
        Assert.Contains("no hook present", u2Out);

        // verify after uninstall fails
        var (_, v2Err, v2Code) = _h.Run(_dir, "hooks", "verify", "--copilot", "--hooks-dir", _dir);
        Assert.Equal(1, v2Code);
        Assert.Contains("not found", v2Err);
    }

    [Fact]
    public void VerifyDetectsDesync()
    {
        var stale = OperatingSystem.IsWindows() ? "C:\\stale\\vtk.exe" : "/stale/vtk";
        var config = new JsonObject
        {
            ["version"] = 1,
            ["hooks"] = new JsonObject
            {
                ["preToolUse"] = new JsonArray(new JsonObject
                {
                    ["type"] = "command",
                    ["matcher"] = "bash|powershell",
                    ["bash"] = $"\"{stale}\" hooks rewrite --copilot",
                    ["powershell"] = $"& \"{stale}\" hooks rewrite --copilot",
                }),
            },
        };
        File.WriteAllText(HookFile, config.ToJsonString());
        var (_, dErr, dCode) = _h.Run(_dir, "hooks", "verify", "--copilot", "--hooks-dir", _dir);
        Assert.Equal(1, dCode);
        Assert.Contains("desync", dErr);

        // init from the canonical binary heals the desync
        var (hOut, _, hCode) = _h.Run(_dir, "hooks", "init", "--copilot", "--hooks-dir", _dir);
        Assert.Equal(0, hCode);
        Assert.Contains("updated", hOut);
        var (_, _, vCode) = _h.Run(_dir, "hooks", "verify", "--copilot", "--hooks-dir", _dir);
        Assert.Equal(0, vCode);
    }

    [Fact]
    public void CopilotHomeResolvesDefaultDir()
    {
        var home = Path.Combine(_dir, "copilot-home");
        var env = new Dictionary<string, string> { ["COPILOT_HOME"] = home };
        var (iOut, _, iCode) = _h.RunEnv(_dir, env, "hooks", "init", "--copilot");
        Assert.Equal(0, iCode);
        Assert.Contains("installed", iOut);
        Assert.True(File.Exists(Path.Combine(home, "hooks", "vtk.json")));
        var (_, _, vCode) = _h.RunEnv(_dir, env, "hooks", "verify", "--copilot");
        Assert.Equal(0, vCode);
    }

    [Fact]
    public void RewriteWrapsPlainFamilyCommandsPerShell()
    {
        // bash: bash-quoted (MSYS-style on Windows) self-located binary prefix
        var bashInput = new JsonObject
        {
            ["toolName"] = "bash",
            ["toolArgs"] = new JsonObject { ["command"] = "git status", ["extra"] = "keep" },
        }.ToJsonString();
        var (bOut, bErr, bCode) = RunStdin(bashInput, "hooks", "rewrite", "--copilot");
        Assert.Equal(0, bCode);
        Assert.Equal("", bErr);
        var bArgs = JsonNode.Parse(bOut)!.AsObject()["modifiedArgs"]!.AsObject();
        var bCmd = bArgs["command"]!.GetValue<string>();
        Assert.StartsWith("'", bCmd);
        Assert.EndsWith(" git status", bCmd);
        Assert.Equal("keep", bArgs["extra"]!.GetValue<string>()); // other fields preserved
        Assert.DoesNotContain("permissionDecision", bOut);
        Assert.DoesNotContain("hookSpecificOutput", bOut); // native format, not the Claude shape

        // powershell: call-operator + single-quoted native path prefix
        var psInput = new JsonObject
        {
            ["toolName"] = "powershell",
            ["toolArgs"] = new JsonObject { ["command"] = "gh pr list" },
        }.ToJsonString();
        var (pOut, pErr, pCode) = RunStdin(psInput, "hooks", "rewrite", "--copilot");
        Assert.Equal(0, pCode);
        Assert.Equal("", pErr);
        var pCmd = JsonNode.Parse(pOut)!.AsObject()["modifiedArgs"]!["command"]!.GetValue<string>();
        Assert.StartsWith("& '", pCmd);
        Assert.EndsWith(" gh pr list", pCmd);
    }

    [Theory]
    [InlineData("{\"toolName\":\"bash\",\"toolArgs\":{\"command\":\"git log | head\"}}")] // pipe
    [InlineData("{\"toolName\":\"bash\",\"toolArgs\":{\"command\":\"git diff > f.txt\"}}")] // redirect
    [InlineData("{\"toolName\":\"powershell\",\"toolArgs\":{\"command\":\"cd x; git status\"}}")] // chaining
    [InlineData("{\"toolName\":\"powershell\",\"toolArgs\":{\"command\":\"git status (Get-Location)\"}}")] // PS subexpression
    [InlineData("{\"toolName\":\"powershell\",\"toolArgs\":{\"command\":\"git log @{a=1}\"}}")] // PS splatting/hashtable
    [InlineData("{\"toolName\":\"bash\",\"toolArgs\":{\"command\":\"ls -la\"}}")] // non-family
    [InlineData("{\"toolName\":\"str_replace\",\"toolArgs\":{\"command\":\"git status\"}}")] // non-shell tool
    [InlineData("{\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\"git status\"}}")] // Claude shape, wrong casing
    [InlineData("{\"toolName\":\"bash\",\"toolArgs\":{}}")] // missing command
    [InlineData("{not json")] // malformed
    [InlineData("")] // empty stdin
    public void RewritePassesThroughSilently(string input)
    {
        var (stdout, stderr, code) = RunStdin(input, "hooks", "rewrite", "--copilot");
        Assert.Equal(0, code); // fail-closed host: non-zero would deny the tool call
        Assert.Equal("", stdout);
        Assert.Equal("", stderr);
    }

    [Fact]
    public void ConcurrentCopilotRewritesBothSucceed()
    {
        var input = "{\"toolName\":\"powershell\",\"toolArgs\":{\"command\":\"git status\"}}";
        var t1 = Task.Run(() => RunStdin(input, "hooks", "rewrite", "--copilot"));
        var t2 = Task.Run(() => RunStdin(input, "hooks", "rewrite", "--copilot"));
        Task.WaitAll(t1, t2);
        Assert.Equal(0, t1.Result.code);
        Assert.Equal(0, t2.Result.code);
        Assert.Equal(t1.Result.stdout, t2.Result.stdout);
        Assert.NotEqual("", t1.Result.stdout.Trim());
    }

    /// <summary>
    /// The sacred AC end to end: the rewritten PowerShell command, executed
    /// for real in a scratch repo, exits with the same code as the raw
    /// command — on success and on failure.
    /// </summary>
    [Theory]
    [InlineData("git status")]
    [InlineData("git bogus-subcommand")]
    public void RewrittenPowershellCommandPreservesExitCode(string command)
    {
        if (!OperatingSystem.IsWindows()) return; // PS parity smoke is Windows-primary

        var input = new JsonObject
        {
            ["toolName"] = "powershell",
            ["toolArgs"] = new JsonObject { ["command"] = command },
        }.ToJsonString();
        var (stdout, _, code) = RunStdin(input, "hooks", "rewrite", "--copilot");
        Assert.Equal(0, code);
        var rewritten = JsonNode.Parse(stdout)!.AsObject()["modifiedArgs"]!["command"]!.GetValue<string>();

        Assert.Equal(RunPowershell(command), RunPowershell(rewritten));
    }

    private int RunPowershell(string command)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell",
            WorkingDirectory = _h.Repo,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(command);
        psi.EnvironmentVariables["LOCALAPPDATA"] = _h.Home; // isolate the spool
        psi.EnvironmentVariables["XDG_CACHE_HOME"] = _h.Home;
        psi.EnvironmentVariables["HOME"] = _h.Home;
        using var proc = Process.Start(psi)!;
        proc.StandardOutput.ReadToEnd();
        proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return proc.ExitCode;
    }
}
