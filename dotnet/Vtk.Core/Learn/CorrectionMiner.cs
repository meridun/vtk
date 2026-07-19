// Detects fail→succeed correction pairs in a session's command stream (#43).
using Vtk.Core.Session;

namespace Vtk.Core.Learn;

/// <summary>One observed correction: a failed command and the succeeding rewrite of the same base command.</summary>
public sealed record CorrectionPair(string Wrong, string Right, string Base, ErrorType Error);

public static class CorrectionMiner
{
    /// <summary>How many subsequent commands a failure searches for its correction.</summary>
    public const int DefaultWindow = 10;

    /// <summary>
    /// Scans one session's events in order. A failed command pairs with the
    /// next non-error command sharing the same base token within the
    /// lookahead window. An identical retry that succeeded (flaky command,
    /// not a correction) produces no pair. Pairs never cross sessions —
    /// call per session.
    /// </summary>
    public static List<CorrectionPair> FindPairs(IReadOnlyList<CommandEvent> events, int window = DefaultWindow)
    {
        var pairs = new List<CorrectionPair>();
        for (var i = 0; i < events.Count; i++)
        {
            if (!events[i].IsError) continue;
            var wrong = events[i].Command.Trim();
            var baseCmd = BaseCommand(wrong);
            if (baseCmd == "") continue;
            var end = Math.Min(events.Count, i + 1 + window);
            for (var j = i + 1; j < end; j++)
            {
                if (events[j].IsError) continue;
                var right = events[j].Command.Trim();
                if (BaseCommand(right) != baseCmd) continue;
                if (right != wrong)
                {
                    pairs.Add(new CorrectionPair(wrong, right, baseCmd,
                        ErrorClassifier.Classify(events[i].Output)));
                }
                break; // first same-base success settles this failure either way
            }
        }
        return pairs;
    }

    /// <summary>
    /// The base program of a shell command line: leading <c>cd … &amp;&amp;</c>
    /// segments and leading VAR=value assignments are skipped so
    /// <c>cd x &amp;&amp; git status</c> bases as <c>git</c>. Empty when no
    /// program token is found.
    /// </summary>
    public static string BaseCommand(string command)
    {
        var segment = command;
        foreach (var seg in command.Split("&&"))
        {
            var first = FirstProgramToken(seg);
            if (first != "cd" && first != "")
            {
                segment = seg;
                break;
            }
            segment = seg; // all-cd command lines base as "cd"
        }
        return FirstProgramToken(segment);
    }

    private static string FirstProgramToken(string segment)
    {
        foreach (var tok in segment.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (IsEnvAssignment(tok)) continue;
            return tok;
        }
        return "";
    }

    private static bool IsEnvAssignment(string tok)
    {
        var eq = tok.IndexOf('=');
        if (eq <= 0) return false;
        for (var i = 0; i < eq; i++)
        {
            var c = tok[i];
            if (!char.IsAsciiLetterOrDigit(c) && c != '_') return false;
        }
        return true;
    }
}
