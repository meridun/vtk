// Transparent-launcher prefix unwrap for filter resolution (#97). Launchers
// like cross-env and npx run their trailing command unchanged, so real
// traffic arrives as `cross-env VAR=x mocha ...` and never matches the
// mocha filter. Unwrap strips those prefixes for *matching and telemetry
// attribution only* — the executed command line is always the original argv,
// so exit-code parity and command side effects are untouched.
//
// Pure: argv in, argv out. No process spawning, no filesystem access.
using System.Text.RegularExpressions;

namespace Vtk.Core.Filter;

public static class Prefix
{
    // Shell/cross-env environment assignment: NAME=value as a single token.
    private static readonly Regex EnvAssignRe = new(@"^[A-Za-z_][A-Za-z0-9_]*=", RegexOptions.Compiled);

    // npx flags that leave the trailing command transparent. Anything else
    // (e.g. -p/--package, -c/--call) changes what runs, so unwrap stops.
    private static readonly HashSet<string> NpxTransparentFlags = new(StringComparer.Ordinal)
    {
        "--yes", "-y", "--no-install",
    };

    /// <summary>
    /// Strips transparent launcher prefixes from argv until a fixpoint:
    /// `cross-env VAR=x [...] &lt;cmd&gt;`, `npx [--yes] &lt;cmd&gt;`, and bare
    /// leading `VAR=x [...] &lt;cmd&gt;` shapes all yield &lt;cmd&gt;'s argv.
    /// Degenerate shapes (nothing after the prefix, an npx flag that is not
    /// transparent) return the argv unchanged from that point — when in
    /// doubt, don't unwrap. Returns the original array when nothing was
    /// stripped.
    /// </summary>
    public static string[] Unwrap(string[] argv)
    {
        var i = 0;
        var progressed = true;
        while (progressed && i < argv.Length)
        {
            progressed = false;

            // Bare env assignments: VAR=x [VAR2=y ...] <cmd>
            while (i < argv.Length && EnvAssignRe.IsMatch(argv[i]))
            {
                i++;
                progressed = true;
            }
            if (i >= argv.Length)
            {
                // Assignments only, no command: nothing to match — keep original.
                return argv;
            }

            if (argv[i] == "cross-env")
            {
                var j = i + 1;
                while (j < argv.Length && EnvAssignRe.IsMatch(argv[j])) j++;
                if (j >= argv.Length) return argv; // no inner command: keep original
                i = j;
                progressed = true;
            }
            else if (argv[i] == "npx")
            {
                var j = i + 1;
                while (j < argv.Length && NpxTransparentFlags.Contains(argv[j])) j++;
                // Unwrap only when the next token is a plain command; an
                // unrecognized flag means npx may run something other than
                // that token, so stop with the npx form intact (the exact
                // "npx eslint"/"npx mocha" registry keys still apply).
                if (j >= argv.Length || argv[j].StartsWith('-')) break;
                i = j;
                progressed = true;
            }
        }
        return i == 0 ? argv : argv[i..];
    }
}
