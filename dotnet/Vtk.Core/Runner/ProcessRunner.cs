// Spawns the wrapped command, streams/captures output, propagates exit code.
// Port of the exec.Command usage in cmd/vtk/main.go.
using System.Diagnostics;

namespace Vtk.Core.Runner;

/// <summary>Result of running a child process with output captured (not streamed to the console).</summary>
public sealed class CapturedResult
{
    public required int ExitCode { get; init; }
    public required string Stdout { get; init; }
    public required string Stderr { get; init; }
    /// <summary>True when the child process never started (exit code is the synthetic 127, not a child's).</summary>
    public bool SpawnFailed { get; init; }
    public string Combined => Stdout + Stderr;
}

public static class ProcessRunner
{
    /// <summary>
    /// Resolves argv[0] to a concrete executable path the same way a shell
    /// PATH lookup would, including PATHEXT extensions (.exe, .cmd, .bat, ...)
    /// — necessary on Windows because CreateProcess cannot launch a .cmd/.bat
    /// script directly; those must run through cmd.exe.
    /// </summary>
    public static string? ResolveExecutable(string name)
    {
        if (Path.IsPathRooted(name) && File.Exists(name)) return name;

        var pathExt = Environment.GetEnvironmentVariable("PATHEXT")?.Split(';')
            ?? new[] { ".COM", ".EXE", ".BAT", ".CMD" };
        var hasExt = pathExt.Any(ext => name.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        foreach (var dir in dirs)
        {
            if (dir.Length == 0) continue;
            if (hasExt)
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate)) return candidate;
                continue;
            }
            foreach (var ext in pathExt)
            {
                var candidate = Path.Combine(dir, name + ext);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    /// <summary>
    /// Decides whether the child's stdin is redirected to a pipe vtk closes
    /// at once (child sees EOF) or inherited from vtk's own stdin handle
    /// (child reads exactly what the shell handed vtk). Redirect-and-close
    /// only when vtk's stdin is an interactive console AND the child's
    /// output is captured or counted: under capture the user cannot see a
    /// prompt, so a blocked read would be an invisible hang. Everywhere else
    /// — TTY bypass (prompts must work) or a piped/file/NUL stdin
    /// (<c>git commit -F -</c>, <c>gh --body-file -</c>) — inherit, so the
    /// wrapper never alters the child's input (#144).
    /// </summary>
    internal static bool ShouldRedirectStdin(bool tty, bool stdinRedirected) => !tty && !stdinRedirected;

    private static ProcessStartInfo BuildStartInfo(IReadOnlyList<string> argv, bool redirectStdin)
    {
        var resolved = ResolveExecutable(argv[0]) ?? argv[0];
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardInput = redirectStdin,
        };

        // CreateProcess cannot launch .cmd/.bat scripts directly; route those
        // through cmd.exe /c, same trick a real shell performs transparently.
        if (resolved.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
            resolved.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            psi.FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
            // cmd.exe does NOT parse its command line by the C-runtime rules
            // that ArgumentList encodes. When the script path holds a space it
            // gets quoted, and any additional quoted token (e.g. a multi-word
            // arg) pushes the line past two quote chars — at which point cmd /c
            // strips the OUTER pair and runs the rest verbatim, splitting a
            // spaced path ("C:\Program Files\...\npm.cmd" -> `C:\Program` is not
            // recognized). Build the line by cmd's own rules instead: quote each
            // token, wrap the whole thing in a sacrificial outer pair, and pass
            // /s so cmd strips exactly that pair and runs the remainder as-is.
            psi.Arguments = BuildCmdArguments(resolved, argv);
        }
        else
        {
            psi.FileName = resolved;
            for (var i = 1; i < argv.Count; i++) psi.ArgumentList.Add(argv[i]);
        }
        return psi;
    }

    private static readonly char[] CmdQuoteTriggers =
        { ' ', '\t', '"', '&', '|', '<', '>', '^', '(', ')' };

    /// <summary>
    /// Builds the raw <c>cmd.exe</c> argument string for launching a .cmd/.bat
    /// script: <c>/s /c "&lt;quoted script&gt; &lt;quoted args...&gt;"</c>. The
    /// outer quote pair is sacrificial — <c>/s</c> tells cmd to strip the first
    /// and last character and treat everything between literally, so the inner
    /// per-token quoting survives intact (fixes the spaced-path + multi-word-arg
    /// break where cmd /c would otherwise strip the wrong quote pair).
    /// </summary>
    internal static string BuildCmdArguments(string resolved, IReadOnlyList<string> argv)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(QuoteForCmd(resolved));
        for (var i = 1; i < argv.Count; i++)
        {
            sb.Append(' ');
            sb.Append(QuoteForCmd(argv[i]));
        }
        return $"/s /c \"{sb}\"";
    }

    /// <summary>
    /// Quotes one token for a cmd.exe command line: wraps it in double quotes
    /// when it is empty or contains whitespace, a quote, or a cmd
    /// metacharacter (&amp;|&lt;&gt;^()), escaping any embedded quote with the
    /// C-runtime backslash convention the wrapped program's own parser
    /// understands. Bare tokens pass through unchanged so simple lines stay
    /// readable. Not handled: a literal '%' (cmd variable expansion has no
    /// command-line-safe escape outside a batch file).
    /// </summary>
    internal static string QuoteForCmd(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny(CmdQuoteTriggers) < 0) return arg;
        return "\"" + arg.Replace("\"", "\\\"") + "\"";
    }

    /// <summary>Runs argv with stdout/stderr captured to strings (for filtering). Returns exit 127 if the command could not run.</summary>
    public static CapturedResult RunCaptured(IReadOnlyList<string> argv)
    {
        var psi = BuildStartInfo(argv, ShouldRedirectStdin(tty: false, Console.IsInputRedirected));
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        // Decode captured output as UTF-8, not the ambient console codepage:
        // wrapped tools (git, eslint, mocha, gh, ...) emit UTF-8 to pipes, and
        // a legacy-codepage decode mangles non-ASCII bytes ("✔" -> "Γ£ö")
        // before a filter ever sees them. The Go binary treated captured
        // bytes opaquely; UTF-8 in (here) / UTF-8 out (Program.Run) is the
        // C# equivalent of that transparency.
        psi.StandardOutputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        psi.StandardErrorEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        try
        {
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("process failed to start");
            if (psi.RedirectStandardInput) proc.StandardInput.Close();
            // Drain both pipes concurrently. Sequential ReadToEnd calls deadlock:
            // a child that fills the ~4KB stderr buffer while we are still blocked
            // on stdout stalls in its stderr write and never closes stdout (#94 —
            // wedged every `vtk npm run e2e:local` via rolldown-vite's stderr
            // warnings). Same pattern as RunPassthroughCounted's paired copies.
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            Task.WaitAll(stdoutTask, stderrTask);
            proc.WaitForExit();
            return new CapturedResult { ExitCode = proc.ExitCode, Stdout = stdoutTask.Result, Stderr = stderrTask.Result };
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.Error.WriteLine($"vtk: {ex.Message}");
            return new CapturedResult { ExitCode = 127, Stdout = "", Stderr = "", SpawnFailed = true };
        }
    }

    /// <summary>
    /// Runs argv with stdout/stderr inherited directly (TTY passthrough) and
    /// returns the exit code, counting bytes written. <paramref name="spawnFailed"/>
    /// is true when the child never started (the synthetic 127 return, #118),
    /// so callers can log the invocation as a spawn failure, not a coverage gap.
    /// </summary>
    public static int RunPassthroughCounted(IReadOnlyList<string> argv, bool tty, out long bytesWritten, out bool spawnFailed)
    {
        var psi = BuildStartInfo(argv, ShouldRedirectStdin(tty, Console.IsInputRedirected));
        long total = 0;
        spawnFailed = false;

        if (tty)
        {
            psi.RedirectStandardOutput = false;
            psi.RedirectStandardError = false;
        }
        else
        {
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
        }

        try
        {
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("process failed to start");
            if (psi.RedirectStandardInput) proc.StandardInput.Close();
            if (!tty)
            {
                var stdoutTask = CopyCountedAsync(proc.StandardOutput.BaseStream, Console.OpenStandardOutput());
                var stderrTask = CopyCountedAsync(proc.StandardError.BaseStream, Console.OpenStandardError());
                Task.WaitAll(stdoutTask, stderrTask);
                total = stdoutTask.Result + stderrTask.Result;
            }
            proc.WaitForExit();
            bytesWritten = total;
            return proc.ExitCode;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.Error.WriteLine($"vtk: {ex.Message}");
            bytesWritten = 0;
            spawnFailed = true;
            return 127;
        }
    }

    private static async Task<long> CopyCountedAsync(Stream source, Stream dest)
    {
        long total = 0;
        var buffer = new byte[8192];
        int read;
        while ((read = await source.ReadAsync(buffer)) > 0)
        {
            await dest.WriteAsync(buffer.AsMemory(0, read));
            total += read;
        }
        return total;
    }
}
