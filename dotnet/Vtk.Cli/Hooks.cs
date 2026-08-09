// `vtk hooks` — self-install/verify the agent pre-invocation rewrite hooks.
//
// Claude Code (#45, MVP):
//   vtk hooks init     splice the hook into ~/.claude/settings.json
//   vtk hooks verify   integrity/desync check of the installed hook
//   vtk hooks rewrite  the hook payload: stdin tool-call JSON in,
//                      updatedInput JSON (or nothing = passthrough) out
//
// GitHub Copilot CLI (#83, --copilot on each subcommand): same trio against
// Copilot CLI's documented version-1 hook config (preToolUse command hooks,
// stdout `modifiedArgs` rewrite; docs.github.com/en/copilot/reference/
// hooks-reference). The install target is a wholly-vtk-owned managed file
// `vtk.json` in the user-level hooks directory (~/.copilot/hooks, or
// $COPILOT_HOME/hooks) — the file itself is the marker, so re-runs are a
// fixed point and --uninstall deletes exactly that file.
//
// Same shape as `vtk install` (Install.cs, #49): self-locating via the
// running executable's path, one managed entry identified by a marker (here
// the command suffix " hooks rewrite [--copilot]" — the JSON analog of the
// rc-file marker block), idempotent re-runs, exact --uninstall, honest
// "unchanged".
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vtk.Cli;

public static class Hooks
{
    /// <summary>Suffix that marks a PreToolUse command entry as vtk-managed.</summary>
    internal const string CommandMarker = " hooks rewrite";

    /// <summary>Tool families the rewrite hook wraps in every shell flavor — same set as the Install.cs shell wrappers.</summary>
    private static readonly HashSet<string> Families = new(StringComparer.Ordinal) { "git", "gh", "npm", "winget", "choco", "reg" };

    /// <summary>
    /// Families plus the POSIX file/search utilities (#114) — bash flavors
    /// only. PowerShell resolves `ls` to the Get-ChildItem alias and Windows
    /// resolves `find` to find.exe (string search), so rewriting them outside
    /// bash would change what runs. Hook-only coverage by design: shell
    /// functions are pipe-unsafe for these families (a grep() function also
    /// wraps mid-pipeline calls), so Install.cs deliberately does not wrap
    /// them.
    /// </summary>
    private static readonly HashSet<string> BashFamilies = new(Families, StringComparer.Ordinal) { "grep", "ls", "find" };

    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: vtk hooks <init|verify|rewrite> [--copilot] [--dry-run] [--uninstall] [--print] [--settings <path>] [--hooks-dir <path>]");
            return 2;
        }
        var rest = args[1..];
        var copilot = rest.Contains("--copilot");
        if (copilot) rest = rest.Where(a => a != "--copilot").ToArray();
        return args[0] switch
        {
            "init" => copilot ? CmdCopilotInit(rest) : CmdInit(rest),
            "verify" => copilot ? CmdCopilotVerify(rest) : CmdVerify(rest),
            "rewrite" => CmdRewrite(copilot),
            _ => Unknown(args[0]),
        };
    }

    private static int Unknown(string sub)
    {
        Console.Error.WriteLine($"vtk hooks: unknown subcommand \"{sub}\" (expected init, verify, or rewrite)");
        return 2;
    }

    // ---------------------------------------------------------------- init

    private static int CmdInit(string[] args)
    {
        bool dryRun = false, uninstall = false, printOnly = false;
        string settings = "";
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--dry-run": dryRun = true; break;
                case "--uninstall": uninstall = true; break;
                case "--print": printOnly = true; break;
                case "--settings":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("vtk hooks init: --settings requires a path");
                        return 2;
                    }
                    i++;
                    settings = args[i];
                    break;
                default:
                    Console.Error.WriteLine($"vtk hooks init: unexpected argument \"{args[i]}\"");
                    return 2;
            }
        }

        string exe;
        try
        {
            exe = Path.GetFullPath(Environment.ProcessPath
                ?? throw new InvalidOperationException("process path unavailable"));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"vtk hooks init: cannot locate own binary: {ex.Message}");
            return 1;
        }
        var hookCmd = RenderHookCommand(exe);

        // --print is side-effect-free: emit the hook command for the user to
        // wire by hand, touching no files.
        if (printOnly)
        {
            Console.Out.WriteLine(hookCmd);
            return 0;
        }

        if (settings == "")
        {
            try { settings = DefaultSettingsPath(); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"vtk hooks init: {ex.Message}");
                return 1;
            }
        }

        var old = File.Exists(settings) ? File.ReadAllText(settings) : "";
        string next, action;
        try
        {
            if (uninstall)
            {
                var (res, found) = UninstallJson(old);
                if (!found)
                {
                    Console.Out.WriteLine($"claude-code: no hook present ({settings})");
                    return 0;
                }
                next = res;
                action = "removed";
            }
            else
            {
                next = InitJson(old, hookCmd);
                if (next == old)
                {
                    Console.Out.WriteLine($"claude-code: unchanged ({settings})");
                    return 0;
                }
                action = FindVtkCommands(old).Count > 0 ? "updated" : "installed";
            }
        }
        catch (JsonException ex)
        {
            // Never clobber a settings file we cannot parse — that could
            // destroy the user's unrelated Claude Code configuration.
            Console.Error.WriteLine($"vtk hooks init: cannot parse {settings}: {ex.Message}");
            return 1;
        }

        if (dryRun)
        {
            Console.Out.WriteLine($"claude-code: {action} (dry-run) ({settings})");
            return 0;
        }
        try
        {
            var dir = Path.GetDirectoryName(settings);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(settings, next);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"vtk hooks init: {ex.Message}");
            return 1;
        }
        Console.Out.WriteLine($"claude-code: {action} ({settings})");
        return 0;
    }

    /// <summary>The command Claude Code runs for the hook. Double quotes survive both sh and cmd, so one form serves every hook runner.</summary>
    internal static string RenderHookCommand(string exe) => $"\"{exe}\"{CommandMarker}";

    /// <summary>Default Claude Code user settings file: ~/.claude/settings.json.</summary>
    private static string DefaultSettingsPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) throw new InvalidOperationException("cannot resolve home dir");
        return Path.Combine(home, ".claude", "settings.json");
    }

    /// <summary>
    /// Returns the settings JSON with exactly one vtk-managed PreToolUse
    /// entry. Idempotent: any prior vtk entries are stripped first, so
    /// re-running with the same binary path is a fixed point. Everything
    /// unrelated in the file (other settings, other hooks) is preserved.
    /// Empty/missing input starts from "{}".
    /// </summary>
    internal static string InitJson(string existing, string hookCmd)
    {
        var root = ParseRoot(existing);
        RemoveVtkEntries(root);

        var hooks = root["hooks"] as JsonObject;
        if (hooks is null) root["hooks"] = hooks = new JsonObject();
        var pre = hooks["PreToolUse"] as JsonArray;
        if (pre is null) hooks["PreToolUse"] = pre = new JsonArray();

        pre.Add(new JsonObject
        {
            ["matcher"] = "Bash",
            ["hooks"] = new JsonArray(new JsonObject
            {
                ["type"] = "command",
                ["command"] = hookCmd,
            }),
        });
        return Serialize(root);
    }

    /// <summary>Removes the vtk-managed entry and reports whether one was present. Unrelated content is untouched (modulo reformatting).</summary>
    internal static (string content, bool found) UninstallJson(string existing)
    {
        var root = ParseRoot(existing);
        var found = RemoveVtkEntries(root);
        if (!found) return (existing, false);
        return (Serialize(root), true);
    }

    /// <summary>
    /// Strips every vtk-managed command from hooks.PreToolUse, dropping
    /// emptied hook groups and, when nothing else remains, the PreToolUse
    /// array and hooks object themselves so an uninstall leaves no husk.
    /// </summary>
    private static bool RemoveVtkEntries(JsonObject root)
    {
        if (root["hooks"] is not JsonObject hooks || hooks["PreToolUse"] is not JsonArray pre) return false;

        var found = false;
        for (var g = pre.Count - 1; g >= 0; g--)
        {
            if (pre[g] is not JsonObject group || group["hooks"] is not JsonArray cmds) continue;
            for (var c = cmds.Count - 1; c >= 0; c--)
            {
                if (cmds[c] is JsonObject h && IsVtkHookCommand(h["command"]?.GetValue<string>()))
                {
                    cmds.RemoveAt(c);
                    found = true;
                }
            }
            if (cmds.Count == 0) pre.RemoveAt(g);
        }
        if (found && pre.Count == 0)
        {
            hooks.Remove("PreToolUse");
            if (hooks.Count == 0) root.Remove("hooks");
        }
        return found;
    }

    /// <summary>Reports whether a PreToolUse command string is the vtk-managed rewrite hook.</summary>
    internal static bool IsVtkHookCommand(string? cmd) =>
        cmd is not null && cmd.TrimEnd().EndsWith(CommandMarker, StringComparison.Ordinal);

    /// <summary>Collects every vtk-managed command string in the settings JSON ("" input is an empty file). Malformed JSON throws JsonException.</summary>
    internal static List<string> FindVtkCommands(string json)
    {
        var res = new List<string>();
        var root = ParseRoot(json);
        if (root["hooks"] is not JsonObject hooks || hooks["PreToolUse"] is not JsonArray pre) return res;
        foreach (var g in pre)
        {
            if (g is not JsonObject group || group["hooks"] is not JsonArray cmds) continue;
            foreach (var c in cmds)
            {
                if (c is JsonObject h && h["command"]?.GetValue<string>() is string cmd && IsVtkHookCommand(cmd))
                    res.Add(cmd);
            }
        }
        return res;
    }

    private static JsonObject ParseRoot(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new JsonObject();
        return JsonNode.Parse(json) as JsonObject
            ?? throw new JsonException("settings root is not a JSON object");
    }

    private static string Serialize(JsonObject root) =>
        root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";

    // -------------------------------------------------------------- verify

    private static int CmdVerify(string[] args)
    {
        string settings = "";
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--settings":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("vtk hooks verify: --settings requires a path");
                        return 2;
                    }
                    i++;
                    settings = args[i];
                    break;
                default:
                    Console.Error.WriteLine($"vtk hooks verify: unexpected argument \"{args[i]}\"");
                    return 2;
            }
        }

        string exe;
        try
        {
            exe = Path.GetFullPath(Environment.ProcessPath
                ?? throw new InvalidOperationException("process path unavailable"));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"vtk hooks verify: cannot locate own binary: {ex.Message}");
            return 1;
        }
        if (settings == "")
        {
            try { settings = DefaultSettingsPath(); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"vtk hooks verify: {ex.Message}");
                return 1;
            }
        }

        if (!File.Exists(settings))
        {
            Console.Error.WriteLine($"vtk hooks verify: FAIL: settings file not found ({settings}) — run `vtk hooks init`");
            return 1;
        }
        var problems = VerifyJson(File.ReadAllText(settings), exe, File.Exists);
        if (problems.Count == 0)
        {
            Console.Out.WriteLine($"claude-code: verified ({settings} -> {exe})");
            return 0;
        }
        foreach (var p in problems) Console.Error.WriteLine("vtk hooks verify: FAIL: " + p);
        return 1;
    }

    /// <summary>
    /// Integrity/desync checks over the settings JSON: exactly one
    /// vtk-managed PreToolUse entry, matcher "Bash", the hook's binary
    /// present on disk, and that binary being THIS binary — the desync class
    /// of bug (stale hook pointing at an old/removed install) that the
    /// hand-wired two-binary setup could not detect. fileExists is injected
    /// for testability. Returns an empty list when verified.
    /// </summary>
    internal static List<string> VerifyJson(string json, string exe, Func<string, bool> fileExists)
    {
        var problems = new List<string>();
        JsonObject root;
        try { root = ParseRoot(json); }
        catch (JsonException ex)
        {
            problems.Add($"settings file is not valid JSON: {ex.Message}");
            return problems;
        }

        var cmds = new List<(string cmd, string matcher)>();
        if (root["hooks"] is JsonObject hooks && hooks["PreToolUse"] is JsonArray pre)
        {
            foreach (var g in pre)
            {
                if (g is not JsonObject group || group["hooks"] is not JsonArray list) continue;
                var matcher = group["matcher"]?.GetValue<string>() ?? "";
                foreach (var c in list)
                {
                    if (c is JsonObject h && h["command"]?.GetValue<string>() is string cmd && IsVtkHookCommand(cmd))
                        cmds.Add((cmd, matcher));
                }
            }
        }

        if (cmds.Count == 0)
        {
            problems.Add("no vtk PreToolUse hook installed — run `vtk hooks init`");
            return problems;
        }
        if (cmds.Count > 1)
        {
            problems.Add($"{cmds.Count} vtk PreToolUse hook entries found (expected 1) — run `vtk hooks init` to collapse them");
        }

        var (cmd0, matcher0) = cmds[0];
        if (matcher0 != "Bash")
        {
            problems.Add($"hook matcher is \"{matcher0}\" (expected \"Bash\")");
        }
        var hookExe = ExtractExe(cmd0);
        if (hookExe == "")
        {
            problems.Add($"cannot parse binary path from hook command: {cmd0}");
            return problems;
        }
        if (!fileExists(hookExe))
        {
            problems.Add($"hook binary missing: {hookExe}");
        }
        if (!PathsEqual(hookExe, exe))
        {
            problems.Add($"hook points at {hookExe} but this binary is {exe} (desync) — run `vtk hooks init` from the canonical binary");
        }
        return problems;
    }

    /// <summary>Extracts the executable path from a rendered hook command ("&lt;exe&gt;" hooks rewrite), quoted or bare.</summary>
    internal static string ExtractExe(string cmd)
    {
        cmd = cmd.TrimEnd();
        if (!cmd.EndsWith(CommandMarker, StringComparison.Ordinal)) return "";
        var exe = cmd[..^CommandMarker.Length].Trim();
        if (exe.Length >= 2 && exe[0] == '"' && exe[^1] == '"') exe = exe[1..^1];
        return exe;
    }

    /// <summary>Path equality with Windows' case-insensitive filesystem semantics.</summary>
    private static bool PathsEqual(string a, string b)
    {
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), cmp); }
        catch { return string.Equals(a, b, cmp); }
    }

    // ------------------------------------------------------------- rewrite

    /// <summary>
    /// The installed hook's entry point: reads the pre-tool-use tool-call
    /// JSON from stdin and prints a rewrite JSON that routes a plain
    /// intercepted-family command (Families) through this binary — Claude Code updatedInput by
    /// default, Copilot CLI modifiedArgs with --copilot. Anything
    /// unsupported — other tools, compound/piped/redirected commands,
    /// already-wrapped commands, malformed input, any internal error — emits
    /// nothing and exits 0, so the agent's command runs exactly as typed
    /// (passthrough is a feature). Exit 0 is load-bearing for --copilot:
    /// Copilot CLI preToolUse command hooks are fail-closed, so a non-zero
    /// exit would deny the agent's tool call outright. No permission
    /// decision is ever emitted: the normal permission flow applies to the
    /// updated input; the hook never approves or blocks.
    /// </summary>
    private static int CmdRewrite(bool copilot)
    {
        try
        {
            var input = Console.In.ReadToEnd();
            var exe = Path.GetFullPath(Environment.ProcessPath
                ?? throw new InvalidOperationException("process path unavailable"));
            var output = copilot ? RewriteCopilotToolCall(input, exe) : RewriteToolCall(input, exe);
            if (output is not null) Console.Out.WriteLine(output);
        }
        catch
        {
            // Passthrough — never surface an error into the agent's tool call.
        }
        return 0;
    }

    /// <summary>
    /// Pure rewrite core: hook input JSON + own binary path in, hook output
    /// JSON out, or null for "no rewrite" (passthrough). Rewrites only a
    /// single plain top-level intercepted-family invocation: any shell metacharacter
    /// that would change data flow around the wrapper (pipes, redirects,
    /// chaining, substitution) disqualifies the command, because vtk
    /// compacting inside a pipe or redirect would alter what the rest of the
    /// pipeline sees.
    /// </summary>
    internal static string? RewriteToolCall(string inputJson, string exe)
    {
        JsonObject? root;
        try { root = JsonNode.Parse(inputJson) as JsonObject; }
        catch (JsonException) { return null; }
        if (root is null) return null;

        if (root["tool_name"]?.GetValue<string>() != "Bash") return null;
        if (root["tool_input"] is not JsonObject ti) return null;
        if (ti["command"]?.GetValue<string>() is not string command) return null;

        var rewritten = RewriteCommand(command, exe);
        if (rewritten is null) return null;

        var updated = ti.DeepClone().AsObject();
        updated["command"] = rewritten;
        var output = new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject
            {
                ["hookEventName"] = "PreToolUse",
                ["updatedInput"] = updated,
            },
        };
        return output.ToJsonString();
    }

    /// <summary>Rewrites "git ..." to "'&lt;exe&gt;' git ..." when eligible; null means leave the command untouched.</summary>
    internal static string? RewriteCommand(string command, string exe)
    {
        var trimmed = EligibleFamilyCommand(command, BashFamilies);
        if (trimmed is null) return null;
        return BashQuote(HookExePath(exe)) + " " + trimmed;
    }

    /// <summary>
    /// Conservative eligibility shared by every rewrite flavor: a single
    /// simple top-level command in the given family set, or null. Rewriting
    /// `git log | head` or `git diff > f` would put compacted output where
    /// the pipeline expects raw bytes — altered semantics, so passthrough.
    /// </summary>
    internal static string? EligibleFamilyCommand(string command, HashSet<string> families)
    {
        var trimmed = command.Trim();
        if (trimmed == "") return null;
        if (trimmed.IndexOfAny(new[] { '|', '&', ';', '<', '>', '`', '$', '\n', '\r' }) >= 0) return null;

        var space = trimmed.IndexOf(' ');
        var first = space < 0 ? trimmed : trimmed[..space];
        if (!families.Contains(first)) return null;

        return trimmed;
    }

    /// <summary>The binary path in the form the Bash tool's shell expects: MSYS on Windows (Git Bash), native elsewhere.</summary>
    private static string HookExePath(string exe) =>
        OperatingSystem.IsWindows() ? Install.WinToMsys(exe) : exe;

    /// <summary>Single-quotes a path for bash (embedded single quotes escaped the POSIX way).</summary>
    internal static string BashQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    // ---------------------------------------------------- copilot (#83)

    /// <summary>Suffix that marks a Copilot CLI hook command as vtk-managed.</summary>
    internal const string CopilotCommandMarker = " hooks rewrite --copilot";

    /// <summary>toolName matcher for the managed entry: Copilot CLI's two shell tools.</summary>
    internal const string CopilotMatcher = "bash|powershell";

    /// <summary>Name of the wholly-vtk-owned managed file inside the hooks directory.</summary>
    internal const string CopilotFileName = "vtk.json";

    /// <summary>
    /// Default Copilot CLI user-level hooks directory: $COPILOT_HOME/hooks
    /// when COPILOT_HOME is set, else ~/.copilot/hooks (per the GitHub
    /// Copilot hooks reference).
    /// </summary>
    private static string DefaultCopilotHooksDir()
    {
        var home = Environment.GetEnvironmentVariable("COPILOT_HOME");
        if (!string.IsNullOrEmpty(home)) return Path.Combine(home, "hooks");
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(profile)) throw new InvalidOperationException("cannot resolve home dir");
        return Path.Combine(profile, ".copilot", "hooks");
    }

    /// <summary>Bash-side hook command ("&lt;exe&gt;" hooks rewrite --copilot). Double quotes survive sh.</summary>
    internal static string RenderCopilotBashCommand(string exe) => $"\"{exe}\"{CopilotCommandMarker}";

    /// <summary>PowerShell-side hook command. The call operator is required: a bare quoted path is a string expression, not a command.</summary>
    internal static string RenderCopilotPowershellCommand(string exe) => $"& \"{exe}\"{CopilotCommandMarker}";

    /// <summary>
    /// The full managed-file content: Copilot CLI's documented version-1
    /// config with exactly one preToolUse command entry matching the two
    /// shell tools. Pure render — byte-identical for the same binary path,
    /// which is what makes the file-level install idempotent.
    /// </summary>
    internal static string CopilotConfigJson(string exe)
    {
        var root = new JsonObject
        {
            ["version"] = 1,
            ["hooks"] = new JsonObject
            {
                ["preToolUse"] = new JsonArray(new JsonObject
                {
                    ["type"] = "command",
                    ["matcher"] = CopilotMatcher,
                    ["bash"] = RenderCopilotBashCommand(exe),
                    ["powershell"] = RenderCopilotPowershellCommand(exe),
                }),
            },
        };
        return Serialize(root);
    }

    private static int CmdCopilotInit(string[] args)
    {
        bool dryRun = false, uninstall = false, printOnly = false;
        string dir = "";
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--dry-run": dryRun = true; break;
                case "--uninstall": uninstall = true; break;
                case "--print": printOnly = true; break;
                case "--hooks-dir":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("vtk hooks init: --hooks-dir requires a path");
                        return 2;
                    }
                    i++;
                    dir = args[i];
                    break;
                default:
                    Console.Error.WriteLine($"vtk hooks init: unexpected argument \"{args[i]}\"");
                    return 2;
            }
        }

        string exe;
        try
        {
            exe = Path.GetFullPath(Environment.ProcessPath
                ?? throw new InvalidOperationException("process path unavailable"));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"vtk hooks init: cannot locate own binary: {ex.Message}");
            return 1;
        }
        var content = CopilotConfigJson(exe);

        // --print is side-effect-free: emit the config for the user to wire
        // by hand, touching no files.
        if (printOnly)
        {
            Console.Out.Write(content);
            return 0;
        }

        if (dir == "")
        {
            try { dir = DefaultCopilotHooksDir(); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"vtk hooks init: {ex.Message}");
                return 1;
            }
        }
        var file = Path.Combine(dir, CopilotFileName);

        if (uninstall)
        {
            if (!File.Exists(file))
            {
                Console.Out.WriteLine($"copilot-cli: no hook present ({file})");
                return 0;
            }
            if (dryRun)
            {
                Console.Out.WriteLine($"copilot-cli: removed (dry-run) ({file})");
                return 0;
            }
            try { File.Delete(file); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"vtk hooks init: {ex.Message}");
                return 1;
            }
            Console.Out.WriteLine($"copilot-cli: removed ({file})");
            return 0;
        }

        var old = File.Exists(file) ? File.ReadAllText(file) : null;
        if (old == content)
        {
            Console.Out.WriteLine($"copilot-cli: unchanged ({file})");
            return 0;
        }
        var action = old is null ? "installed" : "updated";
        if (dryRun)
        {
            Console.Out.WriteLine($"copilot-cli: {action} (dry-run) ({file})");
            return 0;
        }
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(file, content);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"vtk hooks init: {ex.Message}");
            return 1;
        }
        Console.Out.WriteLine($"copilot-cli: {action} ({file})");
        return 0;
    }

    private static int CmdCopilotVerify(string[] args)
    {
        string dir = "";
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--hooks-dir":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("vtk hooks verify: --hooks-dir requires a path");
                        return 2;
                    }
                    i++;
                    dir = args[i];
                    break;
                default:
                    Console.Error.WriteLine($"vtk hooks verify: unexpected argument \"{args[i]}\"");
                    return 2;
            }
        }

        string exe;
        try
        {
            exe = Path.GetFullPath(Environment.ProcessPath
                ?? throw new InvalidOperationException("process path unavailable"));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"vtk hooks verify: cannot locate own binary: {ex.Message}");
            return 1;
        }
        if (dir == "")
        {
            try { dir = DefaultCopilotHooksDir(); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"vtk hooks verify: {ex.Message}");
                return 1;
            }
        }
        var file = Path.Combine(dir, CopilotFileName);

        if (!File.Exists(file))
        {
            Console.Error.WriteLine($"vtk hooks verify: FAIL: hook file not found ({file}) — run `vtk hooks init --copilot`");
            return 1;
        }
        var problems = VerifyCopilotJson(File.ReadAllText(file), exe, File.Exists);
        if (problems.Count == 0)
        {
            Console.Out.WriteLine($"copilot-cli: verified ({file} -> {exe})");
            return 0;
        }
        foreach (var p in problems) Console.Error.WriteLine("vtk hooks verify: FAIL: " + p);
        return 1;
    }

    /// <summary>
    /// Integrity/desync checks over the managed Copilot hook file: version 1,
    /// exactly one vtk-managed preToolUse entry, the expected matcher, both
    /// shell commands present and parseable, the hook's binary on disk, and
    /// that binary being THIS binary. fileExists is injected for
    /// testability. Returns an empty list when verified.
    /// </summary>
    internal static List<string> VerifyCopilotJson(string json, string exe, Func<string, bool> fileExists)
    {
        var problems = new List<string>();
        JsonObject root;
        try { root = ParseRoot(json); }
        catch (JsonException ex)
        {
            problems.Add($"hook file is not valid JSON: {ex.Message}");
            return problems;
        }

        int? version = root["version"] is JsonValue v && v.TryGetValue<int>(out var vi) ? vi : null;
        if (version != 1)
        {
            problems.Add($"hook file version is {(version?.ToString() ?? "missing or non-numeric")} (expected 1)");
        }

        var entries = new List<JsonObject>();
        if (root["hooks"] is JsonObject hooks && hooks["preToolUse"] is JsonArray pre)
        {
            foreach (var e in pre)
            {
                if (e is JsonObject entry && IsVtkCopilotEntry(entry)) entries.Add(entry);
            }
        }
        if (entries.Count == 0)
        {
            problems.Add("no vtk preToolUse hook entry — run `vtk hooks init --copilot`");
            return problems;
        }
        if (entries.Count > 1)
        {
            problems.Add($"{entries.Count} vtk preToolUse hook entries found (expected 1) — run `vtk hooks init --copilot` to collapse them");
        }

        var entry0 = entries[0];
        var matcher = Str(entry0["matcher"]) ?? "";
        if (matcher != CopilotMatcher)
        {
            problems.Add($"hook matcher is \"{matcher}\" (expected \"{CopilotMatcher}\")");
        }

        var exes = new List<string>();
        foreach (var field in new[] { "bash", "powershell" })
        {
            var cmd = Str(entry0[field]);
            if (cmd is null)
            {
                problems.Add($"hook entry has no \"{field}\" command — run `vtk hooks init --copilot`");
                continue;
            }
            var hookExe = ExtractCopilotExe(cmd);
            if (hookExe == "")
            {
                problems.Add($"cannot parse binary path from \"{field}\" hook command: {cmd}");
                continue;
            }
            exes.Add(hookExe);
        }
        foreach (var hookExe in exes.Distinct())
        {
            if (!fileExists(hookExe))
            {
                problems.Add($"hook binary missing: {hookExe}");
            }
            if (!PathsEqual(hookExe, exe))
            {
                problems.Add($"hook points at {hookExe} but this binary is {exe} (desync) — run `vtk hooks init --copilot` from the canonical binary");
            }
        }
        return problems;
    }

    /// <summary>Lenient string read: the value when the node is a JSON string, else null (never throws).</summary>
    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>Reports whether a preToolUse entry carries a vtk-managed rewrite command in any shell field.</summary>
    private static bool IsVtkCopilotEntry(JsonObject entry)
    {
        foreach (var field in new[] { "bash", "powershell", "command" })
        {
            if (Str(entry[field]) is string cmd
                && cmd.TrimEnd().EndsWith(CopilotCommandMarker, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Extracts the executable path from a rendered Copilot hook command (optionally "&amp; "-prefixed), quoted or bare.</summary>
    internal static string ExtractCopilotExe(string cmd)
    {
        cmd = cmd.Trim();
        if (cmd.StartsWith("& ", StringComparison.Ordinal)) cmd = cmd[2..].TrimStart();
        if (!cmd.EndsWith(CopilotCommandMarker, StringComparison.Ordinal)) return "";
        var exe = cmd[..^CopilotCommandMarker.Length].Trim();
        if (exe.Length >= 2 && exe[0] == exe[^1] && (exe[0] == '"' || exe[0] == '\'')) exe = exe[1..^1];
        return exe;
    }

    /// <summary>
    /// Pure Copilot rewrite core: camelCase preToolUse input JSON + own
    /// binary path in, `modifiedArgs` output JSON out, or null for "no
    /// rewrite" (passthrough — Copilot CLI treats empty output as default
    /// behavior). Only the two shell tools are rewritten, and only for a
    /// single plain top-level intercepted-family invocation. No permission decision
    /// is ever emitted.
    /// </summary>
    internal static string? RewriteCopilotToolCall(string inputJson, string exe)
    {
        JsonObject? root;
        try { root = JsonNode.Parse(inputJson) as JsonObject; }
        catch (JsonException) { return null; }
        if (root is null) return null;

        if (Str(root["toolName"]) is not string toolName) return null;
        if (root["toolArgs"] is not JsonObject ta) return null;
        if (Str(ta["command"]) is not string command) return null;

        var rewritten = RewriteCopilotCommand(toolName, command, exe);
        if (rewritten is null) return null;

        var modified = ta.DeepClone().AsObject();
        modified["command"] = rewritten;
        return new JsonObject { ["modifiedArgs"] = modified }.ToJsonString();
    }

    /// <summary>
    /// Shell-aware rewrite for Copilot CLI's runtime tools: "bash" gets the
    /// bash prefix (MSYS path on Windows), "powershell" gets the
    /// call-operator prefix on the native path. null means leave the command
    /// untouched.
    /// </summary>
    internal static string? RewriteCopilotCommand(string toolName, string command, string exe)
    {
        switch (toolName)
        {
            case "bash":
                return RewriteCommand(command, exe);
            case "powershell":
                // Core families only — see BashFamilies for why the POSIX
                // utilities are excluded here.
                var trimmed = EligibleFamilyCommand(command, Families);
                if (trimmed is null) return null;
                // PowerShell expression-mode triggers on top of the shared
                // banned set: parens/braces/@ start subexpressions, script
                // blocks, hashtables, and splatting — any of which would
                // change how the prefixed command parses.
                if (trimmed.IndexOfAny(new[] { '(', ')', '{', '}', '@' }) >= 0) return null;
                return "& " + PsQuote(exe) + " " + trimmed;
            default:
                return null;
        }
    }

    /// <summary>Single-quotes a path for PowerShell (literal string; embedded single quotes doubled).</summary>
    internal static string PsQuote(string s) => "'" + s.Replace("'", "''") + "'";
}
