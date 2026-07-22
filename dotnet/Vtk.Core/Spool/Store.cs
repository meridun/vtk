// Raw-output store plus per-invocation metadata log — one store, three
// queries (`vtk show`, `vtk gain`, `vtk gaps`). Port of internal/spool/spool.go.
// See docs/Architecture.md #output-spool and decision #2.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Vtk.Core.Spool;

public sealed class Store
{
    public static readonly TimeSpan DefaultTTL = TimeSpan.FromHours(1);

    private const string SpoolExt = ".txt";
    private static readonly Regex IdRe = new("^[0-9a-f]{4}$", RegexOptions.Compiled);

    private readonly string _dir;

    private Store(string dir) => _dir = dir;

    /// <summary>
    /// Resolves the user-local store directory (never inside a repo) and
    /// ensures it exists. Mirrors Go's os.UserCacheDir() on Windows, which
    /// reads the LocalAppData env var directly rather than resolving the
    /// special folder via SHGetKnownFolderPath — SpecialFolder.LocalApplicationData
    /// ignores an overridden LOCALAPPDATA env var, which would silently break
    /// test/tool isolation that relies on redirecting the store.
    /// </summary>
    public static Store Open()
    {
        var cache = OperatingSystem.IsWindows()
            ? Environment.GetEnvironmentVariable("LocalAppData")
                ?? throw new InvalidOperationException("%LocalAppData% is not defined")
            : Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } xdg
                ? xdg
                : Path.Combine(
                    Environment.GetEnvironmentVariable("HOME")
                        ?? throw new InvalidOperationException("$HOME is not defined"),
                    ".cache");
        return NewAt(Path.Combine(cache, "vtk"));
    }

    /// <summary>Opens a store rooted at dir, creating it if needed. Used directly by tests; production callers use Open.</summary>
    public static Store NewAt(string dir)
    {
        Directory.CreateDirectory(Path.Combine(dir, "spool"));
        return new Store(dir);
    }

    private string SpoolDir => Path.Combine(_dir, "spool");

    /// <summary>The spool ID for a command line: first 4 hex chars of a SHA-256 checksum. Rerunning the same command overwrites its own entry.</summary>
    public static string Id(IReadOnlyList<string> argv)
    {
        var sum = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(" ", argv)));
        return Convert.ToHexStringLower(sum)[..4];
    }

    /// <summary>Spools raw output for argv: redaction pass, provenance header, temp-file + atomic-rename write. Returns the retrieval ID.</summary>
    public string Write(IReadOnlyList<string> argv, string raw, DateTime now)
    {
        var id = Id(argv);
        var content = "# vtk spool\n# cmd: " + Redact(string.Join(" ", argv)) +
            "\n# time: " + now.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ") + "\n\n" + Redact(raw);

        var tmpName = Path.Combine(SpoolDir, id + ".tmp-" + Path.GetRandomFileName());
        File.WriteAllText(tmpName, content);
        File.Move(tmpName, Path.Combine(SpoolDir, id + SpoolExt), overwrite: true);
        return id;
    }

    /// <summary>Returns the spooled content (provenance header first) for an ID.</summary>
    public string Read(string id)
    {
        if (!IdRe.IsMatch(id))
            throw new ArgumentException($"invalid spool id \"{id}\"");
        return File.ReadAllText(Path.Combine(SpoolDir, id + SpoolExt));
    }

    /// <summary>Opportunistically deletes spool entries (and stale temp files) older than ttl. Errors are ignored: best-effort by design.</summary>
    public void Sweep(TimeSpan ttl, DateTime now)
    {
        if (!Directory.Exists(SpoolDir)) return;
        foreach (var path in Directory.EnumerateFiles(SpoolDir))
        {
            try
            {
                if (now - File.GetLastWriteTimeUtc(path) > ttl)
                    File.Delete(path);
            }
            catch
            {
                // best-effort sweep
            }
        }
    }

    private static readonly Regex AuthRe = new(@"(?i)(authorization:[ \t]*)[^\r\n]+", RegexOptions.Compiled);
    private static readonly Regex AwsRe = new(@"(?i)(AWS_SECRET[A-Z0-9_]*[ \t]*[=:][ \t]*)[^\s]+", RegexOptions.Compiled);
    private static readonly Regex PemRe = new(@"-----BEGIN [A-Z0-9 ]+-----.*?-----END [A-Z0-9 ]+-----", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>Masks obvious credential patterns: Authorization headers, AWS_SECRET* assignments, and PEM blocks.</summary>
    public static string Redact(string s)
    {
        s = AuthRe.Replace(s, "$1[REDACTED]");
        s = AwsRe.Replace(s, "$1[REDACTED]");
        s = PemRe.Replace(s, "[REDACTED PEM BLOCK]");
        return s;
    }

    // Reason values classify why an invocation was not filtered. Only
    // ReasonNoFilter entries are true coverage gaps.
    public const string ReasonNoFilter = "no-filter";
    public const string ReasonTTYBypass = "tty-bypass";
    public const string ReasonNonzeroExit = "nonzero-exit";
    public const string ReasonFilterPanic = "filter-panic";
    public const string ReasonSpoolFail = "spool-fail";

    private string MetaPath => Path.Combine(_dir, "invocations.jsonl");

    /// <summary>Appends one metadata entry. The command line is redacted before write.</summary>
    public void LogInvocation(Invocation inv)
    {
        inv = inv with { Cmd = Redact(inv.Cmd) };
        var json = JsonSerializer.Serialize(inv);
        using var fs = new FileStream(MetaPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(fs, Encoding.UTF8);
        writer.WriteLine(json);
    }

    /// <summary>Returns all logged entries, skipping malformed lines.</summary>
    public List<Invocation> Invocations()
    {
        var result = new List<Invocation>();
        if (!File.Exists(MetaPath)) return result;
        foreach (var line in File.ReadAllLines(MetaPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var inv = JsonSerializer.Deserialize<Invocation>(line);
                if (inv is not null) result.Add(inv);
            }
            catch (JsonException)
            {
                // malformed line, skip
            }
        }
        return result;
    }

    /// <summary>Aggregates true coverage gaps (no registry match) by command family, sorted by total raw bytes descending.</summary>
    public List<GapSummary> Gaps() => Aggregate(inv =>
    {
        if (inv.Filtered) return false;
        if (inv.Reason == "") return !inv.TTY; // legacy entry, pre-reason
        return inv.Reason == ReasonNoFilter;
    });

    /// <summary>Aggregates filter-panic invocations by command family.</summary>
    public List<GapSummary> Degraded() => Aggregate(inv => inv.Reason == ReasonFilterPanic);

    /// <summary>
    /// Returns coverage-gap families eligible for auto-filed intake issues
    /// (`vtk gaps --file-issues`, #34): true gaps (ReasonNoFilter) that meet
    /// both the cumulative raw-byte and call-count thresholds, excluding
    /// machine-readable invocations (--json &amp;c.) whose output is
    /// structurally uncompressable — not a filter defect, so not worth a
    /// filter issue. Sorted by raw bytes descending. Read-only over metadata
    /// (invariant 3): byte counts and redacted command families only, never
    /// output content.
    /// </summary>
    public List<GapSummary> FileIssueGaps(long minBytes, int minCalls)
    {
        var all = Aggregate(inv =>
        {
            if (inv.Filtered) return false;
            var isGap = inv.Reason == ""
                ? !inv.TTY // legacy entry, pre-reason
                : inv.Reason == ReasonNoFilter;
            return isGap && !IsMachineReadable(inv.Cmd);
        });
        return all.Where(g => g.RawBytes >= minBytes && g.Calls >= minCalls).ToList();
    }

    /// <summary>
    /// Reports whether a command line requests machine-readable (JSON)
    /// output. Such output is structurally uncompressable, so its raw bytes
    /// are a routing artifact rather than a coverage gap and must not inflate
    /// a family toward the filing threshold (#34). Conservative token match —
    /// a heuristic, deliberately narrow to avoid false positives.
    /// </summary>
    internal static bool IsMachineReadable(string cmd)
    {
        var fields = cmd.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < fields.Length; i++)
        {
            var f = fields[i];
            if (f == "--json" || f.StartsWith("--json=", StringComparison.Ordinal))
                return true;
            if (string.Equals(f, "--format=json", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(f, "--output=json", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(f, "-o=json", StringComparison.OrdinalIgnoreCase))
                return true;
            if ((f == "--format" || f == "--output" || f == "-o") &&
                i + 1 < fields.Length &&
                string.Equals(fields[i + 1], "json", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private List<GapSummary> Aggregate(Func<Invocation, bool> match)
    {
        var agg = new Dictionary<string, GapSummary>();
        foreach (var inv in Invocations())
        {
            if (!match(inv)) continue;
            var family = FirstToken(inv.Cmd);
            if (family == "") continue;
            if (!agg.TryGetValue(family, out var g))
            {
                g = new GapSummary { Family = family };
                agg[family] = g;
            }
            g.Calls++;
            g.RawBytes += inv.RawBytes;
        }
        var outList = agg.Values.ToList();
        outList.Sort((a, b) => a.RawBytes != b.RawBytes
            ? b.RawBytes.CompareTo(a.RawBytes)
            : string.CompareOrdinal(a.Family, b.Family));
        return outList;
    }

    /// <summary>Reports whether an invocation contributes real byte counts to the savings roll-up.</summary>
    private static bool Countable(Invocation inv)
    {
        if (inv.TTY) return false;
        return inv.Reason != ReasonTTYBypass;
    }

    /// <summary>Aggregates cumulative token savings from the invocation log. Backs `vtk gain`.</summary>
    public GainReport Gain()
    {
        var report = new GainReport();
        var agg = new Dictionary<string, FamilyGain>();
        foreach (var inv in Invocations())
        {
            if (!Countable(inv)) continue;
            var family = FirstToken(inv.Cmd);
            if (family == "") continue;
            report.Calls++;
            report.RawBytes += inv.RawBytes;
            report.OutBytes += inv.OutBytes;
            if (!agg.TryGetValue(family, out var g))
            {
                g = new FamilyGain { Family = family };
                agg[family] = g;
            }
            g.Calls++;
            g.RawBytes += inv.RawBytes;
            g.OutBytes += inv.OutBytes;
        }
        report.Families = agg.Values.ToList();
        report.Families.Sort((a, b) => a.Saved != b.Saved
            ? b.Saved.CompareTo(a.Saved)
            : string.CompareOrdinal(a.Family, b.Family));
        return report;
    }

    /// <summary>
    /// Aggregates countable invocations per UTC day, oldest day first. Backs
    /// `vtk gain --daily` and `--graph` (#58). Same countable gate as Gain(),
    /// so TTY/tty-bypass entries never inflate a day.
    /// </summary>
    public List<DailyGain> Daily()
    {
        var agg = new Dictionary<DateOnly, DailyGain>();
        foreach (var inv in Invocations())
        {
            if (!Countable(inv)) continue;
            var day = DateOnly.FromDateTime(inv.Time.ToUniversalTime());
            if (!agg.TryGetValue(day, out var d))
            {
                d = new DailyGain { Date = day };
                agg[day] = d;
            }
            d.Calls++;
            d.RawBytes += inv.RawBytes;
            d.OutBytes += inv.OutBytes;
        }
        var outList = agg.Values.ToList();
        outList.Sort((a, b) => a.Date.CompareTo(b.Date));
        return outList;
    }

    /// <summary>
    /// Aggregates countable invocations into agent-session time windows.
    /// Backs `vtk gain --session` (#46). Same countable gate as Gain()/
    /// Daily(). Attribution is by containment (UTC); when windows overlap,
    /// the one with the latest start wins — the most-recently-started active
    /// session, a deterministic approximation consistent with the #46/#58
    /// registry decisions. Invocations outside every window and sessions with
    /// zero attributed calls are omitted. Sorted oldest-first by start.
    /// </summary>
    public List<SessionGain> BySession(IReadOnlyList<(string Id, DateTime Start, DateTime End)> windows)
    {
        var agg = new Dictionary<string, SessionGain>();
        foreach (var inv in Invocations())
        {
            if (!Countable(inv)) continue;
            var t = inv.Time.ToUniversalTime();
            (string Id, DateTime Start, DateTime End)? best = null;
            foreach (var w in windows)
            {
                if (t < w.Start || t > w.End) continue;
                if (best is null || w.Start > best.Value.Start) best = w;
            }
            if (best is null) continue;
            if (!agg.TryGetValue(best.Value.Id, out var s))
            {
                s = new SessionGain { Id = best.Value.Id, Start = best.Value.Start, End = best.Value.End };
                agg[best.Value.Id] = s;
            }
            s.Calls++;
            s.RawBytes += inv.RawBytes;
            s.OutBytes += inv.OutBytes;
        }
        var outList = agg.Values.ToList();
        outList.Sort((a, b) => a.Start != b.Start
            ? a.Start.CompareTo(b.Start)
            : string.CompareOrdinal(a.Id, b.Id));
        return outList;
    }

    /// <summary>
    /// The last <paramref name="n"/> countable invocations in log order
    /// (oldest of the window first). Backs `vtk gain --history` (#58).
    /// </summary>
    public List<Invocation> Recent(int n)
    {
        var countable = Invocations().Where(Countable).ToList();
        return countable.Count <= n ? countable : countable[^n..];
    }

    private static string FirstToken(string cmd)
    {
        var idx = cmd.IndexOf(' ');
        return idx < 0 ? cmd : cmd[..idx];
    }
}
