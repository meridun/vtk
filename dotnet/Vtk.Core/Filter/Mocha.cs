// The mocha filter: a pure function that compacts the default "spec"
// reporter output of `mocha` / `npx mocha`. Raw output in, compact output
// out; returning the input unchanged means "nothing to elide".
//
// A passing run prints one indented "✔ <title>" line per spec under a nested
// suite tree — pure per-spec noise once the run is green. A failing run adds
// a "failing" summary line and a detail block per failure (name, assertion,
// trimmed stack) — that detail is the whole point to keep. The filter folds
// the spec tree, keeps the summary lines, and keeps the failure-detail
// blocks verbatim.
//
// mocha reports test failures via exit 1 — that output is exactly what we
// want to compact. The registry declares mocha's exit-code allowlist as
// {0, 1} (exit 2+ is a mocha/config error and stays raw).
// Port of internal/filter/mocha/mocha.go.
using System.Text.RegularExpressions;

namespace Vtk.Core.Filter;

public static partial class Mocha
{
    // A summary line: "  N passing (12ms)", "  M failing", "  K pending".
    // mocha indents these two spaces; count first, keyword second.
    [GeneratedRegex(@"^\s*\d+\s+(passing|failing|pending)\b")]
    private static partial Regex SummaryRe();

    // The first line of a failure-detail block: "  N) <suite title>". Same
    // shape appears in the tree ("    N) <title>"), but the detail region
    // only begins after the summary, so we switch on the summary boundary
    // rather than on this pattern alone.
    [GeneratedRegex(@"^\s*\d+\)\s")]
    private static partial Regex FailDetailRe();

    /// <summary>
    /// Compacts mocha spec-reporter output to just the summary lines plus the
    /// per-failure detail blocks. The passing/pending/suite spec tree above
    /// the summary is folded away; everything from the summary onward is
    /// preserved verbatim. Input that carries no mocha summary passes
    /// through unchanged.
    /// </summary>
    public static string Filter(string raw)
    {
        var lines = raw.Split('\n');

        // Locate the summary region: the contiguous run of summary lines.
        // mocha prints them together (passing, then failing, then pending as
        // applicable) after the spec tree and before the failure-detail
        // blocks.
        int firstSummary = -1, lastSummary = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (SummaryRe().IsMatch(lines[i]))
            {
                if (firstSummary == -1) firstSummary = i;
                lastSummary = i;
            }
        }
        if (firstSummary == -1)
        {
            // No mocha summary: not spec-reporter output we recognize. Pass
            // through unchanged rather than risk mangling an unrelated
            // tool's output.
            return raw;
        }

        var outLines = new List<string>();
        // Keep the summary lines, trimmed of mocha's leading indent so the
        // rollup reads as a compact header.
        for (var i = firstSummary; i <= lastSummary; i++)
        {
            if (SummaryRe().IsMatch(lines[i]))
                outLines.Add(lines[i].Trim());
        }

        // Keep the failure-detail region verbatim: from the first "N)" block
        // after the summary to the last non-blank line. Preserve interior
        // blank lines (they separate blocks and are part of the assertion
        // diff), but trim the trailing blank padding mocha emits.
        var detailStart = -1;
        for (var i = lastSummary + 1; i < lines.Length; i++)
        {
            if (FailDetailRe().IsMatch(lines[i]))
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
            if (detailEnd >= detailStart)
            {
                outLines.Add(""); // blank line between summary and first block
                for (var i = detailStart; i <= detailEnd; i++)
                    outLines.Add(lines[i]);
            }
        }

        return string.Join("\n", outLines);
    }
}
