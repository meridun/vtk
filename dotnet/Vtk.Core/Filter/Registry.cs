// Maps command/subcommand patterns to pure filter functions.
// Filters take raw captured output and return a compacted form; they never
// spawn processes or touch the filesystem (docs/Architecture.md invariant 4).
// Port of internal/filter/filter.go.
using System.Text.RegularExpressions;
using Vtk.Core.Filter.Toml;

namespace Vtk.Core.Filter;

/// <summary>A pure filter: raw captured output in, compacted output out. Returning the input unchanged means "nothing to elide".</summary>
public delegate string FilterFunc(string raw);

/// <summary>
/// A registered filter plus the child exit codes on which it may run. Most
/// tools only produce filterable output on success (code 0); report-style
/// tools (eslint, mocha, ...) treat a nonzero "problems found" code as a
/// report, not a failure, and declare those codes here. Exit-code parity is
/// unaffected: the allowlist gates only whether the filter runs versus raw
/// passthrough — vtk always returns the child's own code.
/// </summary>
public sealed class Entry
{
    public required FilterFunc Fn { get; init; }
    public required IReadOnlySet<int> ExitCodes { get; init; }

    /// <summary>
    /// The filter's registry identity — the exact key ("gh run") or the
    /// declarative filter's name. Telemetry records it as the invocation
    /// reason when the filter engages (#96), so `filtered=true` entries name
    /// the filter that ran. A static identifier, never output content
    /// (invariant 3).
    /// </summary>
    public string Name { get; init; } = "";

    /// <summary>Reports whether this entry is allowed to run for the given child exit code.</summary>
    public bool Filters(int code) => ExitCodes.Contains(code);
}

/// <summary>
/// Maps "cmd" or "cmd subcommand" keys to filter entries, with a
/// regex-matched fallback list for declarative TOML filters. Regex entries
/// are consulted only after the exact-key map misses, so the hand-written
/// families keep precedence and their behavior is unchanged (#40/#61).
/// </summary>
public sealed class Registry
{
    private readonly Dictionary<string, Entry> _entries = new();
    private readonly List<(Regex Re, Entry Entry)> _regexes = new();

    /// <summary>Binds a key ("git status", "ls", ...) to a filter that runs only on a clean (exit 0) child.</summary>
    public void Register(string key, FilterFunc fn) => RegisterCodes(key, fn, 0);

    /// <summary>Binds a key to a filter that runs for any of the given child exit codes.</summary>
    public void RegisterCodes(string key, FilterFunc fn, params int[] codes)
    {
        if (codes.Length == 0) codes = new[] { 0 };
        _entries[key] = new Entry { Name = key, Fn = fn, ExitCodes = new HashSet<int>(codes) };
    }

    /// <summary>
    /// Binds a regex (matched against the full command string, argv joined by
    /// spaces) to a filter running for the given child exit codes. No codes
    /// means exit 0 only. Regex entries are the declarative-filter path and
    /// are checked only after the exact-key map misses.
    /// </summary>
    public void RegisterRegex(Regex re, FilterFunc fn, params int[] codes) =>
        RegisterRegexNamed(re.ToString(), re, fn, codes);

    /// <summary>RegisterRegex with an explicit filter name for telemetry (declarative TOML filters carry their def name).</summary>
    public void RegisterRegexNamed(string name, Regex re, FilterFunc fn, params int[] codes)
    {
        if (codes.Length == 0) codes = new[] { 0 };
        _regexes.Add((re, new Entry { Name = name, Fn = fn, ExitCodes = new HashSet<int>(codes) }));
    }

    /// <summary>
    /// Matches argv against the registry: "argv[0] argv[1]" first, then bare
    /// "argv[0]", then (only if both miss) the regex fallback list against
    /// the full command string. Invocations whose second token is a flag
    /// (e.g. `git -C dir status`) miss the exact keys; a regex filter may
    /// still claim them if its pattern matches. A total miss falls through to
    /// passthrough — the gap log shows whether that pattern is worth handling.
    /// </summary>
    public bool TryLookup(IReadOnlyList<string> argv, out Entry entry)
    {
        entry = null!;
        if (argv.Count == 0) return false;
        if (argv.Count >= 2 && _entries.TryGetValue(argv[0] + " " + argv[1], out var byPair))
        {
            entry = byPair;
            return true;
        }
        if (_entries.TryGetValue(argv[0], out var byName))
        {
            entry = byName;
            return true;
        }
        var cmd = string.Join(" ", argv);
        foreach (var (re, byRegex) in _regexes)
        {
            if (re.IsMatch(cmd))
            {
                entry = byRegex;
                return true;
            }
        }
        return false;
    }

    /// <summary>mocha exits with its failure count (min(failures, 255)), so every code in 0..255 is a report, not a failure (#138).</summary>
    private static readonly int[] MochaExitCodes = Enumerable.Range(0, 256).ToArray();

    /// <summary>The registry with all shipped filter families wired in. Empty until filters are ported (task #2+).</summary>
    public static Registry Default()
    {
        var r = new Registry();
        r.Register("git status", Git.Status);
        r.Register("git log", Git.Log);
        r.Register("git diff", Git.Diff);
        r.Register("git show", Git.Show);
        r.Register("git add", Git.Add);
        r.Register("git commit", Git.Commit);
        r.Register("git push", Git.Push);
        r.Register("git pull", Git.Pull);
        r.Register("git branch", Git.Branch);
        // eslint reports "problems found" via exit 1; that output is the whole
        // point to compact. Exit 2+ is a fatal/config error and stays raw.
        r.RegisterCodes("eslint", Eslint.Filter, 0, 1);
        r.RegisterCodes("npx eslint", Eslint.Filter, 0, 1);
        // mocha's exit code is its failure count, min(failures, 255) (#138):
        // a run with N failing specs exits N, and that failing run is exactly
        // what to compact (fold passing specs, keep failures). Allow the whole
        // 0..255 range; fatal/config errors carry no "N passing/failing"
        // summary and Mocha.Filter already returns those byte-identical, so
        // content — not exit code — is the gate for the raw path.
        r.RegisterCodes("mocha", Mocha.Filter, MochaExitCodes);
        r.RegisterCodes("npx mocha", Mocha.Filter, MochaExitCodes);
        // gh list families: TSV human output on success (exit 0). `gh --json`
        // forms hit the same keys but pass through structurally intact.
        r.Register("gh issue", Gh.IssueList);
        r.Register("gh pr", Gh.PrList);
        // `gh run` dispatches by content shape: run-list tables and CI job
        // logs (`run view --log` / `--log-failed`, #96). Exit 1 is in the
        // allowlist for the report-style `--exit-status` forms, which
        // propagate a failed run's conclusion while emitting exactly the log
        // worth folding; nonzero API/usage errors have no log/table shape
        // and pass through via the shape guards.
        r.RegisterCodes("gh run", Gh.Run, 0, 1);
        // files/search family: clean-run-only (grep exit 1 = no matches = no
        // output worth compacting; find/ls nonzero exits keep raw error output).
        r.Register("ls", Files.Ls);
        r.Register("grep", Files.Grep);
        r.Register("find", Files.Find);
        // dbmate migration family (direct, and via the npm-run inner-tool
        // dispatch): status collapses the applied-migrations list to a count
        // and keeps pending + summary; up/down/migrate/rollback drop
        // progress chatter and keep result + error lines. Clean-run-only —
        // a failed migration exits nonzero and passes through raw.
        r.Register("dbmate status", Dbmate.Status);
        r.Register("dbmate up", Dbmate.Migrate);
        r.Register("dbmate down", Dbmate.Migrate);
        r.Register("dbmate migrate", Dbmate.Migrate);
        r.Register("dbmate rollback", Dbmate.Migrate);
        // Declarative TOML filters (embedded at build) register on the regex
        // path, so they coexist with the hand-written families above without
        // shadowing any exact key. The embedded defs are
        // parse/validate/fixture-checked by the TomlFilter tests; a load
        // error here would mean a corrupt binary, so skip them and keep the
        // hand-written families working rather than fail the whole registry.
        try
        {
            foreach (var d in TomlFilter.Load())
            {
                r.RegisterRegexNamed(d.Name, d.Match, d.Fn, d.ExitCodes);
            }
        }
        catch
        {
            // degrade: exact-key families stay available
        }
        return r;
    }
}
