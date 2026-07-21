// Post-hoc missed-optimization mining over session command events (#44).
// Layered per the registry decision: `gaps` stays the live execution-time
// telemetry; discover reasons over agent session history about commands that
// *could* have been compacted. Pure analysis: no process spawning, no
// filesystem access, no wrap-path involvement.
using Vtk.Core.Filter;
using Vtk.Core.Session;

namespace Vtk.Core.Discover;

/// <summary>
/// How an opportunity would be banked: <c>Unwrapped</c> — a shipped vtk
/// filter already covers the shape, the command just ran bare (wrap it with
/// vtk to save today); <c>Candidate</c> — a rule-matched verbose shape with
/// no filter yet (evidence for a new filter).
/// </summary>
public enum OpportunityClass { Unwrapped, Candidate }

/// <summary>
/// One ranked opportunity: the covering filter's registry name (Unwrapped)
/// or the rule name (Candidate), observed call count and output size
/// (char length of the tool results the agent actually read — the tokens at
/// stake), and a representative command segment.
/// </summary>
public sealed record Opportunity(
    string Name, OpportunityClass Class, int Calls, long OutputChars, string Example, string Hint);

/// <summary>
/// Shipped-filter coverage probe (Registry.TryLookup shape): true when a
/// filter covers <paramref name="argv"/>, yielding its registry name.
/// Injected so the miner is testable without the real registry.
/// </summary>
public delegate bool CoverageLookup(string[] argv, out string filterName);

public static class OpportunityMiner
{
    /// <summary>
    /// Classifies session command events into ranked opportunities. Error
    /// events are skipped (failures emit raw by invariant 1 — nothing to
    /// compact); so are `vtk`-wrapped and `cd`/assignment-only segments. A
    /// compound command's output is attributed to its <b>first</b> classified
    /// segment only — never double-counted. Shipped-filter coverage is
    /// probed before the rules, so shapes that already have a filter report
    /// as Unwrapped, never as a Candidate (dedupe against shipped filters).
    /// Ranked by observed output desc, then calls desc, then name.
    /// </summary>
    public static List<Opportunity> Mine(
        IEnumerable<CommandEvent> events, CoverageLookup covered, IReadOnlyList<DiscoverRule> rules)
    {
        var agg = new Dictionary<(OpportunityClass Class, string Name), (int Calls, long Chars, string Example, string Hint)>();
        foreach (var ev in events)
        {
            if (ev.IsError) continue;
            foreach (var argv in Segments(ev.Command))
            {
                if (!Classify(argv, covered, rules, out var cls, out var name, out var hint)) continue;
                var key = (cls, name);
                var example = string.Join(" ", argv);
                agg[key] = agg.TryGetValue(key, out var cur)
                    ? (cur.Calls + 1, cur.Chars + ev.Output.Length, cur.Example, cur.Hint)
                    : (1, ev.Output.Length, example, hint);
                break; // first classified segment claims the event's output
            }
        }

        var opportunities = new List<Opportunity>();
        foreach (var ((cls, name), (calls, chars, example, hint)) in agg)
        {
            opportunities.Add(new Opportunity(name, cls, calls, chars, example, hint));
        }
        opportunities.Sort((a, b) =>
        {
            if (a.OutputChars != b.OutputChars) return b.OutputChars.CompareTo(a.OutputChars);
            if (a.Calls != b.Calls) return b.Calls.CompareTo(a.Calls);
            return string.CompareOrdinal(a.Name, b.Name);
        });
        return opportunities;
    }

    private static bool Classify(
        string[] argv, CoverageLookup covered, IReadOnlyList<DiscoverRule> rules,
        out OpportunityClass cls, out string name, out string hint)
    {
        cls = default;
        name = "";
        hint = "";
        if (covered(argv, out var filterName))
        {
            cls = OpportunityClass.Unwrapped;
            name = filterName;
            hint = "shipped filter; run via vtk";
            return true;
        }
        var cmd = string.Join(" ", argv);
        foreach (var rule in rules)
        {
            if (rule.Match.IsMatch(cmd))
            {
                cls = OpportunityClass.Candidate;
                name = rule.Name;
                hint = rule.Hint;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Splits a transcript command line into simple-command argvs: a
    /// quote-aware scan over unquoted separators (<c>&amp;&amp; || | ; &amp;</c> and
    /// newlines; <c>&gt;&amp;</c> redirection targets like <c>2&gt;&amp;1</c> do not
    /// split), then per segment a whitespace tokenize plus launcher-prefix
    /// unwrap (cross-env / npx / VAR=x — Prefix.Unwrap, #97). Segments that
    /// are empty, `cd`, or already `vtk`-wrapped are dropped. Best-effort by
    /// design: analysis input, not the wrap path.
    /// </summary>
    internal static List<string[]> Segments(string command)
    {
        var result = new List<string[]>();
        var start = 0;
        var quote = '\0'; // active quote char, or NUL

        void Emit(int end)
        {
            var text = command[start..end].Trim();
            if (text.Length == 0) return;
            var argv = Prefix.Unwrap(text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (argv.Length == 0) return;
            if (argv[0] == "cd" || argv[0] == "vtk") return;
            result.Add(argv);
        }

        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                continue;
            }
            switch (c)
            {
                case '\'':
                case '"':
                    quote = c;
                    break;
                case ';':
                case '\n':
                    Emit(i);
                    start = i + 1;
                    break;
                case '&':
                case '|':
                    if (c == '&' && i > 0 && command[i - 1] == '>') break; // 2>&1
                    Emit(i);
                    start = i + 1;
                    break;
            }
        }
        Emit(command.Length);
        return result;
    }
}
