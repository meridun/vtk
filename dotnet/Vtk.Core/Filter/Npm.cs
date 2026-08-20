// The shared dispatch layer for `npm run <script>`. npm prepends a two-line
// banner to every script run — a "> <pkg>@<ver> <script>" header and a
// "> <expanded command line>" line — pure per-call noise. This strips that
// banner and parses the inner tool from the expanded command line so the
// runner can delegate the remaining output to that tool's own filter
// (eslint, mocha, dbmate, ...).
//
// Everything here is pure: raw output in, banner-stripped body + inner argv
// out. No process spawning, no filesystem access. The CLI runner owns the
// delegation and the gap attribution to the inner tool's family.
// Port of internal/filter/npm/npm.go.
namespace Vtk.Core.Filter;

public static class Npm
{
    // Marks both npm banner lines. npm writes them as "> ..." with a single
    // leading "> ".
    private const string BannerPrefix = "> ";

    public sealed record StripResult(string Body, string[] Inner, bool Ok);

    /// <summary>
    /// Detects the npm run banner in raw and returns the output body with the
    /// banner removed, plus the inner command parsed from the expanded
    /// command line. Ok is false when raw does not carry a recognizable npm
    /// banner, in which case Body and Inner are unspecified and the caller
    /// must treat the invocation as an ordinary (unhandled) command — never
    /// as a banner strip.
    ///
    /// Recognized shape (leading blank lines tolerated — npm/stderr
    /// interleaving can emit one):
    ///
    ///   &gt; &lt;pkg&gt;@&lt;ver&gt; &lt;script&gt;
    ///   &gt; &lt;expanded command line&gt;
    ///   &lt;optional blank line&gt;
    ///   &lt;inner tool output...&gt;
    ///
    /// The inner command is the fields of the second banner line, so
    /// "&gt; eslint . --cache" yields inner = ["eslint", ".", "--cache"].
    /// </summary>
    public static StripResult StripBanner(string raw)
    {
        var lines = raw.Split('\n');

        // Skip any leading blank lines before the banner.
        var i = 0;
        while (i < lines.Length && lines[i].Trim() == "") i++;

        // Need two consecutive banner lines: the script header and the
        // expanded command line. Anything else is not an npm run banner.
        if (i + 1 >= lines.Length ||
            !lines[i].StartsWith(BannerPrefix) ||
            !lines[i + 1].StartsWith(BannerPrefix))
        {
            return new StripResult("", Array.Empty<string>(), false);
        }

        var expanded = lines[i + 1][BannerPrefix.Length..].Trim();
        var inner = expanded.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (inner.Length == 0)
        {
            // A banner with an empty command line is malformed; do not claim it.
            return new StripResult("", Array.Empty<string>(), false);
        }

        // Body is everything after the two banner lines. Drop a single blank
        // separator line that npm emits between the banner and the tool
        // output; preserve any further blank lines (they may be meaningful
        // to the inner filter).
        var rest = lines[(i + 2)..];
        if (rest.Length > 0 && rest[0].Trim() == "")
            rest = rest[1..];

        return new StripResult(string.Join("\n", rest), inner, true);
    }

    // ---- size-floored fold (#93, registry option C) -------------------------
    //
    // Modern npm (>= 11) emits the run banner only on a TTY, so under vtk's
    // pipe capture the inner tool is often undiscoverable. When the inner
    // tool is unrecognized (or there is no banner at all), successful output
    // folds behind `OK <id>` only above a generous absolute byte floor;
    // below it — and on any failure — output stays inline, so terse
    // load-bearing scripts (`npm run sdlc`) pass through automatically with
    // no exempt list. The mechanism (floor, tail caps, tail slice) lives in
    // <see cref="Fold"/>, shared with `powershell -File` (#131); the aliases
    // below keep npm's original public surface. The CLI runner owns the
    // exit-code gate, the spool, and the telemetry; the helpers here are pure.

    /// <summary>
    /// Absolute byte floor a successful unrecognized-inner `npm run` body
    /// must reach before it folds behind `OK &lt;id&gt;` (#93, option C).
    /// Alias of <see cref="Fold.FloorBytes"/>.
    /// </summary>
    public const int FoldFloorBytes = Fold.FloorBytes;

    /// <summary>
    /// Registry-style identity for the fold pseudo-filter. Telemetry records
    /// it as the invocation reason when the fold path engages (both the
    /// above-floor fold and the below-floor intentional near-passthrough),
    /// so `npm run` stops inflating the gap table without ever being
    /// silently unlogged. A static identifier, never output content.
    /// </summary>
    public const string FoldName = "npm-run-fold";

    /// <summary>Maximum number of trailing lines <see cref="FoldTail"/> keeps inline. Alias of <see cref="Fold.TailMaxLines"/>.</summary>
    public const int FoldTailMaxLines = Fold.TailMaxLines;

    /// <summary>Maximum total size (chars) of the tail <see cref="FoldTail"/> keeps inline. Alias of <see cref="Fold.TailMaxBytes"/>.</summary>
    public const int FoldTailMaxBytes = Fold.TailMaxBytes;

    /// <summary>The shared fold tail slice — see <see cref="Fold.Tail"/>.</summary>
    public static string FoldTail(string body) => Fold.Tail(body);
}
