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

    private static string FirstToken(string cmd)
    {
        var idx = cmd.IndexOf(' ');
        return idx < 0 ? cmd : cmd[..idx];
    }
}
