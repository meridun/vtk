// The eslint filter: a pure function that compacts the stylish-formatter
// output of `eslint` / `npx eslint` into a per-file compact listing that
// keeps every location (#170). Raw output in, compact output out; returning
// the input unchanged means "nothing to elide".
//
// eslint reports "problems found" via exit 1 — that large report is the whole
// point to compact. The registry declares eslint's exit-code allowlist as
// {0, 1} (exit 2+ is a fatal/config error and stays raw).
// Originally a port of internal/filter/eslint/eslint.go (rollup shape, #7).
using System.Text.RegularExpressions;

namespace Vtk.Core.Filter;

public static partial class Eslint
{
    // A problem line: "  <line>:<col>  <severity>  <message>  <rule-id>".
    // The rule id is the last whitespace-run-separated token; the message is
    // everything between the severity and the rule id. Two-space column gaps
    // in the stylish formatter are collapsed by splitting on runs of spaces.
    [GeneratedRegex(@"^\s+(\d+):(\d+)\s+(error|warning)\s+(.*?)(?:\s{2,}([\w./-]+))?\s*$")]
    private static partial Regex ProblemRe();

    // The trailing summary line, e.g. "✖ 3 problems (2 errors, 1 warning)".
    [GeneratedRegex(@"^[\s✖xX*]*\d+ problems?\b")]
    private static partial Regex SummaryRe();

    private const int PackWidth = 100; // column cap for packed location lines

    private sealed class RuleAgg
    {
        public string Rule = "";
        public bool Error;   // any occurrence is an error
        public bool Warning; // any occurrence is a warning
        public string Message = ""; // first message seen for this rule
        public bool Mixed => Error && Warning;
    }

    private sealed class FileLocs
    {
        public string File = "";
        public List<(string Loc, string Rule, string Severity)> Locs = new();
    }

    /// <summary>
    /// Compacts eslint stylish output into a per-file compact listing: each
    /// file header verbatim, then every "line:col rule" location in source
    /// order packed onto lines up to a width cap, the original summary line,
    /// and one "rules:" trailer naming each rule's severity and first-seen
    /// message. A rule that mixes severities marks each of its locations
    /// inline ("12:5 rule(error)"). Unrecognized input (no problem lines and
    /// no summary) passes through unchanged.
    /// </summary>
    public static string Filter(string raw)
    {
        var lines = raw.Split('\n');

        var files = new List<FileLocs>();
        FileLocs? cur = null;
        var byRule = new Dictionary<string, RuleAgg>();
        var order = new List<string>(); // rule ids in first-seen order
        var summary = "";
        var sawProblem = false;

        foreach (var line in lines)
        {
            if (SummaryRe().IsMatch(line.Trim()))
            {
                summary = line.Trim();
                continue;
            }
            var m = ProblemRe().Match(line);
            if (m.Success)
            {
                var lineNo = m.Groups[1].Value;
                var col = m.Groups[2].Value;
                var sev = m.Groups[3].Value;
                var msg = m.Groups[4].Value.Trim();
                var rule = m.Groups[5].Success ? m.Groups[5].Value : "";
                if (rule == "")
                {
                    // No rule id (e.g. a parse error): bucket under a sentinel
                    // so it is still listed rather than dropped.
                    rule = "(no rule)";
                }
                sawProblem = true;
                if (cur == null)
                {
                    // A problem before any file header: keep it under a placeholder.
                    cur = new FileLocs { File = "?" };
                    files.Add(cur);
                }
                if (!byRule.TryGetValue(rule, out var agg))
                {
                    agg = new RuleAgg { Rule = rule, Message = msg };
                    byRule[rule] = agg;
                    order.Add(rule);
                }
                if (sev == "error") agg.Error = true; else agg.Warning = true;
                cur.Locs.Add(($"{lineNo}:{col}", rule, sev));
                continue;
            }
            // A non-empty, non-indented, non-summary line is a file header.
            var t = line.Trim();
            if (t != "" && !line.StartsWith(' '))
            {
                cur = new FileLocs { File = t };
                files.Add(cur);
            }
        }

        if (!sawProblem && summary == "")
            return raw; // unrecognized shape: pass through unchanged

        var outLines = new List<string>();
        foreach (var f in files)
        {
            if (f.Locs.Count == 0) continue; // a header with no problems (e.g. trailing noise)
            outLines.Add(f.File);
            var packed = "  ";
            foreach (var (loc, rule, sev) in f.Locs)
            {
                var tok = byRule[rule].Mixed ? $"{loc} {rule}({sev})" : $"{loc} {rule}";
                if (packed.Length == 2)
                    packed += tok;
                else if (packed.Length + 2 + tok.Length > PackWidth)
                {
                    outLines.Add(packed);
                    packed = "  " + tok;
                }
                else
                    packed += "  " + tok;
            }
            outLines.Add(packed);
        }
        if (summary != "") outLines.Add(summary);

        var trailer = new List<string>(order.Count);
        foreach (var rule in order)
        {
            var a = byRule[rule];
            var sev = a.Mixed ? "error/warning" : a.Error ? "error" : "warning";
            trailer.Add($"{a.Rule} ({sev}) {a.Message}");
        }
        outLines.Add("rules: " + string.Join("; ", trailer));
        return string.Join("\n", outLines);
    }
}
