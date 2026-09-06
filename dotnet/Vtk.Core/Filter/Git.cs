// Pure functions that compact the human-format output of high-frequency git
// subcommands. Raw output in, compact output out; returning the input
// unchanged means "nothing to elide". Port of internal/filter/git/git.go.
using System.Text;
using System.Text.RegularExpressions;

namespace Vtk.Core.Filter;

public static partial class Git
{
    [GeneratedRegex(@"^commit ([0-9a-f]{7,40})(?: \((.*)\))?")]
    private static partial Regex CommitRe();
    [GeneratedRegex(@"^diff --git a/(.+) b/(.+)$")]
    private static partial Regex DiffFileRe();
    [GeneratedRegex(@"Your branch is (ahead|behind) [^\s]+ by (\d+) commit")]
    private static partial Regex AheadRe();
    [GeneratedRegex(@"^\S.*\|\s+(\d+\s*[+-]*|Bin\b.*)$")]
    private static partial Regex StatLineRe();

    /// <summary>Compacts `git status` (human format) to a porcelain-style summary: a "## branch" header plus one short-coded line per changed file.</summary>
    public static string Status(string raw)
    {
        var branch = "";
        var track = "";
        var entries = new List<string>();
        var section = "";
        foreach (var line in raw.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith("On branch "))
                branch = t["On branch ".Length..];
            else if (t.StartsWith("HEAD detached at "))
                branch = "detached@" + t["HEAD detached at ".Length..];
            else if (AheadRe().IsMatch(t))
            {
                var m = AheadRe().Match(t);
                track = " [" + m.Groups[1].Value + " " + m.Groups[2].Value + "]";
            }
            else if (t.Contains("have diverged"))
                track = " [diverged]";
            else if (t.StartsWith("Changes to be committed"))
                section = "staged";
            else if (t.StartsWith("Changes not staged"))
                section = "unstaged";
            else if (t.StartsWith("Untracked files"))
                section = "untracked";
            else if (t.StartsWith("Unmerged paths"))
                section = "unmerged";
            else if (line.StartsWith('\t') && t != "" && section != "")
                entries.Add(StatusEntry(t, section));
        }
        if (branch == "" && entries.Count == 0)
            return raw; // unrecognized shape: pass through unchanged

        var head = "## " + branch + track;
        if (raw.Contains("nothing to commit, working tree clean"))
            return head + " clean";
        var all = new List<string> { head };
        all.AddRange(entries);
        return string.Join("\n", all);
    }

    private static string StatusEntry(string entry, string section)
    {
        string kind = "", file = entry;
        var i = entry.IndexOf(':');
        if (i >= 0)
        {
            kind = entry[..i];
            file = entry[(i + 1)..].Trim();
        }
        var c = kind switch
        {
            "new file" => "A",
            "modified" => "M",
            "deleted" => "D",
            "renamed" => "R",
            "copied" => "C",
            "typechange" => "T",
            _ => "?",
        };
        return section switch
        {
            "staged" => c + "  " + file,
            "unstaged" => " " + c + " " + file,
            "unmerged" => "UU " + file,
            _ => "?? " + file, // untracked
        };
    }

    /// <summary>Compacts default `git log` output to one line per commit: short hash, decorations if present, subject.</summary>
    public static string Log(string raw)
    {
        var outLines = new List<string>();
        var cur = "";
        var haveSubject = false;
        foreach (var line in raw.Split('\n'))
        {
            var m = CommitRe().Match(line);
            if (m.Success)
            {
                if (cur != "") outLines.Add(cur);
                var h = m.Groups[1].Value;
                if (h.Length > 7) h = h[..7];
                cur = h;
                if (m.Groups[2].Success && m.Groups[2].Value != "")
                    cur += " (" + m.Groups[2].Value + ")";
                haveSubject = false;
                continue;
            }
            if (cur != "" && !haveSubject && line.StartsWith("    ") && line.Trim() != "")
            {
                cur += " " + line.Trim();
                haveSubject = true;
            }
        }
        if (cur != "") outLines.Add(cur);
        return outLines.Count == 0 ? raw : string.Join("\n", outLines);
    }

    private sealed class FileStat
    {
        public string Name = "";
        public int Add;
        public int Del;
        public bool Binary;
    }

    private static List<FileStat> DiffStats(string raw)
    {
        var stats = new List<FileStat>();
        FileStat? cur = null;
        foreach (var line in raw.Split('\n'))
        {
            var m = DiffFileRe().Match(line);
            if (m.Success)
            {
                cur = new FileStat { Name = m.Groups[2].Value };
                stats.Add(cur);
                continue;
            }
            if (cur is null) continue;
            if (line.StartsWith("Binary files"))
                cur.Binary = true;
            else if (line.StartsWith("+++") || line.StartsWith("---"))
            {
                // hunk header noise, dropped
            }
            else if (line.StartsWith('+'))
                cur.Add++;
            else if (line.StartsWith('-'))
                cur.Del++;
        }
        return stats;
    }

    private static List<string> FormatStats(List<FileStat> stats)
    {
        var outLines = new List<string>();
        int ta = 0, td = 0;
        foreach (var s in stats)
        {
            if (s.Binary)
            {
                outLines.Add(s.Name + " | bin");
                continue;
            }
            outLines.Add($"{s.Name} | +{s.Add} -{s.Del}");
            ta += s.Add;
            td += s.Del;
        }
        if (stats.Count > 1)
            outLines.Add($"{stats.Count} files, +{ta} -{td}");
        return outLines;
    }

    /// <summary>
    /// Compacts `git diff` to a per-file +/- summary with a total line — only
    /// at or above <see cref="Fold.FloorBytes"/> (#135, shared #93/#131
    /// floor). Hunk output below the floor is the load-bearing case (agents
    /// run `git diff` to read the hunks and recovered 93% of the folds via
    /// `vtk show`), so it is returned unchanged; the runner treats that as a
    /// byte-identical passthrough logged under this entry, never a gap.
    /// </summary>
    public static string Diff(string raw)
    {
        var stats = DiffStats(raw);
        if (stats.Count == 0) return raw; // no unified-diff headers (e.g. --stat output): pass through
        if (raw.Length < Fold.FloorBytes) return raw; // hunks below the fold floor stay inline (#135)
        return string.Join("\n", FormatStats(stats));
    }

    /// <summary>
    /// Compacts `git show` to "hash subject" plus the diff summary. Hunk-shaped
    /// output below <see cref="Fold.FloorBytes"/> is returned unchanged (#135,
    /// same floor as <see cref="Diff"/>): agents recovered 77% of those folds.
    /// Header-only shapes (`--stat`, `-s`) carry no hunks and compact as before.
    /// </summary>
    public static string Show(string raw)
    {
        var head = "";
        foreach (var line in raw.Split('\n'))
        {
            var m = CommitRe().Match(line);
            if (m.Success && head == "")
            {
                var h = m.Groups[1].Value;
                if (h.Length > 7) h = h[..7];
                head = h;
                continue;
            }
            if (head != "" && line.StartsWith("    ") && line.Trim() != "")
            {
                head += " " + line.Trim();
                break;
            }
        }
        var stats = DiffStats(raw);
        if (head == "" && stats.Count == 0) return raw;
        if (stats.Count > 0 && raw.Length < Fold.FloorBytes) return raw; // hunks below the fold floor stay inline (#135)
        var outLines = new List<string>();
        if (head != "") outLines.Add(head);
        outLines.AddRange(FormatStats(stats));
        return string.Join("\n", outLines);
    }

    /// <summary>Drops line-ending conversion warnings from `git add`; anything else (errors, unusual warnings) is kept verbatim.</summary>
    public static string Add(string raw)
    {
        var outLines = new List<string>();
        foreach (var line in raw.Split('\n'))
        {
            var t = line.Trim();
            if (t == "") continue;
            if (t.Contains("LF will be replaced by CRLF") ||
                t.Contains("CRLF will be replaced by LF") ||
                t.StartsWith("warning: in the working copy of"))
                continue;
            outLines.Add(t);
        }
        return string.Join("\n", outLines);
    }

    /// <summary>Keeps the "[branch hash] subject" line and the change summary, dropping per-file create/delete/rewrite mode chatter.</summary>
    public static string Commit(string raw)
    {
        var outLines = new List<string>();
        foreach (var line in raw.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith('['))
                outLines.Add(t);
            else if (t.Contains("file changed") || t.Contains("files changed"))
                outLines.Add(t);
        }
        return outLines.Count == 0 ? raw : string.Join("\n", outLines);
    }

    private static readonly string[] TransferNoise =
    {
        "Enumerating objects", "Counting objects", "Compressing objects",
        "Writing objects", "Receiving objects", "Resolving deltas",
        "Delta compression", "Unpacking objects", "Total ",
        "remote: Enumerating", "remote: Counting", "remote: Compressing",
        "remote: Resolving", "remote: Total",
    };

    private static bool IsTransferNoise(string t) => TransferNoise.Any(t.StartsWith);

    /// <summary>Drops object-transfer progress chatter, keeping the destination, ref-update lines, rejections, and errors.</summary>
    public static string Push(string raw)
    {
        var outLines = new List<string>();
        foreach (var line in raw.Split('\n'))
        {
            var t = line.Trim();
            if (t == "" || IsTransferNoise(t)) continue;
            outLines.Add(t);
        }
        return string.Join("\n", outLines);
    }

    /// <summary>Drops transfer progress, the "From ..." line, and per-file diffstat lines, keeping the update range, strategy, and change summary.</summary>
    public static string Pull(string raw)
    {
        var outLines = new List<string>();
        foreach (var line in raw.Split('\n'))
        {
            var t = line.Trim();
            if (t == "" || IsTransferNoise(t)) continue;
            if (t.StartsWith("From ")) continue;
            if (t.StartsWith("create mode ") || t.StartsWith("delete mode ")) continue;
            if (StatLineRe().IsMatch(t) && !t.Contains("changed")) continue;
            outLines.Add(t);
        }
        return string.Join("\n", outLines);
    }

    /// <summary>Joins the branch list onto a single line; the current branch keeps its "*" marker.</summary>
    public static string Branch(string raw)
    {
        var items = new List<string>();
        foreach (var line in raw.Split('\n'))
        {
            var t = line.Trim();
            if (t == "") continue;
            items.Add(t);
        }
        return string.Join(", ", items);
    }
}
