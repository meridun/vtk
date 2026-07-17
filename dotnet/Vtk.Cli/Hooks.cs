// `vtk hooks` — self-install/verify the Claude Code PreToolUse rewrite hook
// (#45, Claude-Code-only MVP). Replaces the hand-wired .bashrc dogfooding
// path with a first-class, verifiable install against a single canonical
// binary:
//
//   vtk hooks init     splice the hook into ~/.claude/settings.json
//   vtk hooks verify   integrity/desync check of the installed hook
//   vtk hooks rewrite  the hook payload: stdin tool-call JSON in,
//                      updatedInput JSON (or nothing = passthrough) out
//
// Same shape as `vtk install` (Install.cs, #49): self-locating via the
// running executable's path, one managed entry identified by a marker (here
// the command suffix " hooks rewrite" — the JSON analog of the rc-file
// marker block), idempotent re-runs, exact --uninstall, honest "unchanged".
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vtk.Cli;

public static class Hooks
{
    /// <summary>Suffix that marks a PreToolUse command entry as vtk-managed.</summary>
    internal const string CommandMarker = " hooks rewrite";

    /// <summary>Tool families the rewrite hook wraps — same set as the Install.cs shell wrappers.</summary>
    private static readonly HashSet<string> Families = new(StringComparer.Ordinal) { "git", "gh", "npm" };

    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: vtk hooks <init|verify|rewrite> [--dry-run] [--uninstall] [--print] [--settings <path>]");
            return 2;
        }
        return args[0] switch
        {
            "init" => CmdInit(args[1..]),
            "verify" => CmdVerify(args[1..]),
            "rewrite" => CmdRewrite(),
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
    /// The installed hook's entry point: reads the PreToolUse tool-call JSON
    /// from stdin and prints an updatedInput JSON that routes a plain
    /// git/gh/npm command through this binary. Anything unsupported — other
    /// tools, compound/piped/redirected commands, already-wrapped commands,
    /// malformed input, any internal error — emits nothing and exits 0, so
    /// the agent's command runs exactly as typed (passthrough is a feature).
    /// No permissionDecision is ever emitted: the normal permission flow
    /// applies to the updated input; the hook never approves or blocks.
    /// </summary>
    private static int CmdRewrite()
    {
        try
        {
            var input = Console.In.ReadToEnd();
            var exe = Path.GetFullPath(Environment.ProcessPath
                ?? throw new InvalidOperationException("process path unavailable"));
            var output = RewriteToolCall(input, exe);
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
    /// single plain top-level git/gh/npm invocation: any shell metacharacter
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
        var trimmed = command.Trim();
        if (trimmed == "") return null;

        // Conservative eligibility: single simple command only. Rewriting
        // `git log | head` or `git diff > f` would put compacted output where
        // the pipeline expects raw bytes — altered semantics, so passthrough.
        if (trimmed.IndexOfAny(new[] { '|', '&', ';', '<', '>', '`', '$', '\n', '\r' }) >= 0) return null;

        var space = trimmed.IndexOf(' ');
        var first = space < 0 ? trimmed : trimmed[..space];
        if (!Families.Contains(first)) return null;

        return BashQuote(HookExePath(exe)) + " " + trimmed;
    }

    /// <summary>The binary path in the form the Bash tool's shell expects: MSYS on Windows (Git Bash), native elsewhere.</summary>
    private static string HookExePath(string exe) =>
        OperatingSystem.IsWindows() ? Install.WinToMsys(exe) : exe;

    /// <summary>Single-quotes a path for bash (embedded single quotes escaped the POSIX way).</summary>
    internal static string BashQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";
}
