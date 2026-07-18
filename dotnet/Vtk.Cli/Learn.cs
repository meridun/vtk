// vtk learn — mine Claude Code session JSONL for fail→succeed CLI
// corrections and emit .claude/rules/cli-corrections.md (#43). Read-only
// analysis over agent-owned transcripts: no process spawning, no network,
// wrap-path invariants untouched.
using System.Globalization;
using Vtk.Core.Learn;
using Vtk.Core.Session;

namespace Vtk.Cli;

public static class Learn
{
    internal const double DefaultMinConfidence = 0.5;
    internal const int DefaultMinOccurrences = 1;

    private const string Usage =
        "usage: vtk learn [--min-confidence <0..1>] [--min-occurrences <N>] [--sessions <dir>] [--out <file>] [--dry-run]";

    public static int Run(string[] args) => Run(args, Console.Out, Console.Error);

    /// <summary>
    /// Testable entry point: exit 0 on success (including "nothing mined"),
    /// 1 on environment failure (no session directory / unwritable output),
    /// 2 on usage errors — the same convention as show/gaps/gain.
    /// </summary>
    internal static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        var minConfidence = DefaultMinConfidence;
        var minOccurrences = DefaultMinOccurrences;
        string? sessionsDir = null;
        string? outPath = null;
        var dryRun = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--min-confidence":
                    if (i + 1 >= args.Length)
                    {
                        stderr.WriteLine("vtk learn: --min-confidence requires a value");
                        return 2;
                    }
                    i++;
                    if (!double.TryParse(args[i], NumberStyles.Float, CultureInfo.InvariantCulture, out minConfidence) ||
                        minConfidence < 0 || minConfidence > 1)
                    {
                        stderr.WriteLine($"vtk learn: invalid --min-confidence \"{args[i]}\" (want 0..1)");
                        return 2;
                    }
                    break;
                case "--min-occurrences":
                    if (i + 1 >= args.Length)
                    {
                        stderr.WriteLine("vtk learn: --min-occurrences requires a value");
                        return 2;
                    }
                    i++;
                    if (!int.TryParse(args[i], out minOccurrences) || minOccurrences < 1)
                    {
                        stderr.WriteLine($"vtk learn: invalid --min-occurrences \"{args[i]}\" (want >= 1)");
                        return 2;
                    }
                    break;
                case "--sessions":
                    if (i + 1 >= args.Length)
                    {
                        stderr.WriteLine("vtk learn: --sessions requires a directory");
                        return 2;
                    }
                    i++;
                    sessionsDir = args[i];
                    break;
                case "--out":
                    if (i + 1 >= args.Length)
                    {
                        stderr.WriteLine("vtk learn: --out requires a path");
                        return 2;
                    }
                    i++;
                    outPath = args[i];
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                default:
                    stderr.WriteLine($"vtk learn: unexpected argument \"{args[i]}\"");
                    stderr.WriteLine(Usage);
                    return 2;
            }
        }

        sessionsDir ??= SessionProvider.SessionDirFor(Directory.GetCurrentDirectory());
        outPath ??= RulesFile.DefaultRelativePath;

        var files = SessionProvider.SessionFiles(sessionsDir);
        if (files.Count == 0)
        {
            stderr.WriteLine($"vtk learn: no session transcripts in {sessionsDir}");
            return 1;
        }

        var commands = 0;
        var pairs = new List<CorrectionPair>();
        foreach (var file in files)
        {
            List<CommandEvent> events;
            try
            {
                events = SessionReader.ReadCommands(File.ReadLines(file));
            }
            catch (IOException)
            {
                continue; // transcript in use / vanished: best-effort scan
            }
            commands += events.Count;
            pairs.AddRange(CorrectionMiner.FindPairs(events)); // pairs never cross sessions
        }

        var rules = RuleMiner.Mine(pairs, minConfidence, minOccurrences);
        if (rules.Count == 0)
        {
            stdout.WriteLine($"no correction rules mined ({commands} commands across {files.Count} sessions)");
            return 0;
        }

        var content = RulesFile.Render(rules);
        if (dryRun)
        {
            stdout.Write(content);
            stdout.WriteLine($"(dry-run: {rules.Count} rules, would write {outPath})");
            return 0;
        }
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(outPath, content);
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"vtk learn: cannot write {outPath}: {ex.Message}");
            return 1;
        }
        stdout.WriteLine($"wrote {rules.Count} rules to {outPath} ({commands} commands across {files.Count} sessions)");
        return 0;
    }
}
