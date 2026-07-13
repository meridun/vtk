// The GitHub CLI (`gh`) filter family: pure functions that compact the
// tab-separated human-format output of `gh` list subcommands. Raw output in,
// compact output out; returning the input unchanged means "nothing to elide".
//
// `gh` list commands emit tab-separated rows when their output is not a TTY
// (which is always the case under vtk's capture). `--json` forms are emitted
// as a JSON object/array and are passed through structurally intact.
// Port of internal/filter/gh/gh.go.
namespace Vtk.Core.Filter;

public static class Gh
{
    // isNum reports whether s is a non-empty run of ASCII digits — the
    // leading field of an issue/PR list row. `gh issue view` / `gh pr view`
    // output leads with "title:" etc., so this guard keeps the filter from
    // mangling view output that reaches the same two-token registry key.
    private static bool IsNum(string s)
    {
        s = s.Trim();
        if (s == "") return false;
        return s.All(c => c is >= '0' and <= '9');
    }

    /// <summary>Reports whether raw looks like a `gh --json` payload, which must pass through unchanged so the JSON stays structurally intact.</summary>
    private static bool IsJson(string raw)
    {
        var t = raw.Trim();
        return t.StartsWith('{') || t.StartsWith('[');
    }

    /// <summary>Splits raw into non-empty lines, each further split on tabs. Returns empty if no tab-delimited row is found (unrecognized shape → caller passes through).</summary>
    private static List<string[]> Rows(string raw)
    {
        var outRows = new List<string[]>();
        foreach (var line in raw.Split('\n'))
        {
            if (line.Trim() == "") continue;
            if (!line.Contains('\t')) continue;
            outRows.Add(line.Split('\t'));
        }
        return outRows;
    }

    /// <summary>Renders an ISO-8601 timestamp as a compact relative age (e.g. "3h", "2d", "5mo") against ref. Unparseable timestamps render empty.</summary>
    private static string Rel(string ts, DateTime refTime)
    {
        if (!DateTime.TryParse(ts.Trim(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var t))
            return "";

        var d = refTime.ToUniversalTime() - t.ToUniversalTime();
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;

        if (d < TimeSpan.FromMinutes(1)) return "now";
        if (d < TimeSpan.FromHours(1)) return (int)d.TotalMinutes + "m";
        if (d < TimeSpan.FromHours(24)) return (int)d.TotalHours + "h";
        if (d < TimeSpan.FromDays(30)) return (int)(d.TotalHours / 24) + "d";
        if (d < TimeSpan.FromDays(365)) return (int)(d.TotalHours / (24 * 30)) + "mo";
        return (int)(d.TotalHours / (24 * 365)) + "y";
    }

    /// <summary>Joins a base line with a "(age)" suffix when age is non-empty.</summary>
    private static string AppendRel(string baseLine, string age) => age == "" ? baseLine : $"{baseLine} ({age})";

    /// <summary>Compacts `gh issue list` (TSV: num, STATE, title, labels, ts) to one "#&lt;n&gt; &lt;state&gt; &lt;title&gt; (&lt;age&gt;)" line per issue, dropping label noise.</summary>
    public static string IssueList(string raw) => IssueListAt(raw, DateTime.UtcNow);

    internal static string IssueListAt(string raw, DateTime refTime)
    {
        if (IsJson(raw)) return raw;
        var rs = Rows(raw);
        if (rs.Count == 0) return raw;

        var outLines = new List<string>();
        foreach (var f in rs)
        {
            if (f.Length < 3 || !IsNum(f[0])) return raw; // not a list row (e.g. `gh issue view`): pass through
            var age = f.Length >= 5 ? Rel(f[4], refTime) : "";
            var line = "#" + f[0] + " " + f[1].ToLowerInvariant() + " " + f[2];
            outLines.Add(AppendRel(line, age));
        }
        return string.Join("\n", outLines);
    }

    /// <summary>Compacts `gh pr list` (TSV: num, title, headBranch, STATE, ts) to one "#&lt;n&gt; &lt;state&gt; &lt;title&gt; (&lt;age&gt;)" line per PR, dropping the head-branch column.</summary>
    public static string PrList(string raw) => PrListAt(raw, DateTime.UtcNow);

    internal static string PrListAt(string raw, DateTime refTime)
    {
        if (IsJson(raw)) return raw;
        var rs = Rows(raw);
        if (rs.Count == 0) return raw;

        var outLines = new List<string>();
        foreach (var f in rs)
        {
            if (f.Length < 4 || !IsNum(f[0])) return raw; // not a list row (e.g. `gh pr view`): pass through
            var age = f.Length >= 5 ? Rel(f[4], refTime) : "";
            var line = "#" + f[0] + " " + f[3].ToLowerInvariant() + " " + f[1];
            outLines.Add(AppendRel(line, age));
        }
        return string.Join("\n", outLines);
    }

    // The set of `gh run list` first-column status values; anything else in
    // field 0 means the input is not a run-list table (pass through).
    private static readonly HashSet<string> RunStatus = new()
    {
        "completed", "in_progress", "queued", "requested", "waiting", "pending",
    };

    /// <summary>Compacts `gh run list` (TSV: status, conclusion, title, workflow, branch, event, runID, elapsed, ts) to one "&lt;conclusion&gt; &lt;title&gt; · &lt;workflow&gt; (&lt;age&gt;)" line per run.</summary>
    public static string RunList(string raw) => RunListAt(raw, DateTime.UtcNow);

    internal static string RunListAt(string raw, DateTime refTime)
    {
        if (IsJson(raw)) return raw;
        var rs = Rows(raw);
        if (rs.Count == 0) return raw;

        var outLines = new List<string>();
        foreach (var f in rs)
        {
            if (f.Length < 4 || !RunStatus.Contains(f[0].Trim())) return raw; // not a run-list row: pass through unchanged
            var state = f[1];
            if (state == "") state = f[0]; // in-progress runs have no conclusion yet
            var age = f.Length >= 9 ? Rel(f[8], refTime) : "";
            var line = state + " " + f[2] + " · " + f[3];
            outLines.Add(AppendRel(line, age));
        }
        return string.Join("\n", outLines);
    }
}
