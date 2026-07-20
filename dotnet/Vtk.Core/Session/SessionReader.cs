// Parses Claude Code session JSONL into ordered Bash command events.
// Analysis input, not the wrap path: malformed lines and unknown shapes are
// skipped silently rather than failing the run.
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Vtk.Core.Session;

public static class SessionReader
{
    /// <summary>
    /// Extracts Bash tool invocations from one session transcript's lines.
    /// A <c>tool_use</c> (name "Bash") is joined to its <c>tool_result</c>
    /// by <c>tool_use_id</c>; the event is emitted when the result arrives,
    /// so events are ordered by outcome. Tool uses without a result, other
    /// tools, and malformed lines are dropped.
    /// </summary>
    public static List<CommandEvent> ReadCommands(IEnumerable<string> lines)
    {
        var pending = new Dictionary<string, string>(); // tool_use_id -> command
        var events = new List<CommandEvent>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }
            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                if (!doc.RootElement.TryGetProperty("message", out var msg) ||
                    msg.ValueKind != JsonValueKind.Object) continue;
                if (!msg.TryGetProperty("content", out var content) ||
                    content.ValueKind != JsonValueKind.Array) continue;
                foreach (var item in content.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object ||
                        !item.TryGetProperty("type", out var type) ||
                        type.ValueKind != JsonValueKind.String) continue;
                    switch (type.GetString())
                    {
                        case "tool_use":
                            if (item.TryGetProperty("name", out var name) &&
                                name.ValueKind == JsonValueKind.String &&
                                name.GetString() == "Bash" &&
                                item.TryGetProperty("id", out var id) &&
                                id.ValueKind == JsonValueKind.String &&
                                item.TryGetProperty("input", out var input) &&
                                input.ValueKind == JsonValueKind.Object &&
                                input.TryGetProperty("command", out var cmd) &&
                                cmd.ValueKind == JsonValueKind.String)
                            {
                                pending[id.GetString()!] = cmd.GetString()!;
                            }
                            break;
                        case "tool_result":
                            if (item.TryGetProperty("tool_use_id", out var tuid) &&
                                tuid.ValueKind == JsonValueKind.String &&
                                pending.Remove(tuid.GetString()!, out var command))
                            {
                                var isError = item.TryGetProperty("is_error", out var ie) &&
                                    ie.ValueKind == JsonValueKind.True;
                                events.Add(new CommandEvent(command, ResultText(item), isError));
                            }
                            break;
                    }
                }
            }
        }
        return events;
    }

    /// <summary>
    /// Scans a transcript's top-level <c>timestamp</c> fields and returns the
    /// session's UTC time window (first, last), or null when no line carries a
    /// parseable timestamp. Malformed lines are skipped silently, same
    /// tolerance as <see cref="ReadCommands"/>. Backs the per-session gain
    /// view (#46): only timestamps are read — never output content.
    /// </summary>
    public static (DateTime Start, DateTime End)? ReadWindow(IEnumerable<string> lines)
    {
        DateTime? start = null, end = null;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }
            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                if (!doc.RootElement.TryGetProperty("timestamp", out var ts) ||
                    ts.ValueKind != JsonValueKind.String) continue;
                if (!DateTime.TryParse(ts.GetString(), CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t))
                    continue;
                if (start is null || t < start) start = t;
                if (end is null || t > end) end = t;
            }
        }
        return start is null || end is null ? null : (start.Value, end.Value);
    }

    /// <summary>
    /// Flattens a tool_result's content: either a plain string or an array
    /// of <c>{type:"text", text}</c> blocks (both shapes occur in real
    /// transcripts). Anything else yields "".
    /// </summary>
    private static string ResultText(JsonElement result)
    {
        if (!result.TryGetProperty("content", out var c)) return "";
        if (c.ValueKind == JsonValueKind.String) return c.GetString() ?? "";
        if (c.ValueKind != JsonValueKind.Array) return "";
        var sb = new StringBuilder();
        foreach (var part in c.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.Object &&
                part.TryGetProperty("type", out var pt) &&
                pt.ValueKind == JsonValueKind.String &&
                pt.GetString() == "text" &&
                part.TryGetProperty("text", out var text) &&
                text.ValueKind == JsonValueKind.String)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(text.GetString());
            }
        }
        return sb.ToString();
    }
}
