// The generic size-floored success fold (#93, registry option C), shared by
// the wrapper families that route through it: `npm run` (#93/#120) and
// `powershell -File <script>` (#131). Success output at or above the byte
// floor folds behind `OK <id>` with a short summary tail inline; below it —
// and on any failure — output stays inline, so terse load-bearing runs pass
// through automatically with no exempt list. The CLI runner owns the
// exit-code gate, the spool, and the telemetry; everything here is pure.
using System.Text.RegularExpressions;

namespace Vtk.Core.Filter;

public static partial class Fold
{
    /// <summary>
    /// Absolute byte floor a successful body must reach before it folds
    /// behind `OK &lt;id&gt;` (#93, option C; shared with `powershell -File`,
    /// #131). Far above the #52 savings bar, so a fold always clears that
    /// bar too.
    /// </summary>
    public const int FloorBytes = 64 * 1024;

    /// <summary>Maximum number of trailing lines <see cref="Tail"/> keeps inline.</summary>
    public const int TailMaxLines = 5;

    /// <summary>Maximum total size (chars) of the tail <see cref="Tail"/> keeps inline.</summary>
    public const int TailMaxBytes = 512;

    /// <summary>
    /// Maximum number of summary lines <see cref="Tail"/> pins above the
    /// positional tail (#134, option B). Their bytes count inside
    /// <see cref="TailMaxBytes"/>.
    /// </summary>
    public const int PinnedMaxLines = 3;

    // Runner summary lines that carry the verdict regardless of where they
    // sit (#134): mocha "  N passing (12ms)" / "N failing" / "N pending"
    // (mirrors Mocha.SummaryRe), jest "Tests:  1 failed, 5 passed, 6 total",
    // eslint "✖ 3 problems (3 errors, 0 warnings)". Content-sniffed
    // dispatch is deliberately not what this is — the fold stays a fold.
    [GeneratedRegex(@"^\s*\d+\s+(passing|failing|pending)\b|^\s*Tests:\s.*\b\d+\s+(passed|failed)\b|^\W*\d+\s+problems?\b")]
    private static partial Regex SummaryRe();

    /// <summary>
    /// The generic "summary stays inline" slice of a folded body: its last
    /// lines — where CLI tools put their summaries — capped at
    /// <see cref="TailMaxLines"/> lines and <see cref="TailMaxBytes"/>
    /// chars, preceded by up to <see cref="PinnedMaxLines"/> runner summary
    /// lines pinned from above that positional tail (#134) — so a mocha
    /// "N passing" line followed by trailing console noise survives the
    /// fold. Pinned lines take budget priority; the positional tail fills
    /// what remains, and the whole result never exceeds
    /// <see cref="TailMaxBytes"/>. Trailing blank lines are dropped first;
    /// lines are taken from the end while both caps hold, so an oversized
    /// final line yields "" (a bare `OK &lt;id&gt;` fold). Lines are emitted
    /// verbatim and in original order; nothing is moved or duplicated.
    /// Pure — the full body is always recoverable from the spool.
    /// </summary>
    public static string Tail(string body)
    {
        var lines = body.Split('\n');
        var end = lines.Length;
        while (end > 0 && lines[end - 1].Trim() == "") end--;

        // Positional boundary with the full budget fixes which lines are
        // "above the tail" and therefore pin candidates.
        var start0 = PositionalStart(lines, end, TailMaxBytes);

        // Pin the summary lines nearest the tail, newest first, while they
        // fit; +1 each for the newline joining them to what follows.
        var pinned = new List<int>();
        var pinnedBytes = 0;
        for (var i = start0 - 1; i >= 0 && pinned.Count < PinnedMaxLines; i--)
        {
            if (!SummaryRe().IsMatch(lines[i])) continue;
            var cost = lines[i].Length + 1;
            if (pinnedBytes + cost > TailMaxBytes) break;
            pinnedBytes += cost;
            pinned.Add(i);
        }
        pinned.Reverse();

        // The remaining budget is never larger than the full one, so the
        // recomputed boundary is at or after start0: no pinned line can
        // also fall inside the positional tail.
        var start = pinned.Count == 0 ? start0 : PositionalStart(lines, end, TailMaxBytes - pinnedBytes);

        var kept = new List<string>(pinned.Count + (end - start));
        foreach (var i in pinned) kept.Add(lines[i]);
        for (var i = start; i < end; i++) kept.Add(lines[i]);
        return kept.Count == 0 ? "" : string.Join("\n", kept);
    }

    // The index of the first line of the positional tail: lines are taken
    // from `end` backwards while both the line cap and `budget` hold.
    private static int PositionalStart(string[] lines, int end, int budget)
    {
        var start = end;
        var total = 0;
        while (start > 0 && end - start < TailMaxLines)
        {
            // +1 for the joining newline on every line after the first.
            var cost = lines[start - 1].Length + (start == end ? 0 : 1);
            if (total + cost > budget) break;
            total += cost;
            start--;
        }
        return start;
    }
}
