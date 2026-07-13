// Maps command/subcommand patterns to pure filter functions.
// Filters take raw captured output and return a compacted form; they never
// spawn processes or touch the filesystem (docs/Architecture.md invariant 4).
// Port of internal/filter/filter.go.
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

    /// <summary>Reports whether this entry is allowed to run for the given child exit code.</summary>
    public bool Filters(int code) => ExitCodes.Contains(code);
}

/// <summary>Maps "cmd" or "cmd subcommand" keys to filter entries.</summary>
public sealed class Registry
{
    private readonly Dictionary<string, Entry> _entries = new();

    /// <summary>Binds a key ("git status", "ls", ...) to a filter that runs only on a clean (exit 0) child.</summary>
    public void Register(string key, FilterFunc fn) => RegisterCodes(key, fn, 0);

    /// <summary>Binds a key to a filter that runs for any of the given child exit codes.</summary>
    public void RegisterCodes(string key, FilterFunc fn, params int[] codes)
    {
        if (codes.Length == 0) codes = new[] { 0 };
        _entries[key] = new Entry { Fn = fn, ExitCodes = new HashSet<int>(codes) };
    }

    /// <summary>
    /// Matches argv against the registry: "argv[0] argv[1]" first, then bare
    /// "argv[0]". Invocations whose second token is a flag (e.g.
    /// `git -C dir status`) intentionally miss and fall through to
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
        return false;
    }

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
        // mocha reports test failures via exit 1; that failing run is exactly
        // what to compact (fold passing specs, keep failures). Exit 2+ is a
        // mocha/config error and stays raw.
        r.RegisterCodes("mocha", Mocha.Filter, 0, 1);
        r.RegisterCodes("npx mocha", Mocha.Filter, 0, 1);
        // gh list families: TSV human output on success (exit 0). `gh --json`
        // forms hit the same keys but pass through structurally intact.
        r.Register("gh issue", Gh.IssueList);
        r.Register("gh pr", Gh.PrList);
        r.Register("gh run", Gh.RunList);
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
        return r;
    }
}
