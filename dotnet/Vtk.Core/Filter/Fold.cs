// The generic size-floored success fold (#93, registry option C), shared by
// the wrapper families that route through it: `npm run` (#93/#120) and
// `powershell -File <script>` (#131). Success output at or above the byte
// floor folds behind `OK <id>` with a short summary tail inline; below it —
// and on any failure — output stays inline, so terse load-bearing runs pass
// through automatically with no exempt list. The CLI runner owns the
// exit-code gate, the spool, and the telemetry; everything here is pure.
namespace Vtk.Core.Filter;

public static class Fold
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
    /// The generic "summary stays inline" slice of a folded body: its last
    /// lines — where CLI tools put their summaries — capped at
    /// <see cref="TailMaxLines"/> lines and <see cref="TailMaxBytes"/>
    /// chars. Trailing blank lines are dropped first; lines are taken from
    /// the end while both caps hold, so an oversized final line yields ""
    /// (a bare `OK &lt;id&gt;` fold). Pure — the full body is always
    /// recoverable from the spool.
    /// </summary>
    public static string Tail(string body)
    {
        var lines = body.Split('\n');
        var end = lines.Length;
        while (end > 0 && lines[end - 1].Trim() == "") end--;

        var start = end;
        var total = 0;
        while (start > 0 && end - start < TailMaxLines)
        {
            // +1 for the joining newline on every line after the first.
            var cost = lines[start - 1].Length + (start == end ? 0 : 1);
            if (total + cost > TailMaxBytes) break;
            total += cost;
            start--;
        }
        return start == end ? "" : string.Join("\n", lines[start..end]);
    }
}
