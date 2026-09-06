// Git global-option normalization for filter resolution (#152). Real
// traffic arrives as `git -C <dir> status`, `git -c k=v commit`,
// `git --no-pager branch`, `git --git-dir=... push`: the subcommand is not
// argv[1], so the "git <sub>" pair keys miss and the call passes through as
// a no-filter gap. Strip rewrites the argv to `git <sub> ...` for *key
// selection and aggregation only* — the executed command line is always the
// original argv, so exit-code parity and command side effects are untouched.
//
// Pure: argv in, argv out. No process spawning, no filesystem access. Which
// subcommands may engage a filter through the normalized form is dispatch
// policy and lives in Registry, not here.
namespace Vtk.Core.Filter;

public static class GitOptions
{
    // Global options that take their value as the next token.
    private static readonly HashSet<string> ValueOptions = new(StringComparer.Ordinal)
    {
        "-C", "-c", "--git-dir", "--work-tree",
    };

    // Global options that take their value inline as --opt=value.
    private static readonly string[] InlineValueOptions = { "--git-dir=", "--work-tree=" };

    // Global flags with no value.
    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal)
    {
        "--no-pager", "-P", "-p", "--paginate",
    };

    /// <summary>
    /// Strips known git global options (<c>-C &lt;dir&gt;</c>, <c>-c &lt;k=v&gt;</c>,
    /// <c>--no-pager</c>/<c>-P</c>, <c>-p</c>/<c>--paginate</c>,
    /// <c>--git-dir[=&lt;path&gt;]</c>, <c>--work-tree[=&lt;path&gt;]</c>) that
    /// sit between <c>git</c> and its subcommand, returning
    /// <c>["git", &lt;sub&gt;, ...]</c>. Returns the original argv when it is not
    /// a git invocation, nothing was stripped, an unrecognized <c>-</c> token
    /// precedes the subcommand, a value-taking option has no value, or no
    /// subcommand follows — when in doubt, don't normalize.
    /// </summary>
    public static string[] Strip(IReadOnlyList<string> argv)
    {
        var original = argv as string[] ?? argv.ToArray();
        if (argv.Count < 3 || argv[0] != "git") return original;

        var i = 1;
        while (i < argv.Count)
        {
            var tok = argv[i];
            if (Flags.Contains(tok))
            {
                i++;
            }
            else if (ValueOptions.Contains(tok))
            {
                if (i + 1 >= argv.Count) return original; // option without its value
                i += 2;
            }
            else if (InlineValueOptions.Any(p => tok.StartsWith(p, StringComparison.Ordinal)))
            {
                i++;
            }
            else if (tok.StartsWith('-'))
            {
                return original; // unknown global option: don't guess
            }
            else
            {
                break; // the subcommand
            }
        }
        if (i == 1 || i >= argv.Count) return original;

        var result = new string[argv.Count - i + 1];
        result[0] = argv[0];
        for (var k = i; k < argv.Count; k++) result[k - i + 1] = argv[k];
        return result;
    }
}
