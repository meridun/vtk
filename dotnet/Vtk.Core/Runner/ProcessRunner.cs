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

    private static ProcessStartInfo BuildStartInfo(IReadOnlyList<string> argv)
    {
        var resolved = ResolveExecutable(argv[0]) ?? argv[0];
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
        };

        // CreateProcess cannot launch .cmd/.bat scripts directly; route those
        // through cmd.exe /c, same trick a real shell performs transparently.
        if (resolved.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
            resolved.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            psi.FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(resolved);
            for (var i = 1; i < argv.Count; i++) psi.ArgumentList.Add(argv[i]);
        }
        else
        {
            psi.FileName = resolved;
            for (var i = 1; i < argv.Count; i++) psi.ArgumentList.Add(argv[i]);
        }
        return psi;
    }

    /// <summary>Runs argv with stdout/stderr captured to strings (for filtering). Returns exit 127 if the command could not run.</summary>
    public static CapturedResult RunCaptured(IReadOnlyList<string> argv)
    {
        var psi = BuildStartInfo(argv);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;

        try
        {
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("process failed to start");
            proc.StandardInput.Close();
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            return new CapturedResult { ExitCode = proc.ExitCode, Stdout = stdout, Stderr = stderr };
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.Error.WriteLine($"vtk: {ex.Message}");
            return new CapturedResult { ExitCode = 127, Stdout = "", Stderr = "" };
        }
    }

    /// <summary>Runs argv with stdout/stderr inherited directly (TTY passthrough) and returns the exit code, counting bytes written.</summary>
    public static int RunPassthroughCounted(IReadOnlyList<string> argv, bool tty, out long bytesWritten)
    {
        var psi = BuildStartInfo(argv);
        long total = 0;

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
            proc.StandardInput.Close();
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
