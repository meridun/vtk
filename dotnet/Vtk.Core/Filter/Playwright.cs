// The playwright filter: a pure function that compacts the default "list"
// reporter output of `playwright test` / `npx playwright test` (#169). Raw
// output in, compact output out; returning the input unchanged means
// "nothing to elide". Modeled on Mocha.Filter and NodeTest.Filter.
//
// Under pipe capture the list reporter prints "Running N tests using M
// workers", then one line per test attempt — "ok N [project] › file:line:col
// › title (ms)" for a pass, "x N …" for a failure, "-  N …" for a skip
// (win32 markers; POSIX prints ✓ / ✘ / -) — then a numbered detail block
// per failure ("1) [project] › … ────", error, snippet, stack) and a
// trailing summary block: "N failed" / "N flaky" (each followed by an
// indented listing of the affected tests), "N skipped", "N passed (1.6s)".
// The filter folds the pass and skip lines, keeps everything else above the
// summary (the header, failing lines including "(retry #1)" attempts, the
// detail blocks, stdout/stderr forwarded from tests), and keeps the summary
// block verbatim.
//
// playwright colors the failure detail blocks (expect's diff) even on a
// pipe, and colors everything under FORCE_COLOR; lines are matched and
// emitted ANSI-stripped so the compact view is the same either way. The
// spool keeps the raw bytes.
//
// playwright exits 1 when any test fails; that output is exactly what to
// compact, so the registry allowlists {0, 1}. Fatal errors ("No tests
// found", config crashes) and the json/line/dot reporters carry no list
// lines to fold — content, not exit code or flags, is the raw gate.
using System.Text.RegularExpressions;

namespace Vtk.Core.Filter;

public static partial class Playwright
{
    // CSI escape sequences (colors, cursor moves). Same form as the TOML
    // engine's strip_ansi; #163 lands a shared helper this can defer to.
    [GeneratedRegex(@"\x1b\[[0-9;?]*[ -/]*[@-~]")]
    private static partial Regex AnsiRe();

    // Summary lines: "  2 failed", "  1 flaky", "  1 skipped",
    // "  7 passed (1.6s)", "  3 did not run", "  1 interrupted".
    [GeneratedRegex(@"^\s*\d+\s+(passed|failed|flaky|skipped|did not run|interrupted)\b")]
    private static partial Regex SummaryRe();

    // The indented listing lines under "N failed" / "N flaky":
    // "    [chromium] › tests\a.spec.js:3:3 › suite › title ────". The
    // project tag is absent when the config declares no projects.
    [GeneratedRegex(@"^\s+\S.*\s›\s")]
    private static partial Regex ListingRe();

    // Per-test lines that fold: passes ("ok N …" / "✓ N …") and skips
    // ("-  N …"). Failing lines ("x N …" / "✘ N …") never match and stay.
    [GeneratedRegex(@"^\s*(ok|✓|-)\s+\d+\s")]
    private static partial Regex FoldRe();

    /// <summary>
    /// Compacts playwright list-reporter output: folds the per-test pass and
    /// skip lines, keeps every other line above the summary (header, failing
    /// lines, failure detail blocks, forwarded test output), and keeps the
    /// trailing summary block verbatim. Lines are ANSI-stripped. Input that
    /// carries no summary block, or in which nothing folds, passes through
    /// unchanged.
    /// </summary>
    public static string Filter(string raw)
    {
        var lines = raw.Split('\n');
        var plain = new string[lines.Length];
        for (var i = 0; i < lines.Length; i++)
            plain[i] = AnsiRe().Replace(lines[i], "");

        // Locate the trailing summary block: walk back over trailing blanks,
        // then over summary and listing lines. The block is contiguous
        // (playwright prints no blank line inside it) and must hold at
        // least one summary line; otherwise this is not list-reporter
        // output we recognize.
        var end = plain.Length;
        while (end > 0 && plain[end - 1].Trim() == "") end--;
        var summaryStart = end;
        var sawSummary = false;
        while (summaryStart > 0)
        {
            var l = plain[summaryStart - 1];
            if (SummaryRe().IsMatch(l)) sawSummary = true;
            else if (!ListingRe().IsMatch(l)) break;
            summaryStart--;
        }
        if (!sawSummary) return raw;

        // Above the summary: drop pass/skip lines, keep the rest, collapse
        // blank runs to one and trim leading blanks.
        var outLines = new List<string>();
        var folded = 0;
        var pendingBlank = false;
        for (var i = 0; i < summaryStart; i++)
        {
            var l = plain[i];
            if (FoldRe().IsMatch(l))
            {
                folded++;
                continue;
            }
            if (l.Trim() == "")
            {
                pendingBlank = outLines.Count > 0;
                continue;
            }
            if (pendingBlank) outLines.Add("");
            pendingBlank = false;
            outLines.Add(l);
        }
        if (folded == 0) return raw;

        if (outLines.Count > 0) outLines.Add("");
        for (var i = summaryStart; i < end; i++)
            outLines.Add(plain[i]);
        return string.Join("\n", outLines);
    }
}
