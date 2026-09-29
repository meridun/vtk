// The node --test filter: a pure function that compacts the default "spec"
// reporter output of Node's built-in test runner (`node --test`). Raw
// output in, compact output out; returning the input unchanged means
// "nothing to elide". Modeled on Mocha.Filter (#168).
//
// A run prints a nested suite tree — "▶ <suite>" headers, one "✔ <title>
// (ms)" line per passing test (and per closed suite), "﹣ <title> # <why>"
// for skipped tests, "✖ <title> (ms)" for failures — then an "ℹ <key> <n>"
// summary block (tests, suites, pass, fail, cancelled, skipped, todo,
// duration_ms) and, on failure, a "✖ failing tests:" region with one detail
// block per failure (location, assertion, diff, stack). The filter folds
// the passing/suite/skipped tree lines, keeps the summary block verbatim,
// and keeps the failing-tests region verbatim.
//
// Only recognized tree lines fold. Everything else above the summary — tree
// "✖" lines (which suite failed), "ℹ" diagnostics, stdout/stderr forwarded
// from test files — stays inline: a module-load crash prints its stack
// *before* the summary, and folding it would hide the cause. When in doubt,
// pass through.
//
// node exits 1 when any test fails; that output is exactly what to compact,
// so the registry allowlists {0, 1}. Fatal errors (bad option, exit 9) and
// the TAP reporter carry no "ℹ tests N" block and pass through unchanged —
// content, not exit code, is the raw gate.
using System.Text.RegularExpressions;

namespace Vtk.Core.Filter;

public static partial class NodeTest
{
    // ANSI SGR/CSI sequences: under FORCE_COLOR node wraps "✔"/"ℹ" lines in
    // "\x1b[32m…\x1b[39m". Stripped for matching only; kept lines are
    // emitted verbatim. Same shape as Gh.cs's private regex; #163 lands the
    // shared helper this can defer to.
    [GeneratedRegex(@"\x1b\[[0-9;]*[A-Za-z]")]
    private static partial Regex AnsiRe();

    // The anchor of the summary block: "ℹ tests N".
    [GeneratedRegex(@"^\s*ℹ\s+tests\s+\d+\s*$")]
    private static partial Regex SummaryStartRe();

    // Any summary-block line: "ℹ <key> <value>" (suites, pass, fail,
    // cancelled, skipped, todo, duration_ms).
    [GeneratedRegex(@"^\s*ℹ\s+\S+\s+\S+\s*$")]
    private static partial Regex SummaryLineRe();

    // Tree lines that fold: suite headers ("▶ <suite>"), passing tests and
    // closed suites ("✔ <title> (ms)", optionally "# TODO"), skipped tests
    // ("﹣ <title> (ms) # <reason>").
    [GeneratedRegex(@"^\s*[▶✔﹣]\s")]
    private static partial Regex FoldRe();

    // The failing-tests region header printed after the summary.
    [GeneratedRegex(@"^\s*✖\s+failing tests:\s*$")]
    private static partial Regex FailHeaderRe();

    /// <summary>
    /// Compacts node --test spec-reporter output: folds passing/suite/skipped
    /// tree lines, keeps every other line above the summary, keeps the
    /// "ℹ" summary block verbatim, and keeps the "✖ failing tests:" region
    /// verbatim. Input that carries no summary block passes through
    /// unchanged.
    /// </summary>
    public static string Filter(string raw)
    {
        var lines = raw.Split('\n');
        var plain = new string[lines.Length];
        for (var i = 0; i < lines.Length; i++)
            plain[i] = AnsiRe().Replace(lines[i], "");

        // Locate the summary block: the last "ℹ tests N" line and the
        // contiguous "ℹ" lines that follow it. A diagnostic "ℹ" line inside
        // the tree is not an anchor and stays inline like any other
        // unrecognized line.
        var firstSummary = -1;
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            if (SummaryStartRe().IsMatch(plain[i]))
            {
                firstSummary = i;
                break;
            }
        }
        if (firstSummary == -1)
        {
            // No node summary: not spec-reporter output we recognize (fatal
            // error, TAP reporter, ...). Pass through unchanged rather than
            // risk mangling it.
            return raw;
        }
        var lastSummary = firstSummary;
        while (lastSummary + 1 < lines.Length && SummaryLineRe().IsMatch(plain[lastSummary + 1]))
            lastSummary++;

        var outLines = new List<string>();

        // Above the summary: fold recognized tree lines, keep the rest.
        for (var i = 0; i < firstSummary; i++)
        {
            if (FoldRe().IsMatch(plain[i])) continue;
            outLines.Add(lines[i]);
        }
        // Drop blank padding left between kept lines and the summary.
        while (outLines.Count > 0 && outLines[^1].Trim() == "")
            outLines.RemoveAt(outLines.Count - 1);

        // The summary block verbatim.
        for (var i = firstSummary; i <= lastSummary; i++)
            outLines.Add(lines[i]);

        // The failing-tests region verbatim: from its header after the
        // summary to the last non-blank line. Interior blank lines separate
        // blocks and belong to assertion diffs; only the trailing padding
        // is trimmed.
        var detailStart = -1;
        for (var i = lastSummary + 1; i < lines.Length; i++)
        {
            if (FailHeaderRe().IsMatch(plain[i]))
            {
                detailStart = i;
                break;
            }
        }
        if (detailStart != -1)
        {
            var detailEnd = lines.Length - 1;
            while (detailEnd >= detailStart && lines[detailEnd].Trim() == "")
                detailEnd--;
            outLines.Add(""); // blank line between summary and the region
            for (var i = detailStart; i <= detailEnd; i++)
                outLines.Add(lines[i]);
        }

        return string.Join("\n", outLines);
    }
}
