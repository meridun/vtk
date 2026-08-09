// Cross-reference between session transcript commands and the vtk
// invocation log (#115). Dogfooding wraps git/gh/npm through shell
// functions that are invisible in transcripts — the command text stays bare
// `gh issue list ...` even though it routed through vtk. Matching a
// transcript segment to a logged invocation by (normalized argv, timestamp
// window) lets discover subtract those calls instead of reporting them as
// `unwrapped` false positives. Read-only over metadata (invariant 3): only
// the entry's time and already-redacted cmd are consulted — never output
// content.
using System.Text;
using Vtk.Core.Spool;

namespace Vtk.Core.Discover;

/// <summary>
/// An index of invocation-log entries consumable one-to-one by transcript
/// command events: <see cref="TryConsume"/> matches a segment's argv and
/// result timestamp against the nearest unconsumed log entry with the same
/// normalized command text within <see cref="Tolerance"/>. One log entry
/// satisfies at most one event, so counts subtract rather than a single
/// wrapped call suppressing a whole family.
/// </summary>
public sealed class WrappedInvocations
{
    /// <summary>
    /// Default match window. The invocation is logged at command completion
    /// and the transcript stamps the tool_result at arrival — same machine,
    /// both UTC, seconds apart in practice; command-text equality gates
    /// first, so the window only disambiguates repeats of the same command
    /// across time.
    /// </summary>
    public static readonly TimeSpan DefaultTolerance = TimeSpan.FromMinutes(5);

    private readonly Dictionary<string, List<DateTime>> _byCmd = new();

    public TimeSpan Tolerance { get; }

    /// <summary>Count of log entries consumed by successful matches so far.</summary>
    public int Matched { get; private set; }

    public WrappedInvocations(IEnumerable<Invocation> invocations, TimeSpan? tolerance = null)
    {
        Tolerance = tolerance ?? DefaultTolerance;
        foreach (var inv in invocations)
        {
            var key = Normalize(inv.Cmd);
            if (key.Length == 0) continue;
            if (!_byCmd.TryGetValue(key, out var times))
            {
                times = new List<DateTime>();
                _byCmd[key] = times;
            }
            times.Add(inv.Time.ToUniversalTime());
        }
    }

    /// <summary>
    /// Attempts to match a transcript segment against the log: true consumes
    /// the nearest unconsumed same-command entry within the tolerance. An
    /// event without a timestamp never matches (conservative: it stays an
    /// opportunity, the pre-#115 behavior).
    /// </summary>
    public bool TryConsume(string[] argv, DateTime? at)
    {
        if (at is null) return false;
        var key = Normalize(string.Join(" ", argv));
        if (key.Length == 0) return false;
        if (!_byCmd.TryGetValue(key, out var times) || times.Count == 0) return false;

        var t = at.Value.ToUniversalTime();
        var bestIdx = -1;
        var bestDelta = TimeSpan.MaxValue;
        for (var i = 0; i < times.Count; i++)
        {
            var delta = times[i] > t ? times[i] - t : t - times[i];
            if (delta < bestDelta)
            {
                bestDelta = delta;
                bestIdx = i;
            }
        }
        if (bestIdx < 0 || bestDelta > Tolerance) return false;
        times.RemoveAt(bestIdx);
        Matched++;
        return true;
    }

    /// <summary>
    /// Canonicalizes a command line for cross-source comparison: shell quote
    /// characters are stripped (the transcript keeps `-m "msg"` quoting that
    /// an argv join never has), credentials are redacted with the same pass
    /// the log applied at write time, and whitespace is collapsed.
    /// Best-effort by design — analysis input, not the wrap path.
    /// </summary>
    internal static string Normalize(string cmd)
    {
        var sb = new StringBuilder(cmd.Length);
        foreach (var c in cmd)
        {
            if (c != '"' && c != '\'') sb.Append(c);
        }
        var tokens = Store.Redact(sb.ToString())
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", tokens);
    }
}
