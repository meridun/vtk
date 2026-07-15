using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Vtk.Tests.Smoke;

/// <summary>
/// Exercises vtk end-to-end through the real published binary and real
/// wrapped commands. Mirrors test/smoke/smoke_test.go's harness: an isolated
/// spool home (LocalAppData override) and a scratch git repo, so runs never
/// touch the real shared vtk store.
/// </summary>
public sealed class SmokeHarness : IDisposable
{
    private static readonly Regex OkRe = new(@"(?m)^OK ([0-9a-f]{4})$", RegexOptions.Compiled);

    public string Bin { get; }
    public string Home { get; }
    public string Repo { get; }
    public string Other { get; }

    public SmokeHarness()
    {
        Bin = Environment.GetEnvironmentVariable("VTK_SMOKE_BIN")
            ?? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Vtk.Cli", "bin", "smoke", "vtk.exe");
        Bin = Path.GetFullPath(Bin);
        if (!File.Exists(Bin))
            throw new FileNotFoundException($"smoke binary not found: {Bin} (run `dotnet publish Vtk.Cli -c Release -o Vtk.Cli/bin/smoke` first, or set VTK_SMOKE_BIN)");

        Home = MakeTempDir();
        Repo = MakeTempDir();
        Other = MakeTempDir();

        Git(Repo, "init", "-q", "-b", "main");
        Git(Repo, "config", "user.email", "smoke@vtk");
        Git(Repo, "config", "user.name", "smoke");
        for (var i = 1; i <= 3; i++)
        {
            var name = $"file{i}.txt";
            File.WriteAllText(Path.Combine(Repo, name), $"line {i} content\n");
            Git(Repo, "add", ".");
            Git(Repo, "commit", "-q", "-m", $"commit {i}: add {name}");
        }
    }

    private static string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vtk-smoke-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    public string SpoolDir => Path.Combine(Home, "vtk", "spool");

    public static string Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = dir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)}: exit {proc.ExitCode}\n{stdout}{stderr}");
        return stdout;
    }

    /// <summary>Runs the vtk binary with an isolated cache dir. Returns combined stdout, stderr, and exit code.</summary>
    public (string stdout, string stderr, int code) Run(string dir, params string[] args)
    {
        var psi = BasePsi(dir, args);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return (stdout, stderr, proc.ExitCode);
    }

    /// <summary>
    /// Like Run but sends the child's stdout to the OS null device (NUL on
    /// Windows) — a char device, not a pipe. This is the shape that must not
    /// be mistaken for an interactive TTY.
    /// </summary>
    public (string stderr, int code) RunNull(string dir, params string[] args)
    {
        var psi = BasePsi(dir, args);
        psi.RedirectStandardError = true;
        // Redirect stdout to NUL by inheriting a handle opened on the null
        // device rather than a pipe — ProcessStartInfo has no direct knob
        // for this, so route through cmd.exe's own redirection instead.
        // Uses the raw Arguments string (not ArgumentList): ArgumentList
        // re-escapes each element independently, which mangles an
        // already-quoted "/c <command> > NUL" command line.
        var exeArgs = string.Join(" ", new[] { $"\"{Bin}\"" }.Concat(args.Select(Quote)));
        psi.FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
        psi.ArgumentList.Clear();
        psi.Arguments = $"/c \"{exeArgs} > NUL\"";

        using var proc = Process.Start(psi)!;
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return (stderr, proc.ExitCode);
    }

    private static string Quote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;

    private string? _fakeBin;

    /// <summary>
    /// A scratch bin dir of fake external tools (eslint, gh, mocha, npx, npm,
    /// dbmate, ls, grep, find), lazily materialized as .cmd shims around the
    /// vtk-faketool binary the test project references. Prepended to the
    /// child PATH by <see cref="RunFaked"/>, this is the C# analog of the Go
    /// smoke suite's per-tool fakes compiled into a temp dir — vtk resolves
    /// them through its normal PATH + PATHEXT lookup (.cmd via cmd.exe), so
    /// the whole capture/filter/spool path is exercised end to end.
    /// </summary>
    public string FakeBinDir
    {
        get
        {
            if (_fakeBin != null) return _fakeBin;
            var dll = Path.Combine(AppContext.BaseDirectory, "vtk-faketool.dll");
            if (!File.Exists(dll))
                throw new FileNotFoundException($"vtk-faketool.dll not found beside the tests: {dll}");
            var dir = MakeTempDir();
            foreach (var name in new[] { "eslint", "gh", "mocha", "npx", "npm", "dbmate", "ls", "grep", "find" })
            {
                File.WriteAllText(Path.Combine(dir, name + ".cmd"),
                    $"@dotnet \"{dll}\" --as {name} %*\r\n");
            }
            _fakeBin = dir;
            return dir;
        }
    }

    /// <summary>
    /// Runs the vtk binary with <see cref="FakeBinDir"/> first on PATH so
    /// argv[0] resolves to a fake tool, plus optional VTK_FAKE_* env
    /// overrides. Returns stdout, stderr, and exit code.
    /// </summary>
    public (string stdout, string stderr, int code) RunFaked(string dir, IDictionary<string, string>? env, params string[] args)
    {
        var psi = BasePsi(dir, args);
        psi.EnvironmentVariables["PATH"] = FakeBinDir + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        if (env != null)
            foreach (var (k, v) in env)
                psi.EnvironmentVariables[k] = v;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = System.Text.Encoding.UTF8;
        psi.StandardErrorEncoding = System.Text.Encoding.UTF8;
        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return (stdout, stderr, proc.ExitCode);
    }

    /// <summary>
    /// Runs a fake tool directly (no vtk) — the parity/savings baseline the
    /// Go smoke's rawTool/rawEslint/rawGh helpers established. Returns
    /// combined stdout+stderr and the exit code.
    /// </summary>
    public (string combined, int code) RunRawTool(IDictionary<string, string>? env, string tool, params string[] args)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "vtk-faketool.dll");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        psi.ArgumentList.Add(dll);
        psi.ArgumentList.Add("--as");
        psi.ArgumentList.Add(tool);
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env != null)
            foreach (var (k, v) in env)
                psi.EnvironmentVariables[k] = v;
        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return (stdout + stderr, proc.ExitCode);
    }

    private ProcessStartInfo BasePsi(string dir, string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Bin,
            WorkingDirectory = dir,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.EnvironmentVariables["LOCALAPPDATA"] = Home;
        psi.EnvironmentVariables["XDG_CACHE_HOME"] = Home;
        psi.EnvironmentVariables["HOME"] = Home;
        return psi;
    }

    public static string MustOkId(string output)
    {
        var m = OkRe.Match(output);
        if (!m.Success) throw new InvalidOperationException($"no OK <id> line in output:\n{output}");
        return m.Groups[1].Value;
    }

    public string InvocationLog() => File.ReadAllText(Path.Combine(Home, "vtk", "invocations.jsonl"));

    public void Dispose()
    {
        foreach (var d in new[] { Home, Repo, Other, _fakeBin })
        {
            if (d == null) continue;
            try { Directory.Delete(d, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
