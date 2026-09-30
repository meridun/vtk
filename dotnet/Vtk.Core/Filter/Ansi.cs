// Shared ANSI escape-sequence stripping for filters that must recognize
// colored tool output (#163). Tools that honor a `color: true` config —
// mocha with `.mocharc.json` "color": true is the motivating case — wrap
// their summary lines in SGR sequences even on a pipe, so any regex
// anchored on the line's visible text misses them. Filters strip before
// matching; whether the emitted compact view keeps the codes is each
// filter's call (the spool always keeps the raw bytes either way).
using System.Text.RegularExpressions;

namespace Vtk.Core.Filter;

public static partial class Ansi
{
    // CSI sequences (colors, cursor moves): ESC '[' parameters, intermediates,
    // final byte. Same form as the TOML engine's strip_ansi.
    [GeneratedRegex(@"\x1b\[[0-9;?]*[ -/]*[@-~]")]
    private static partial Regex CsiRe();

    /// <summary>Returns <paramref name="s"/> with every CSI escape sequence removed.</summary>
    public static string Strip(string s) => s.IndexOf('\x1b') < 0 ? s : CsiRe().Replace(s, "");
}
