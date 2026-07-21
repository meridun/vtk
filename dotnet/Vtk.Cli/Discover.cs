// vtk discover — rule-based missed-optimization report over Claude Code
// session history (#44). Layered on `gaps` per the registry decision: gaps
// stays the live execution-time telemetry, discover is the post-hoc analysis
// pass on the shared session provider (#43). Read-only over agent-owned
// transcripts: no process spawning, no network, no disk writes — the report
// goes to stdout only, so no output content lands in persisted metadata.
using Vtk.Core.Discover;
using Vtk.Core.Filter;
using Vtk.Core.Session;

namespace Vtk.Cli;

public static class Discover
{
    internal const int DefaultTop = 20;

    private const string Usage = "usage: vtk discover [--sessions <dir>] [--top <N>]";

    public static int Run(string[] args) => Run(args, Console.Out, Console.Error);

    /// <summary>
    /// Testable entry point: exit 0 on success (including "no opportunities"),
    /// 1 on environment failure (no session transcripts), 2 on usage errors —
    /// the same convention as show/gaps/gain/learn.
    /// </summary>
    internal static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        string? sessionsDir = null;
        var top = DefaultTop;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--sessions":
                    if (i + 1 >= args.Length)
                    {
                        stderr.WriteLine("vtk discover: --sessions requires a directory");
                        return 2;
                    }
                    i++;
                    sessionsDir = args[i];
                    break;
                case "--top":
                    if (i + 1 >= args.Length)
                    {
                        stderr.WriteLine("vtk discover: --top requires a value");
                        return 2;
                    }
                    i++;
                    if (!int.TryParse(args[i], out top) || top < 1)
                    {
                        stderr.WriteLine($"vtk discover: invalid --top \"{args[i]}\" (want >= 1)");
                        return 2;
                    }
                    break;
                default:
                    stderr.WriteLine($"vtk discover: unexpected argument \"{args[i]}\"");
                    stderr.WriteLine(Usage);
                    return 2;
            }
        }

        sessionsDir ??= SessionProvider.SessionDirFor(Directory.GetCurrentDirectory());
        var files = SessionProvider.SessionFiles(sessionsDir);
        if (files.Count == 0)
        {
            stderr.WriteLine($"vtk discover: no session transcripts in {sessionsDir}");
            return 1;
        }

        var commands = 0;
        var events = new List<CommandEvent>();
        foreach (var file in files)
        {
            try
            {
                var read = SessionReader.ReadCommands(File.ReadLines(file));
                commands += read.Count;
                events.AddRange(read);
            }
            catch (IOException)
            {
                // transcript in use / vanished: best-effort scan
            }
        }

        var registry = Registry.Default();
        bool Covered(string[] argv, out string name)
        {
            if (registry.TryLookup(argv, out var entry))
            {
                name = entry.Name;
                return true;
            }
            name = "";
            return false;
        }
        var opportunities = OpportunityMiner.Mine(events, Covered, DiscoverRules.Default());

        if (opportunities.Count == 0)
        {
            stdout.WriteLine($"no opportunities found ({commands} commands across {files.Count} sessions)");
            return 0;
        }

        stdout.WriteLine($"discover: {opportunities.Count} opportunities from {commands} commands across {files.Count} sessions");
        stdout.WriteLine();
        stdout.WriteLine($"{"CLASS",-10} {"OPPORTUNITY",-24} {"CALLS",7} {"OUTPUT CHARS",12}  EXAMPLE");
        foreach (var o in opportunities.Take(top))
        {
            var cls = o.Class == OpportunityClass.Unwrapped ? "unwrapped" : "candidate";
            var example = o.Example.Length <= 40 ? o.Example : o.Example[..37] + "...";
            stdout.WriteLine($"{cls,-10} {o.Name,-24} {o.Calls,7} {o.OutputChars,12}  {example}");
        }
        stdout.WriteLine();
        stdout.WriteLine("unwrapped = a shipped vtk filter covers this shape; run it via vtk to bank the savings");
        stdout.WriteLine("candidate = recurring verbose shape with no filter yet (evidence for a new filter)");
        return 0;
    }
}
