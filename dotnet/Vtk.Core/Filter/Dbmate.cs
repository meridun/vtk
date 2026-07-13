// The dbmate filter family: pure functions that compact the human-format
// output of dbmate migration commands. Raw output in, compact output out;
// returning the input unchanged means "nothing to elide".
//
// The dominant noise source is the full applied-migrations list in `dbmate
// status`, which is almost entirely "[X] <name>" lines. The signal is the
// pending list and the applied/pending counts. The up/rollback/migrate
// commands emit a transient "Applying:"/"Rolling back:" progress line per
// migration alongside the durable result line; only the result (and any
// error) carries information.
//
// Everything here is pure: no process spawning, no filesystem access.
// Port of internal/filter/dbmate/dbmate.go.
namespace Vtk.Core.Filter;

public static class Dbmate
{
    /// <summary>
    /// Compacts `dbmate status`: applied migrations ("[X] ...") collapse to a
    /// single "[X] Applied: N" count line, pending migrations ("[ ] ...")
    /// are kept verbatim, and the trailing summary is preserved.
    /// Output whose shape is not recognized (no status entries) passes
    /// through unchanged.
    /// </summary>
    public static string Status(string raw)
    {
        var outLines = new List<string>();
        var applied = 0;
        var sawEntry = false;
        foreach (var line in raw.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith("[X]"))
            {
                applied++;
                sawEntry = true;
                // collapse: emit the count line once, in place of the first
                // applied entry, so ordering (applied block then pending) holds.
                if (applied == 1) outLines.Add(""); // placeholder, filled in below
            }
            else if (t.StartsWith("[ ]"))
            {
                sawEntry = true;
                outLines.Add(t);
            }
            else
            {
                // blank lines and the "Applied: N"/"Pending: N" summary lines.
                outLines.Add(t);
            }
        }
        if (!sawEntry) return raw; // unrecognized shape: pass through unchanged

        // Fill the collapsed-applied placeholder now that the total is
        // known. If there were no applied migrations there is no
        // placeholder to fill.
        if (applied > 0)
        {
            for (var i = 0; i < outLines.Count; i++)
            {
                if (outLines[i] == "")
                {
                    outLines[i] = "[X] Applied: " + applied;
                    break;
                }
            }
        }
        return string.Join("\n", TrimTrailingBlanks(outLines));
    }

    /// <summary>
    /// Compacts `dbmate up`/`down`/`migrate`/`rollback`: drops the transient
    /// progress lines ("Applying:", "Rolling back:", "Creating:") and keeps
    /// the durable result lines ("Applied:", "Rolled back:") plus any error
    /// output. Output with no recognized result or error line passes
    /// through unchanged.
    /// </summary>
    public static string Migrate(string raw)
    {
        var outLines = new List<string>();
        var kept = false;
        foreach (var line in raw.Split('\n'))
        {
            var t = line.Trim();
            if (t == "") continue;
            if (t.StartsWith("Applying:") || t.StartsWith("Rolling back:") ||
                t.StartsWith("Creating:") || t.StartsWith("Dropping:"))
            {
                continue; // transient progress chatter: drop
            }
            // result lines ("Applied:", "Rolled back:"), errors, and
            // anything else we do not recognize as pure progress: keep
            // verbatim.
            outLines.Add(t);
            kept = true;
        }
        return kept ? string.Join("\n", outLines) : raw;
    }

    /// <summary>Removes trailing empty strings so a collapsed status does not end with dangling blank lines.</summary>
    private static List<string> TrimTrailingBlanks(List<string> lines)
    {
        var end = lines.Count;
        while (end > 0 && lines[end - 1] == "") end--;
        return lines.GetRange(0, end);
    }
}
