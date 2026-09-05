// vtk gaps --file-issues — turn recurring passthrough families surfaced by
// the gap log into stage:intake filter issues, so coverage becomes a tracked
// queue rather than a number nobody reads (#34, ported from Go per #61).
// Filing is explicit and user-invoked: it shells to `gh` (outside the
// no-network non-goal, which binds only the wrap path and telemetry storage —
// human ruling on #34), is dry-run by default, and dedupes against
// already-open Filter issues so re-running never refiles.
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Vtk.Core.Spool;

namespace Vtk.Cli;

/// <summary>Parsed `vtk gaps --file-issues` options.</summary>
internal sealed class FileIssuesOpts
{
    public bool Yes;
    public long MinBytes = GapsIssues.DefaultMinBytes;
    public int MinCalls = GapsIssues.DefaultMinCalls;
    /// <summary>UTC lower bound of the measurement window (`--since`, #140); null = all history.</summary>
    public DateTime? Since;
}

internal static class GapsIssues
{
    // Filing thresholds. Defaults are the intended everyday floor; the hard
    // floors clamp over-eager overrides so filing can never spam trivial
    // gaps (#34).
    internal const long DefaultMinBytes = 51200; // 50 KiB cumulative raw output per family
    internal const int DefaultMinCalls = 3;
    internal const long FloorMinBytes = 4096;
    internal const int FloorMinCalls = 2;

    /// <summary>Both the title stem for filed issues and the token dedupe keys on when scanning existing open issues.</summary>
    internal const string FilterIssueTitlePrefix = "Filter: ";

    /// <summary>
    /// Selects over-threshold gap families, drops any that already have an
    /// open Filter issue, and either prints the plan (dry-run) or files each
    /// via `gh issue create`. Returns nonzero only on a hard error (bad
    /// metadata, unreachable gh, or a failed create) — an empty candidate set
    /// is success.
    /// </summary>
    internal static int Run(Store st, FileIssuesOpts o)
    {
        List<GapSummary> candidates;
        try
        {
            candidates = st.FileIssueGaps(o.MinBytes, o.MinCalls, o.Since);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"vtk: {ex.Message}");
            return 1;
        }
        if (candidates.Count == 0)
        {
            Console.Out.WriteLine($"no gap families over threshold (min-bytes={o.MinBytes}, min-calls={o.MinCalls})");
            return 0;
        }

        HashSet<string> existing;
        try
        {
            existing = GhExistingFilterFamilies();
        }
        catch (Exception ex)
        {
            // Can't dedupe → refuse to file rather than risk duplicate issues.
            Console.Error.WriteLine($"vtk gaps: cannot list existing issues ({ex.Message}); refusing to file");
            return 1;
        }

        var (toFile, skipped) = PlanFilterIssues(candidates, existing);
        foreach (var fam in skipped)
        {
            Console.Out.WriteLine($"skip {fam} — open {FilterIssueTitlePrefix}{fam} issue already exists");
        }
        if (toFile.Count == 0)
        {
            Console.Out.WriteLine("nothing to file (all candidates already have open issues)");
            return 0;
        }

        if (!o.Yes)
        {
            Console.Out.WriteLine($"would file {toFile.Count} intake issue(s) (dry-run; pass --yes to create):");
            foreach (var g in toFile)
            {
                Console.Out.WriteLine($"  {FilterIssueTitle(g.Family)}  [{g.Calls} calls, {g.RawBytes} raw bytes]");
            }
            return 0;
        }

        var rc = 0;
        foreach (var g in toFile)
        {
            string url;
            try
            {
                url = GhCreateFilterIssue(FilterIssueTitle(g.Family), FilterIssueBody(g, o.MinBytes, o.MinCalls, o.Since));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"vtk gaps: failed to file {g.Family}: {ex.Message}");
                rc = 1;
                continue;
            }
            Console.Out.WriteLine($"filed {g.Family} — {url}");
        }
        return rc;
    }

    /// <summary>
    /// Splits candidate families into those to file and those skipped because
    /// an open Filter issue already exists (dedupe → idempotent). Pure: no
    /// I/O, so the selection is unit-testable.
    /// </summary>
    internal static (List<GapSummary> ToFile, List<string> Skipped) PlanFilterIssues(
        IReadOnlyList<GapSummary> candidates, IReadOnlySet<string> existing)
    {
        var toFile = new List<GapSummary>();
        var skipped = new List<string>();
        foreach (var g in candidates)
        {
            if (existing.Contains(g.Family))
            {
                skipped.Add(g.Family);
                continue;
            }
            toFile.Add(g);
        }
        return (toFile, skipped);
    }

    /// <summary>The issue title for a gap family. The prefix is fixed so ParseExistingFilterFamilies can recover the family for dedupe.</summary>
    internal static string FilterIssueTitle(string family) =>
        FilterIssueTitlePrefix + family + " (auto-filed from vtk gaps)";

    /// <summary>Renders the intake issue body from metadata only: family name plus call/byte counts (invariant 3 — never output content).</summary>
    internal static string FilterIssueBody(GapSummary g, long minBytes, int minCalls, DateTime? since = null)
    {
        var b = new StringBuilder();
        b.Append($"Auto-filed by `vtk gaps --file-issues`: the `{g.Family}` command family is passing through unfiltered at meaningful volume.\n\n");
        b.Append("## Measured gap\n\n");
        b.Append($"- Family: `{g.Family}`\n");
        b.Append($"- Passthrough calls: {g.Calls}\n");
        b.Append($"- Cumulative raw bytes: {g.RawBytes}\n");
        b.Append($"- Threshold at filing: min-bytes={minBytes}, min-calls={minCalls}\n");
        if (since is { } s) b.Append($"- Window: since {SinceSpec.Format(s)}\n");
        b.Append('\n');
        b.Append("Machine-readable (`--json` &c.) invocations are excluded from this measurement — they are structurally uncompressable, not a filter defect.\n\n");
        b.Append("## Acceptance criteria\n\n");
        b.Append($"- A `{g.Family}` filter: pure function, raw output in → compact out, fixture-tested with a measured-savings assertion.\n");
        b.Append("- Exit-code parity preserved; filter failure degrades to raw passthrough; no output content in gap/stats metadata.\n");
        return b.ToString();
    }

    /// <summary>
    /// Extracts the gap family from each open-issue title of the form
    /// `Filter: &lt;family&gt; ...`, returning a set for dedupe. Pure.
    /// </summary>
    internal static HashSet<string> ParseExistingFilterFamilies(IEnumerable<string> titles)
    {
        var result = new HashSet<string>();
        foreach (var t in titles)
        {
            if (!t.StartsWith(FilterIssueTitlePrefix, StringComparison.Ordinal)) continue;
            var rest = t[FilterIssueTitlePrefix.Length..].Trim();
            // Family is the first whitespace- or paren-delimited token.
            var fam = rest;
            var i = rest.IndexOfAny(new[] { ' ', '(' });
            if (i >= 0) fam = rest[..i];
            if (fam != "") result.Add(fam);
        }
        return result;
    }

    /// <summary>
    /// Lists open issues and returns the set of families that already have a
    /// Filter issue. The `gh` shell-out is kept thin; the parsing is
    /// delegated to the pure helper.
    /// </summary>
    private static HashSet<string> GhExistingFilterFamilies()
    {
        var stdout = RunGh("issue", "list", "--state", "open", "--limit", "500", "--json", "title");
        List<GhIssueTitle>? issues;
        try
        {
            issues = JsonSerializer.Deserialize<List<GhIssueTitle>>(stdout);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"parsing gh output: {ex.Message}");
        }
        return ParseExistingFilterFamilies((issues ?? new()).Select(i => i.Title));
    }

    private sealed record GhIssueTitle
    {
        [System.Text.Json.Serialization.JsonPropertyName("title")]
        public string Title { get; init; } = "";
    }

    /// <summary>Files one stage:intake filter issue and returns the issue URL gh prints on stdout.</summary>
    private static string GhCreateFilterIssue(string title, string body) =>
        RunGh("issue", "create",
            "--title", title,
            "--body", body,
            "--label", "stage:intake",
            "--label", "enhancement").Trim();

    /// <summary>
    /// Runs `gh` with the given args, returning stdout. A nonzero exit throws
    /// with gh's stderr when available, so failures (not logged in, no repo,
    /// missing label) are legible.
    /// </summary>
    private static string RunGh(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "gh",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("gh failed to start");
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
        {
            var msg = stderr.Trim();
            throw new InvalidOperationException(msg.Length > 0 ? msg : $"gh exited {proc.ExitCode}");
        }
        return stdout;
    }
}
