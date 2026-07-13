// The eslint filter: a pure function that compacts the stylish-formatter
// output of `eslint` / `npx eslint`. Raw output in, compact output out;
// returning the input unchanged means "nothing to elide".
//
// eslint reports "problems found" via exit 1 — that large report is the whole
// point to compact. The registry declares eslint's exit-code allowlist as
// {0, 1} (exit 2+ is a fatal/config error and stays raw).
// Port of internal/filter/eslint/eslint.go.
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

    private sealed class RuleAgg
    {
        public string Rule = "";
        public string Severity = ""; // "error" if any occurrence is an error, else "warning"
        public int Count;
        public string Example = ""; // first "file:line:col message" seen for this rule
    }

    /// <summary>
    /// Compacts eslint stylish output into a per-rule rollup:
    /// "&lt;count&gt;x &lt;severity&gt; &lt;rule&gt;  — &lt;file:line:col first message&gt;"
    /// sorted by count descending (ties broken by rule id first-seen order),
    /// followed by the original summary line. Unrecognized input (no problem
    /// lines and no summary) passes through unchanged.
    /// </summary>
    public static string Filter(string raw)
    {
        var lines = raw.Split('\n');

        var curFile = "";
        var byRule = new Dictionary<string, RuleAgg>();
        var order = new List<string>(); // rule ids in first-seen order, for stable ties
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
                    // so it is still counted rather than dropped.
                    rule = "(no rule)";
                }
                sawProblem = true;
                if (!byRule.TryGetValue(rule, out var agg))
                {
                    agg = new RuleAgg { Rule = rule, Severity = sev };
                    byRule[rule] = agg;
                    order.Add(rule);
                    var loc = curFile == "" ? "?" : curFile;
                    agg.Example = $"{loc}:{lineNo}:{col} {msg}";
                }
                agg.Count++;
                if (sev == "error") agg.Severity = "error";
                continue;
            }
            // A non-empty, non-indented, non-summary line is a file header.
            var t = line.Trim();
            if (t != "" && !line.StartsWith(' '))
                curFile = t;
        }

        if (!sawProblem && summary == "")
            return raw; // unrecognized shape: pass through unchanged

        var firstSeen = new Dictionary<string, int>(order.Count);
        for (var i = 0; i < order.Count; i++) firstSeen[order[i]] = i;

        var aggs = byRule.Values.ToList();
        aggs.Sort((a, b) => a.Count != b.Count
            ? b.Count.CompareTo(a.Count)
            : firstSeen[a.Rule].CompareTo(firstSeen[b.Rule]));

        var outLines = new List<string>();
        foreach (var a in aggs)
            outLines.Add($"{a.Count}x {a.Severity} {a.Rule}  — {a.Example}");
        if (summary != "") outLines.Add(summary);
        return string.Join("\n", outLines);
    }
}
