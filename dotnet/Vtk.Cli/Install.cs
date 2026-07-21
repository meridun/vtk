// `vtk install` — wire the token-killer wrappers into a shell rc/profile so
// the intercepted tool families (git/gh/npm/winget/choco/reg — same set as
// Hooks.Families) route through vtk inside Claude Code sessions without the
// agent having to prefix every command. The wrappers are guarded on $CLAUDECODE, so
// the block is inert in normal interactive shells, and point at THIS
// binary's own path, so install is self-locating. The managed region is
// delimited by markers so re-running is idempotent (same binary path →
// byte-identical file) and --uninstall is exact.
// Port of cmd/vtk/install.go.
using System.Diagnostics;

namespace Vtk.Cli;

public static class Install
{
    private const string MarkerBegin = "# >>> vtk wrappers >>>";
    private const string MarkerEnd = "# <<< vtk wrappers <<<";
    private const string ManagedNote = "  (managed by `vtk install` — do not edit between the markers)";

    /// <summary>One resolved rc/profile file and the block to splice into it.</summary>
    internal sealed record ShellTarget(string Name, string Path, string Block);

    public static int Run(string[] args)
    {
        string only = "";
        bool dryRun = false, uninstall = false, printOnly = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--shell":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("vtk install: --shell requires bash or pwsh");
                        return 2;
                    }
                    i++;
                    only = args[i];
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--uninstall":
                    uninstall = true;
                    break;
                case "--print":
                    printOnly = true;
                    break;
                default:
                    Console.Error.WriteLine($"vtk install: unexpected argument \"{args[i]}\"");
                    return 2;
            }
        }
        if (only != "" && only != "bash" && only != "pwsh")
        {
            Console.Error.WriteLine($"vtk install: --shell must be bash or pwsh, got \"{only}\"");
            return 2;
        }

        string exe;
        try
        {
            exe = Path.GetFullPath(Environment.ProcessPath
                ?? throw new InvalidOperationException("process path unavailable"));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"vtk install: cannot locate own binary: {ex.Message}");
            return 1;
        }

        var (targets, warns) = ResolveTargets(only, exe);
        foreach (var w in warns) Console.Error.WriteLine("vtk install: " + w);
        if (targets.Count == 0)
        {
            Console.Error.WriteLine("vtk install: no shell targets resolved");
            return 1;
        }

        // --print is side-effect-free: emit the block(s) for the user to
        // place by hand (or pipe), touching no files.
        if (printOnly)
        {
            foreach (var t in targets)
                Console.Out.Write($"# --- {t.Name} ({t.Path}) ---\n{t.Block}\n");
            return 0;
        }

        var rc = 0;
        foreach (var t in targets)
        {
            try
            {
                var action = ApplyTarget(t, uninstall, dryRun);
                Console.Out.WriteLine($"{t.Name}: {action} ({t.Path})");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"vtk install: {t.Name}: {ex.Message}");
                rc = 1;
            }
        }
        return rc;
    }

    /// <summary>
    /// Locates each requested shell's rc/profile and renders its wrapper
    /// block. Unresolvable shells become warnings (skipped), never hard
    /// failures — installing bash on a box without pwsh should still succeed.
    /// </summary>
    private static (List<ShellTarget> targets, List<string> warns) ResolveTargets(string only, string exe)
    {
        var targets = new List<ShellTarget>();
        var warns = new List<string>();
        bool Want(string s) => only == "" || only == s;

        if (Want("bash"))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home))
            {
                warns.Add("bash: cannot resolve home dir");
            }
            else
            {
                targets.Add(new ShellTarget("bash", Path.Combine(home, ".bashrc"), RenderBash(BashExe(exe))));
            }
        }
        if (Want("pwsh"))
        {
            try
            {
                var p = PwshProfilePath();
                targets.Add(new ShellTarget("pwsh", p, RenderPwsh(exe)));
            }
            catch (Exception ex)
            {
                warns.Add($"pwsh: {ex.Message} (skipped)");
            }
        }
        return (targets, warns);
    }

    /// <summary>
    /// Reads the current file, splices or removes the managed block, and
    /// writes it back. Returns a human action word. Never writes when the
    /// result is byte-identical (reports "unchanged") so re-runs and
    /// --dry-run are honest.
    /// </summary>
    internal static string ApplyTarget(ShellTarget t, bool uninstall, bool dryRun)
    {
        var old = File.Exists(t.Path) ? File.ReadAllText(t.Path) : "";

        string next, action;
        if (uninstall)
        {
            var (res, found) = UninstallBlock(old);
            if (!found) return "no block present";
            next = res;
            action = "removed";
        }
        else
        {
            next = InstallBlockContent(old, t.Block);
            if (next == old) return "unchanged";
            action = old.Contains(MarkerBegin) ? "updated" : "installed";
        }

        if (dryRun) return action + " (dry-run)";

        var dir = Path.GetDirectoryName(t.Path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(t.Path, next);
        return action;
    }

    /// <summary>
    /// Returns file content with exactly one managed block, appended after
    /// the existing (non-managed) content. Idempotent: it first strips any
    /// prior managed block, so re-running with the same binary path is a
    /// fixed point.
    /// </summary>
    internal static string InstallBlockContent(string existing, string block)
    {
        var (baseContent, _) = RemoveBlock(existing);
        baseContent = baseContent.TrimEnd('\n');
        return baseContent == "" ? block + "\n" : baseContent + "\n\n" + block + "\n";
    }

    /// <summary>Removes the managed block and returns the cleaned content and whether a block was present.</summary>
    internal static (string content, bool found) UninstallBlock(string existing)
    {
        var (res, found) = RemoveBlock(existing);
        if (!found) return (existing, false);
        res = res.TrimEnd('\n');
        return res == "" ? ("", true) : (res + "\n", true);
    }

    /// <summary>Excises the marker-delimited region (inclusive) and trims the newlines that bordered it so no doubled blank lines are left behind.</summary>
    internal static (string content, bool found) RemoveBlock(string existing)
    {
        var b = existing.IndexOf(MarkerBegin, StringComparison.Ordinal);
        if (b < 0) return (existing, false);
        var e = existing.IndexOf(MarkerEnd, StringComparison.Ordinal);
        if (e < b) return (existing, false);
        var end = e + MarkerEnd.Length;
        var before = existing[..b].TrimEnd('\n');
        var after = existing[end..].TrimStart('\n');
        if (before == "" && after == "") return ("", true);
        if (before == "") return (after, true);
        if (after == "") return (before + "\n", true);
        return (before + "\n\n" + after, true);
    }

    /// <summary>Renders the POSIX-shell wrapper block. exe must already be in a form the shell's test/exec understands (see BashExe).</summary>
    internal static string RenderBash(string exe) => string.Join("\n", new[]
    {
        MarkerBegin + ManagedNote,
        $"if [ -n \"$CLAUDECODE\" ] && [ -x \"{exe}\" ]; then",
        $"  vtk() {{ \"{exe}\" \"$@\"; }}",
        "  git() { vtk git \"$@\"; }",
        "  gh()  { vtk gh \"$@\"; }",
        "  npm() { vtk npm \"$@\"; }",
        "  winget() { vtk winget \"$@\"; }",
        "  choco()  { vtk choco \"$@\"; }",
        "  reg()    { vtk reg \"$@\"; }",
        "fi",
        MarkerEnd,
    });

    /// <summary>Renders the PowerShell wrapper block. The exe path is embedded in a single-quoted PS string (literal; embedded quotes doubled), so Windows backslashes need no escaping.</summary>
    internal static string RenderPwsh(string exe)
    {
        var q = "'" + exe.Replace("'", "''") + "'";
        return string.Join("\n", new[]
        {
            MarkerBegin + ManagedNote,
            $"if ($env:CLAUDECODE -and (Test-Path {q})) {{",
            $"    function vtk {{ & {q} @args }}",
            $"    function git {{ & {q} git @args }}",
            $"    function gh  {{ & {q} gh  @args }}",
            $"    function npm {{ & {q} npm @args }}",
            $"    function winget {{ & {q} winget @args }}",
            $"    function choco  {{ & {q} choco  @args }}",
            $"    function reg    {{ & {q} reg    @args }}",
            "}",
            MarkerEnd,
        });
    }

    /// <summary>Converts the binary path to the form a bash test/exec expects. On Windows that means the MSYS/Git-Bash form (/c/Users/... not C:\Users\...); elsewhere the native path is already correct.</summary>
    private static string BashExe(string exe) =>
        OperatingSystem.IsWindows() ? WinToMsys(exe) : exe;

    /// <summary>Rewrites a Windows path to the MSYS form Git Bash understands: C:\Users\x\vtk.exe -&gt; /c/Users/x/vtk.exe.</summary>
    internal static string WinToMsys(string p)
    {
        if (p.Length >= 2 && p[1] == ':')
        {
            var drive = char.ToLowerInvariant(p[0]);
            return "/" + drive + p[2..].Replace('\\', '/');
        }
        return p.Replace('\\', '/');
    }

    /// <summary>Resolves the current user's all-hosts PowerShell profile path by asking pwsh itself. Falls back to Windows PowerShell 5.1 if pwsh 7+ is not installed.</summary>
    private static string PwshProfilePath()
    {
        foreach (var bin in new[] { "pwsh", "powershell" })
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = bin,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-Command");
                psi.ArgumentList.Add("$PROFILE.CurrentUserAllHosts");

                using var proc = Process.Start(psi);
                if (proc is null) continue;
                var output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit();
                if (proc.ExitCode != 0) continue;
                var p = output.Trim();
                if (p != "") return p;
            }
            catch
            {
                // bin not found or failed to start: try the next candidate
            }
        }
        throw new InvalidOperationException("PowerShell not found on PATH");
    }
}
