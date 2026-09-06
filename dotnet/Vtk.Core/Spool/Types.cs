using System.Text.Json.Serialization;

namespace Vtk.Core.Spool;

/// <summary>
/// One metadata entry. Carries the (redacted) command line and byte counts —
/// never output content (invariant 3). Field names match the Go store's JSON
/// tags (snake_case) so invocations.jsonl stays readable by both binaries
/// during the incremental migration — same store directory, same file.
/// </summary>
public sealed record Invocation
{
    [JsonPropertyName("time")]
    public DateTime Time { get; init; }
    [JsonPropertyName("cmd")]
    public string Cmd { get; init; } = "";
    [JsonPropertyName("raw_bytes")]
    public long RawBytes { get; init; }
    [JsonPropertyName("out_bytes")]
    public long OutBytes { get; init; }
    [JsonPropertyName("filtered")]
    public bool Filtered { get; init; }
    [JsonPropertyName("tty")]
    public bool TTY { get; init; }
    [JsonPropertyName("reason")]
    public string Reason { get; init; } = "";
    /// <summary>
    /// The 4-hex spool id this row emitted as `OK &lt;id&gt;` (a fold), or the
    /// id a `vtk show` row read back (#137). Null — and omitted from the
    /// JSON — on every other row, so pre-#137 row shapes are unchanged. A
    /// hash of the command line, never output content.
    /// </summary>
    [JsonPropertyName("spool_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SpoolId { get; init; }
    /// <summary>
    /// `vtk show` rows only (#137): whether `--grep` narrowed the output.
    /// A boolean by decision — the pattern text is agent-authored content
    /// and is never written (invariant 3).
    /// </summary>
    [JsonPropertyName("grep")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Grep { get; init; }
}

/// <summary>Aggregates passthrough invocations for one command family.</summary>
public sealed class GapSummary
{
    public string Family { get; init; } = "";
    public int Calls { get; set; }
    public long RawBytes { get; set; }
}

/// <summary>
/// Recovery rate for one fold identity (#137): how many spooled folds the
/// agent pulled back with `vtk show` inside <see cref="Store.RecoveryWindow"/>,
/// and how many bytes those shows emitted. A high rate marks a filter that
/// suppresses what the agent wanted.
/// </summary>
public sealed class RecoverySummary
{
    public string Filter { get; init; } = "";
    public int Folds { get; set; }
    public int Recovered { get; set; }
    public long ShowBytes { get; set; }
}

/// <summary>
/// Cumulative savings for one command family. <see cref="Saved"/> is the
/// gross figure; <see cref="Recovered"/> is the bytes `vtk show` emitted to
/// undo this family's folds (#137), so <see cref="Net"/> is the honest one.
/// </summary>
public sealed class FamilyGain
{
    public string Family { get; init; } = "";
    public int Calls { get; set; }
    public long RawBytes { get; set; }
    public long OutBytes { get; set; }
    public long Recovered { get; set; }
    public long Saved => RawBytes - OutBytes;
    public long Net => Saved - Recovered;
}

/// <summary>Cumulative savings across all countable invocations: overall roll-up plus per-family breakdown.</summary>
public sealed class GainReport
{
    public int Calls { get; set; }
    public long RawBytes { get; set; }
    public long OutBytes { get; set; }
    /// <summary>Bytes emitted by `vtk show` rows joined to a fold (#137); never counted in <see cref="RawBytes"/>/<see cref="OutBytes"/>.</summary>
    public long Recovered { get; set; }
    /// <summary>Number of folds recovered via `vtk show` (#137).</summary>
    public int Recoveries { get; set; }
    public List<FamilyGain> Families { get; set; } = new();
    public long Saved => RawBytes - OutBytes;
    public long Net => Saved - Recovered;
}

/// <summary>Cumulative savings for one UTC day. Backs `vtk gain --daily` / `--graph` (#58).</summary>
public sealed class DailyGain
{
    public DateOnly Date { get; init; }
    public int Calls { get; set; }
    public long RawBytes { get; set; }
    public long OutBytes { get; set; }
    public long Saved => RawBytes - OutBytes;
}

/// <summary>
/// Savings attributed to one agent session's time window. Backs
/// `vtk gain --session` (#46). Carries the session id and window bounds
/// only — never transcript content (invariant 3).
/// </summary>
public sealed class SessionGain
{
    public string Id { get; init; } = "";
    public DateTime Start { get; init; }
    public DateTime End { get; init; }
    public int Calls { get; set; }
    public long RawBytes { get; set; }
    public long OutBytes { get; set; }
    public long Saved => RawBytes - OutBytes;
}
