// The GitHub CLI (`gh`) filter family: pure functions that compact the
// tab-separated human-format output of `gh` list subcommands and the CI job
// logs of `gh run view --log` / `--log-failed`. Raw output in, compact
// output out; returning the input unchanged means "nothing to elide".
//
// `gh` list commands emit tab-separated rows when their output is not a TTY
// (which is always the case under vtk's capture). `--json` forms are emitted
// as a JSON object/array and are passed through structurally intact.
// Port of internal/filter/gh/gh.go; CI-log fold added per #96.
using System.Text.RegularExpressions;

namespace Vtk.Core.Filter;

public static partial class Gh
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

    /// <summary>
    /// The `gh run` family dispatcher: routes CI job logs (`gh run view
    /// --log` / `--log-failed`) to the log fold and run-list tables to
    /// RunList, by content shape — the registry key ("gh run") cannot see
    /// the sub-subcommand. Anything unrecognized (run-view summaries,
    /// `gh run watch`, error text) passes through unchanged.
    /// </summary>
    public static string Run(string raw)
    {
        if (IsJson(raw)) return raw;
        if (TryRunLogFold(raw, out var folded)) return folded;
        return RunList(raw);
    }

    // A CI log row's content field: an ISO-8601 timestamp (fractional
    // seconds, Z suffix) followed by the log line. The first row of a job's
    // log carries a UTF-8 BOM ahead of the timestamp (observed in real
    // `gh run view --log` captures).
    [GeneratedRegex(@"^﻿?\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?Z? ?")]
    private static partial Regex LogTimestampRe();

    // A definitive failure marker inside a step's log: an Actions error
    // annotation, or the runner's nonzero exit summary when the annotation
    // is absent.
    [GeneratedRegex(@"^Process completed with exit code [1-9]")]
    private static partial Regex ExitCodeRe();

    // ANSI escape sequences (colors etc.) in kept log lines. The raw spool
    // keeps them; the compact view doesn't need them.
    [GeneratedRegex(@"\x1b\[[0-9;]*[A-Za-z]")]
    private static partial Regex AnsiRe();

    // gh renders this placeholder when it cannot map log lines to step
    // names (upstream behavior on current gh; step metadata is often
    // unavailable). It carries no information, so fold labels omit it.
    private const string UnknownStep = "UNKNOWN STEP";

    // One (job, step) run of consecutive log lines. Job == null is the
    // pseudo-section holding non-log lines (e.g. interleaved stderr), kept
    // verbatim in position.
    private sealed class LogSection
    {
        public string? Job;
        public string Step = "";
        public readonly List<string> Lines = new(); // prefix-stripped content
        public bool HasError;
    }

    /// <summary>
    /// Detects and folds `gh run view --log` / `--log-failed` output: rows
    /// of "job\tstep\t&lt;timestamp&gt; content". Consecutive (job, step)
    /// sections without a failure marker fold to one "OK &lt;label&gt; (n
    /// lines)" line (keeping ##[warning] lines); sections with a marker
    /// (##[error] or a nonzero exit summary) stay inline under a FAIL
    /// header, with the job/step/timestamp prefixes and ANSI codes
    /// stripped. When no section carries a marker (e.g. --log-failed for a
    /// job that died without an error annotation) the tail of the log is
    /// kept so the failure evidence stays visible. Returns false when the
    /// input is not log-shaped (caller passes through).
    /// </summary>
    internal static bool TryRunLogFold(string raw, out string folded)
    {
        folded = "";
        var lines = raw.Split('\n');
        var end = lines.Length;
        if (end > 0 && lines[end - 1] == "") end--; // trailing newline artifact

        var sections = new List<LogSection>();
        LogSection? cur = null;
        int logRows = 0, nonEmpty = 0;
        var tail = new List<string>(); // stripped log content, in order

        for (var i = 0; i < end; i++)
        {
            var line = lines[i];
            if (line.Trim() == "") continue;
            nonEmpty++;

            string? job = null, step = null, content = null;
            var t1 = line.IndexOf('\t');
            if (t1 > 0)
            {
                var t2 = line.IndexOf('\t', t1 + 1);
                if (t2 > t1 + 1)
                {
                    var rest = line[(t2 + 1)..];
                    var m = LogTimestampRe().Match(rest);
                    if (m.Success)
                    {
                        job = line[..t1];
                        step = line[(t1 + 1)..t2];
                        content = rest[m.Length..];
                    }
                }
            }

            if (job is null)
            {
                // Not a log row (interleaved stderr &c.): keep verbatim.
                if (cur is null || cur.Job is not null)
                {
                    cur = new LogSection { Job = null };
                    sections.Add(cur);
                }
                cur.Lines.Add(line);
                continue;
            }

            logRows++;
            content = AnsiRe().Replace(content!, "");
            tail.Add(content);
            if (cur is null || cur.Job != job || cur.Step != step)
            {
                cur = new LogSection { Job = job, Step = step! };
                sections.Add(cur);
            }
            cur.Lines.Add(content);
            if (content.Contains("##[error]") || ExitCodeRe().IsMatch(content))
                cur.HasError = true;
        }

        // Engage only on unambiguous log output: enough rows, and nearly all
        // of the input shaped like one. Anything else is not ours to touch.
        if (logRows < 10 || logRows < nonEmpty * 0.9) return false;

        var anyError = sections.Any(s => s.HasError);
        var outLines = new List<string>();
        foreach (var s in sections)
        {
            if (s.Job is null)
            {
                outLines.AddRange(s.Lines);
                continue;
            }
            var label = s.Step == "" || s.Step == UnknownStep ? s.Job : s.Job + " · " + s.Step;
            if (s.HasError)
            {
                outLines.Add($"FAIL {label} ({s.Lines.Count} lines)");
                outLines.AddRange(s.Lines);
            }
            else
            {
                outLines.Add($"OK {label} ({s.Lines.Count} lines)");
                foreach (var l in s.Lines)
                {
                    if (l.Contains("##[warning]")) outLines.Add("  " + l);
                }
            }
        }

        if (!anyError)
        {
            // No definitive failure marker anywhere. For --log-failed this
            // means a job died without an ##[error] annotation (observed:
            // network timeouts) — keep the log tail so the evidence stays
            // inline; the full raw is recoverable via the spool.
            var n = Math.Min(20, tail.Count);
            outLines.Add("");
            outLines.Add($"last {n} log lines:");
            outLines.AddRange(tail.Skip(tail.Count - n));
        }

        folded = string.Join("\n", outLines);
        return true;
    }
}
