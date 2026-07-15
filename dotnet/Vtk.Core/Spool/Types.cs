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
}

/// <summary>Aggregates passthrough invocations for one command family.</summary>
public sealed class GapSummary
{
    public string Family { get; init; } = "";
    public int Calls { get; set; }
    public long RawBytes { get; set; }
}

/// <summary>Cumulative savings for one command family.</summary>
public sealed class FamilyGain
{
    public string Family { get; init; } = "";
    public int Calls { get; set; }
    public long RawBytes { get; set; }
    public long OutBytes { get; set; }
    public long Saved => RawBytes - OutBytes;
}

/// <summary>Cumulative savings across all countable invocations: overall roll-up plus per-family breakdown.</summary>
public sealed class GainReport
{
    public int Calls { get; set; }
    public long RawBytes { get; set; }
    public long OutBytes { get; set; }
    public List<FamilyGain> Families { get; set; } = new();
    public long Saved => RawBytes - OutBytes;
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
