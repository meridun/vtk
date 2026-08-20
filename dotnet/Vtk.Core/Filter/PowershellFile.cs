// The dispatch predicate for `powershell -File <script>` / `pwsh -File
// <script>` (#131). Measured traffic for the powershell family is 100%
// `-File <script>` test-runner invocations (zero cmdlet/`-Command` calls),
// so there is no output shape to reshape — instead successful script output
// routes through the shared size-floored fold (Fold, #93 option C
// mechanism): success at or above the floor folds behind `OK <id>` with the
// raw spooled; failures and small outputs stay inline.
//
// Everything here is pure argv inspection: no process spawning, no
// filesystem access. The CLI runner owns capture, the exit-code gate, the
// spool, and the telemetry.
namespace Vtk.Core.Filter;

public static class PowershellFile
{
    /// <summary>
    /// Registry-style identity for the powershell fold pseudo-filter.
    /// Telemetry records it as the invocation reason when the fold path
    /// engages (both the above-floor fold and the below-floor intentional
    /// near-passthrough), so `powershell -File` stops inflating the gap
    /// table without ever being silently unlogged. A static identifier,
    /// never output content (invariant 3).
    /// </summary>
    public const string FoldName = "powershell-file-fold";

    /// <summary>
    /// Reports whether argv is a `powershell`/`pwsh` script-file invocation
    /// the fold path handles: the host name (case-insensitive, optional
    /// `.exe`) followed by an explicit `-File` parameter (case-insensitive)
    /// with a script token after it. `-Command`/`-EncodedCommand` before the
    /// `-File` vetoes the match — everything after those is command text,
    /// not host parameters. Other tokens (host flags and their values,
    /// e.g. `-ExecutionPolicy Bypass`) are skipped, so the measured shape
    /// `powershell -ExecutionPolicy Bypass -File &lt;script&gt;` matches. A bare
    /// trailing `-File`, positional-script spellings, and anything that is
    /// not powershell/pwsh fall through to normal passthrough.
    /// </summary>
    public static bool Matches(IReadOnlyList<string> argv)
    {
        if (argv.Count < 3) return false; // host + -File + script, minimum

        var host = argv[0];
        if (host.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            host = host[..^4];
        if (!host.Equals("powershell", StringComparison.OrdinalIgnoreCase) &&
            !host.Equals("pwsh", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        for (var i = 1; i < argv.Count; i++)
        {
            if (argv[i].Equals("-Command", StringComparison.OrdinalIgnoreCase) ||
                argv[i].Equals("-EncodedCommand", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (argv[i].Equals("-File", StringComparison.OrdinalIgnoreCase))
            {
                return i + 1 < argv.Count;
            }
        }
        return false;
    }
}
