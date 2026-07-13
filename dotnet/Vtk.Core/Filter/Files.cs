// The files/search filter family: pure functions that compact the piped
// output of `ls`, `grep`, and `find`. Raw output in, compact output out;
// returning the input unchanged means "nothing to elide".
//
// Byte savings come from capping long listings — the full output stays
// recoverable via the spool `OK <id>` line the runner emits whenever content
// was elided. Port of internal/filter/files/files.go.
using System.Text.RegularExpressions;

namespace Vtk.Core.Filter;

public static partial class Files
{
    // Maximum row width when column-packing a name list.
    private const int PackWidth = 96;
    // Caps how many list entries are shown before the "(+N more)" tail; the
    // rest live in the spool.
    private const int MaxEntries = 40;
    // Caps grep match lines shown per file.
    private const int MaxPerFile = 5;

    // A long-format mode string ("drwxr-xr-x", "-rw-r--r--", ...): `ls -l`
    // output is not a plain name list and passes through unchanged.
    [GeneratedRegex(@"^[bcdlps-][rwxsStT-]{9}")]
    private static partial Regex ModeRe();

    // A grep match line with line numbers: "path:lineno:content".
    [GeneratedRegex(@"^(.+?):(\d+):")]
    private static partial Regex GrepMatchRe();

    /// <summary>Splits raw into lines, trimming a trailing CR and dropping blank lines.</summary>
    private static List<string> NonEmptyLines(string raw)
    {
        var outLines = new List<string>();
        foreach (var rawLine in raw.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Trim() == "") continue;
            outLines.Add(line);
        }
        return outLines;
    }

    /// <summary>Column-packs entries into rows of at most PackWidth characters (two-space separated), capped at MaxEntries with a "(+N more)" tail.</summary>
    private static string PackList(List<string> entries)
    {
        var shown = entries.Count > MaxEntries ? entries.Take(MaxEntries).ToList() : entries;
        var rows = new List<string>();
        var cur = "";
        foreach (var e in shown)
        {
            if (cur == "")
                cur = e;
            else if (cur.Length + 2 + e.Length <= PackWidth)
                cur += "  " + e;
            else
            {
                rows.Add(cur);
                cur = e;
            }
        }
        if (cur != "") rows.Add(cur);
        var n = entries.Count - shown.Count;
        if (n > 0) rows.Add($"(+{n} more)");
        return string.Join("\n", rows);
    }

    /// <summary>Compacts piped `ls` output (one entry per line) by column-packing the names and capping the list. Long-format output (`ls -l`) passes through unchanged.</summary>
    public static string Ls(string raw)
    {
        var lines = NonEmptyLines(raw);
        if (lines.Count == 0) return raw;
        foreach (var l in lines)
        {
            if (ModeRe().IsMatch(l) || l.StartsWith("total "))
                return raw; // long format: pass through unchanged
        }
        return PackList(lines);
    }

    /// <summary>
    /// Compacts `grep -rn`-style output by capping match lines per file at
    /// MaxPerFile with a per-file "(+N more)" tail. Output with no
    /// "path:lineno:content" lines at all (e.g. `grep -l` file lists) is
    /// treated as a plain name list and column-packed. Lines that match
    /// neither shape are kept verbatim.
    /// </summary>
    public static string Grep(string raw)
    {
        var lines = NonEmptyLines(raw);
        if (lines.Count == 0) return raw;

        // First pass: total match lines per file.
        var total = new Dictionary<string, int>();
        var sawMatch = false;
        foreach (var l in lines)
        {
            var m = GrepMatchRe().Match(l);
            if (m.Success)
            {
                var file = m.Groups[1].Value;
                total[file] = total.GetValueOrDefault(file) + 1;
                sawMatch = true;
            }
        }
        if (!sawMatch) return PackList(lines); // plain file list (grep -l)

        // Second pass: emit up to MaxPerFile lines per file, one tail per file.
        var seen = new Dictionary<string, int>();
        var outLines = new List<string>();
        foreach (var l in lines)
        {
            var m = GrepMatchRe().Match(l);
            if (!m.Success)
            {
                outLines.Add(l); // unrecognized shape: keep verbatim
                continue;
            }
            var file = m.Groups[1].Value;
            seen[file] = seen.GetValueOrDefault(file) + 1;
            if (seen[file] <= MaxPerFile)
                outLines.Add(l);
            else if (seen[file] == MaxPerFile + 1)
                outLines.Add($"{file}: (+{total[file] - MaxPerFile} more)");
        }
        return string.Join("\n", outLines);
    }

    /// <summary>Compacts `find` output (one path per line) by column-packing and capping the list.</summary>
    public static string Find(string raw)
    {
        var lines = NonEmptyLines(raw);
        return lines.Count == 0 ? raw : PackList(lines);
    }
}
