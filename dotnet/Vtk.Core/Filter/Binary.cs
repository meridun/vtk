// The never-filter predicate for binary-producing commands (#171).
// `git archive` writes a tar/zip stream and `git bundle create` a pack to
// stdout (or to a file, in which case stdout is near-empty): there is no
// text to compact, so the CLI runner passes them through byte-for-byte as
// it does today and logs the distinct reason `binary`, which `vtk gaps`
// excludes the same way it excludes `spawn-fail` and `tty-bypass`. Kept
// out of Registry so `vtk discover` never reports them as covered.
//
// Pure argv inspection: no process spawning, no filesystem access. Git
// global options are normalized first (GitOptions.Strip, #152) so
// `git -C dir archive` matches too. `git cat-file` stays out: its blob
// output is not classifiable from argv alone.
namespace Vtk.Core.Filter;

public static class Binary
{
    /// <summary>
    /// Reports whether argv is a `git archive ...` or `git bundle create
    /// ...` invocation (after git global-option normalization). Any target
    /// spelling matches — `-`, a file, or `-o &lt;file&gt;` — since none of
    /// them yields filterable stdout. Every other command falls through to
    /// normal dispatch.
    /// </summary>
    public static bool Matches(IReadOnlyList<string> argv)
    {
        if (argv.Count < 2 || argv[0] != "git") return false;
        var git = GitOptions.Strip(argv);
        if (git.Length < 2) return false;
        return git[1] switch
        {
            "archive" => true,
            "bundle" => git.Length >= 3 && git[2] == "create",
            _ => false,
        };
    }
}
